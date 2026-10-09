'use strict';

const token = location.pathname.split('/').filter(Boolean).at(-1);
const message = document.querySelector('#message');
const form = document.querySelector('#unlock');
const content = document.querySelector('#content');
const list = document.querySelector('#files');
const progress = document.querySelector('#progress');
const transfer = document.querySelector('#transfer');
const downloadFolderButton = document.querySelector('#downloadFolder');
let cancelDownloadButton = document.querySelector('#cancelDownload');
if (!cancelDownloadButton) {
  cancelDownloadButton = document.createElement('button');
  cancelDownloadButton.id = 'cancelDownload';
  cancelDownloadButton.className = 'secondary';
  cancelDownloadButton.type = 'button';
  cancelDownloadButton.textContent = '受信をキャンセル';
  cancelDownloadButton.hidden = true;
  transfer.insertAdjacentElement('afterend', cancelDownloadButton);
}
let downloadZipButton = document.querySelector('#downloadZip');
if (!downloadZipButton) {
  downloadZipButton = document.createElement('button');
  downloadZipButton.id = 'downloadZip';
  downloadZipButton.type = 'button';
  downloadZipButton.textContent = 'すべてをZIPにまとめて保存';
  downloadZipButton.hidden = true;
  downloadFolderButton.insertAdjacentElement('afterend', downloadZipButton);
}
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const size = n => n >= 1073741824 ? (n / 1073741824).toFixed(2) + ' GB' :
  n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' :
  n >= 1024 ? (n / 1024).toFixed(1) + ' KB' : n + ' B';

let ws, pc, channel, remoteIce, manifestDraft, activeFile, writer, parts = [], finalizeDestination = null, cleanupDestination = null, hash, received = 0, total = 0;
let activeCompletion, activeButton, batch, zipBatch, cancellingReceive = false, inboundQueue = Promise.resolve(), inboundFailure = null, inboundGeneration = 0;
let reconnectAttempt = 0, reconnectTimer = null, stableTimer = null, reconnectStopped = false, waitingForPeerSlot = false;
let peerCloseTask = Promise.resolve(), peerStartTask = null, peerResetting = false, pendingSignals = [], resumeTransferPending = false;
let lastNativeProgressAt = 0;
const nativeSaveRequests = new Map();

window.chrome?.webview?.addEventListener('message', event => {
  const message = event.data;
  if (message?.channel !== 'ez-share-save-result' || typeof message.requestId !== 'string') return;
  const pending = nativeSaveRequests.get(message.requestId);
  if (!pending) return;
  nativeSaveRequests.delete(message.requestId);
  clearTimeout(pending.timer);
  if (message.ok) pending.resolve(message);
  else pending.reject(Error(String(message.error || '保存先への書き込みに失敗しました。')));
});

function nativeSaveRequest(action, details = {}) {
  if (!window.chrome?.webview) return Promise.reject(Error('アプリの保存機能に接続できません。'));
  const requestId = crypto.randomUUID().replaceAll('-', '');
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      nativeSaveRequests.delete(requestId);
      reject(Error('保存先への応答がありません。'));
    }, 5 * 60 * 1000);
    nativeSaveRequests.set(requestId, { resolve, reject, timer });
    try { window.chrome.webview.postMessage({ channel: 'ez-share-save', requestId, action, ...details }); }
    catch (error) {
      clearTimeout(timer);
      nativeSaveRequests.delete(requestId);
      reject(error);
    }
  });
}

function encodeBase64(bytes) {
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000)
    binary += String.fromCharCode(...bytes.subarray(offset, Math.min(offset + 0x8000, bytes.length)));
  return btoa(binary);
}

async function chooseNativeDestination(file) {
  const started = await nativeSaveRequest('begin', {
    fileId: file.id,
    fileName: file.relativePath,
    size: file.length,
    sha256: file.sha256
  });
  const token = started.token;
  return {
    writer: {
      write: async value => {
        const bytes = value instanceof Uint8Array ? value : new Uint8Array(value);
        await nativeSaveRequest('write', { token, data: encodeBase64(bytes) });
      },
      truncate: async length => { await nativeSaveRequest('truncate', { token, length }); },
      seek: async position => { await nativeSaveRequest('seek', { token, position }); },
      close: async () => { await nativeSaveRequest('complete', { token }); },
      abort: async () => { await nativeSaveRequest('abort', { token }); }
    },
    parts: null
  };
}

function reportNativeStatus(state, details = {}) {
  try { window.chrome?.webview?.postMessage({ channel: 'ez-share-status', state, ...details }); }
  catch { }
}

function show(text) {
  message.textContent = text;
  if (text.startsWith('送信元PCとの直接接続を待っています')) reportNativeStatus('waiting');
  else if (text.startsWith('P2P直接接続が確立') || text.startsWith('P2P直接接続中（') || text.startsWith('受信するファイルを選んでください'))
    reportNativeStatus('connected');
  else if (text.includes('切れました') || text.includes('再接続しています')) reportNativeStatus('disconnected');
  else if (text.includes('P2P通信でエラー') || text.includes('SHA-256検証に失敗') || text.includes('共有URLは停止'))
    reportNativeStatus('failed');
}
function sendSignal(data) { if (ws?.readyState === WebSocket.OPEN) ws.send(JSON.stringify(data)); }
function sendData(data) { if (channel?.readyState === 'open') channel.send(JSON.stringify(data)); }
function updateCancelDownloadButton() { cancelDownloadButton.hidden = !(activeFile || batch || zipBatch); }

function failActive(error, { abortTarget = true, cleanupTemporary = true } = {}) {
  if (!activeFile) return;
  const completion = activeCompletion;
  const target = writer;
  const cleanupAction = cleanupDestination;
  activeFile = null;
  activeCompletion = null;
  writer = null;
  finalizeDestination = null;
  cleanupDestination = null;
  parts = [];
  hash = null;
  received = 0;
  total = 0;
  resumeTransferPending = false;
  if (activeButton) activeButton.disabled = false;
  activeButton = null;
  const abortTask = target && abortTarget ? Promise.resolve().then(() => target.abort()).catch(() => {}) : Promise.resolve();
  if (cleanupTemporary && cleanupAction) abortTask.then(cleanupAction).catch(() => {});
  completion?.reject(error);
  updateCancelDownloadButton();
}

function closePeer(preserveActive = false) {
  peerResetting = true;
  pendingSignals.length = 0;
  const task = peerCloseTask.catch(() => {}).then(async () => {
    const closingChannel = channel, closingPeer = pc;
    try { closingChannel?.close(); closingPeer?.close(); } catch { }
    let previousQueue;
    do {
      previousQueue = inboundQueue;
      await previousQueue.catch(() => {});
      await new Promise(resolve => setTimeout(resolve, 0));
    } while (previousQueue !== inboundQueue);
    inboundGeneration++;
    cancellingReceive = false;
    if (preserveActive && activeFile) {
      inboundFailure = null;
      resumeTransferPending = true;
      if (writer) {
        try {
          await writer.truncate(received);
          await writer.seek(received);
        } catch (error) {
          failActive(Error('保存先を再開できません。もう一度保存先を選んでください。' + (error?.message ? ' ' + error.message : '')));
          inboundFailure = error;
        }
      }
    } else {
      const error = Error('P2P接続が終了しました。途中のファイルは破棄しました。');
      failActive(error);
      inboundFailure = error;
    }
    if (channel === closingChannel) channel = null;
    if (pc === closingPeer) pc = null;
    remoteIce = null;
  });
  peerCloseTask = task.catch(() => {});
  return task;
}

async function verifyDirect() {
  if (!pc) return;
  const stats = await pc.getStats();
  let pair;
  for (const item of stats.values()) {
    if (item.type === 'transport' && item.selectedCandidatePairId) pair = stats.get(item.selectedCandidatePairId);
  }
  if (!pair) return;
  const local = stats.get(pair.localCandidateId);
  const remote = stats.get(pair.remoteCandidateId);
  if (local?.candidateType === 'relay' || remote?.candidateType === 'relay') {
    if (!window.EZAllowRelay) {
      failActive(Error('直接接続を確認できませんでした。ファイルは転送していません。'));
      pc.close();
      show('中継経路が選ばれたため接続を終了しました。ファイルは中継しません。');
      return;
    }
    show('P2P接続中（設定したTURNサーバーを経由）');
    return;
  }
  show('P2P直接接続中（' + (local?.candidateType || 'peer') + ' ↔ ' + (remote?.candidateType || 'peer') + '）');
}

function startPeer() {
  if (peerStartTask) return peerStartTask;
  peerResetting = true;
  const task = (async () => {
    await closePeer(true);
    const generation = inboundGeneration;
    inboundQueue = Promise.resolve();
    inboundFailure = null;
    cancellingReceive = false;
    pc = new RTCPeerConnection({ iceServers: window.EZIceServers });
    remoteIce = window.EZRemoteIceQueue(pc);
    pc.ondatachannel = event => {
      const incomingChannel = event.channel;
      channel = incomingChannel;
      incomingChannel.binaryType = 'arraybuffer';
      incomingChannel.onopen = () => show('P2P直接接続が確立しました。共有内容を確認しています...');
      incomingChannel.onmessage = event => {
        if (channel !== incomingChannel) return;
        const data = event.data;
        inboundQueue = inboundQueue.then(async () => {
          if (generation !== inboundGeneration) return;
          if (cancellingReceive) {
            if (typeof data === 'string' && JSON.parse(data).type === 'cancelled') {
              cancellingReceive = false;
              show('受信をキャンセルしました。P2P接続は維持されています。');
            }
            return;
          }
          if (inboundFailure) return;
          if (typeof data === 'string') await handleControl(JSON.parse(data), generation);
          else await receiveChunk(data, generation);
        }).catch(error => {
          if (generation !== inboundGeneration) return;
          inboundFailure = error;
          handleReceiveFailure(error);
        });
      };
    };
    pc.onicecandidate = event => {
      if (event.candidate) sendSignal({ type: 'candidate', candidate: event.candidate.candidate, sdpMid: event.candidate.sdpMid, sdpMLineIndex: event.candidate.sdpMLineIndex });
    };
    pc.onconnectionstatechange = () => {
      if (pc?.connectionState === 'connected') verifyDirect().catch(handleReceiveFailure);
      else if (pc?.connectionState === 'failed') window.EZReportIceFailure(pc, show);
    };
    peerResetting = false;
    for (const signal of pendingSignals.splice(0)) await handlePeerSignal(signal);
  })();
  peerStartTask = task.finally(() => { peerStartTask = null; });
  return peerStartTask;
}

async function acceptOffer(offer) {
  await remoteIce.setRemoteDescription(offer);
  const answer = await pc.createAnswer();
  await pc.setLocalDescription(answer);
  sendSignal({ type: 'answer', sdp: answer.sdp });
}

async function handlePeerSignal(data) {
  if (data.type === 'offer' && pc) await acceptOffer(data);
  else if (data.type === 'answer' && pc) await remoteIce.setRemoteDescription(data);
  else if (data.type === 'candidate' && pc) await remoteIce.addCandidate({ candidate: data.candidate, sdpMid: data.sdpMid, sdpMLineIndex: data.sdpMLineIndex });
}

function queuePeerSignal(data) {
  if (peerResetting || !pc || !remoteIce) {
    if (pendingSignals.length >= 128) throw Error('接続候補が多すぎるため、P2P接続を停止しました。');
    pendingSignals.push(data);
    return;
  }
  handlePeerSignal(data).catch(handleReceiveFailure);
}

function connect() {
  if (reconnectStopped || ws && [WebSocket.CONNECTING, WebSocket.OPEN].includes(ws.readyState)) return;
  const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
  const socket = new WebSocket(scheme + '//' + location.host + '/rtc/' + encodeURIComponent(token) + '?role=guest');
  ws = socket;
  let openedAt = 0;
  socket.onopen = () => {
    openedAt = Date.now();
    waitingForPeerSlot = false;
    show('送信元PCとの直接接続を待っています...');
    clearTimeout(stableTimer);
    stableTimer = setTimeout(() => {
      if (ws === socket && socket.readyState === WebSocket.OPEN) reconnectAttempt = 0;
    }, 10000);
  };
  socket.onmessage = event => {
    if (ws !== socket) return;
    try {
      const data = JSON.parse(event.data);
      if (data.type === 'peer-ready') {
        waitingForPeerSlot = false;
        startPeer().catch(handleReceiveFailure);
      }
      else if (data.type === 'queued') {
        waitingForPeerSlot = true;
        show(`順番待ちです（受付時点で${Number(data.position) || '—'}番目）。前の受信者の転送が終わると、自動で接続します...`);
      }
      else if (data.type === 'queue-full') {
        reconnectStopped = true;
        waitingForPeerSlot = false;
        show('待機中の受信者が多いため接続できません。しばらくしてから、もう一度このURLを開いてください。');
      }
      else if (data.type === 'peer-left') {
        closePeer(true).then(() => show('送信元とのP2P接続が切れました。共有が有効なら、送信元の再接続後に転送を再開します。'))
          .catch(handleReceiveFailure);
      } else if (data.type === 'offer' || data.type === 'answer' || data.type === 'candidate') queuePeerSignal(data);
    } catch (error) { handleReceiveFailure(error); }
  };
  socket.onclose = async () => {
    if (ws !== socket) return;
    clearTimeout(stableTimer);
    await closePeer(!reconnectStopped);
    if (Date.now() - openedAt >= 10000) reconnectAttempt = 0;
    show(waitingForPeerSlot
      ? '現在ほかの受信者を処理中です。接続が空いたら自動で再接続します...'
      : '接続が切れました。受信済みデータを保護して再接続しています...');
    scheduleReconnect();
  };
  socket.onerror = () => show('接続案内へ接続できません。ネットワークを確認して再接続します...');
}

function scheduleReconnect() {
  if (reconnectStopped || reconnectTimer) return;
  const delay = Math.min(30000, 1000 * 2 ** Math.min(reconnectAttempt++, 5));
  reconnectTimer = setTimeout(async () => {
    reconnectTimer = null;
    if (reconnectStopped) return;
    try {
      const response = await fetch(location.pathname, { cache: 'no-store' });
      if (response.status === 410) {
        reconnectStopped = true;
        failActive(Error('共有URLは停止されたか、有効期限が切れました。未完了ファイルは保存されませんでした。'));
        show('共有URLは停止されたか、有効期限が切れています。');
        return;
      }
      if (!response.ok) throw Error('HTTP ' + response.status);
      connect();
    } catch {
      scheduleReconnect();
    }
  }, delay);
}

function safePathParts(path) {
  if (typeof path !== 'string' || path.length < 1 || path.length > 200 || path.startsWith('/') || path.includes('\\'))
    throw Error('保存するフォルダー名が不正です。');
  const segments = path.split('/');
  for (const segment of segments) {
    const stem = segment.split('.')[0].toUpperCase();
    if (!segment || segment === '.' || segment === '..' || segment.endsWith(' ') || segment.endsWith('.') ||
      /[<>:"|?*\x00-\x1f]/.test(segment) || /^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$/i.test(stem))
      throw Error('安全でないフォルダー名が含まれています。');
  }
  return segments;
}

async function fileWriterIn(directory, relativePath) {
  const segments = safePathParts(relativePath);
  const filename = segments.pop();
  let parent = directory;
  for (const segment of segments) parent = await parent.getDirectoryHandle(segment, { create: true });
  const exists = async name => {
    try { await parent.getFileHandle(name); return true; }
    catch (error) { if (error.name === 'NotFoundError') return false; throw error; }
  };
  let targetName = filename;
  if (await exists(targetName)) {
    const dot = filename.lastIndexOf('.');
    const stem = dot > 0 ? filename.slice(0, dot) : filename;
    const extension = dot > 0 ? filename.slice(dot) : '';
    let available = false;
    for (let index = 1; index <= 9999; index++) {
      targetName = stem + ' (' + index + ')' + extension;
      if (!await exists(targetName)) { available = true; break; }
    }
    if (!available) throw Error('同名ファイルが多すぎるため、安全な保存先を作成できません。');
  }
  const fileHandle = await parent.getFileHandle(targetName, { create: true });
  return { writer: await fileHandle.createWritable(), relativePath: [...segments, targetName].join('/') };
}

function beginManifest(data) {
  if (!Number.isInteger(data.count) || data.count < 1 || data.count > 2000) throw Error('ファイル一覧の件数が不正です。');
  manifestDraft = { name: String(data.name || '共有ファイル'), count: data.count, files: [], expiresAt: data.expiresAt, passwordRequired: data.passwordRequired };
}

function showManifest() {
  if (!manifestDraft || manifestDraft.files.length !== manifestDraft.count) throw Error('ファイル一覧を正しく受け取れませんでした。');
  for (const file of manifestDraft.files) {
    safePathParts(file.relativePath);
    if (!Number.isSafeInteger(file.length) || file.length < 0 || !/^[a-f\d]{64}$/i.test(file.sha256)) throw Error('ファイル情報が不正です。');
  }
  const resumeFile = activeFile && resumeTransferPending
    ? manifestDraft.files.find(file => file.id === activeFile.id)
    : null;
  if (activeFile && resumeTransferPending && (!resumeFile || resumeFile.length !== activeFile.length ||
      resumeFile.sha256.toLowerCase() !== activeFile.sha256.toLowerCase() || resumeFile.relativePath !== activeFile.relativePath)) {
    failActive(Error('共有内容が前回と異なるため、途中から再開できません。保存先を選び直してください。'));
  }
  content.hidden = false;
  downloadFolderButton.hidden = typeof window.showDirectoryPicker !== 'function';
  downloadZipButton.hidden = manifestDraft.files.length < 2;
  downloadFolderButton.disabled = Boolean(batch || zipBatch);
  downloadZipButton.disabled = Boolean(batch || zipBatch);
  document.querySelector('#title').textContent = manifestDraft.name + ' から受け取る';
    const expiryTime = Date.parse(manifestDraft.expiresAt);
    const expiryLabel = expiryTime > Date.UTC(9000, 0, 1) ? '停止または送信元PC終了まで' : new Date(expiryTime).toLocaleString();
    document.querySelector('#summary').textContent = manifestDraft.files.length + '件 · ' + size(manifestDraft.files.reduce((sum, file) => sum + file.length, 0)) + ' · 有効: ' + expiryLabel;
  list.replaceChildren();
  for (const file of manifestDraft.files) {
    const row = document.createElement('li');
    const details = document.createElement('div');
    const name = document.createElement('strong');
    const meta = document.createElement('span');
    const button = document.createElement('button');
    name.textContent = file.relativePath;
    meta.textContent = size(file.length);
    const isActive = activeFile?.id === file.id;
    button.textContent = isActive ? '受信中' : '保存';
    button.disabled = Boolean(batch || zipBatch) || isActive;
    if (isActive) activeButton = button;
    button.addEventListener('click', () => download(file, button));
    details.append(name, meta);
    row.append(details, button);
    list.append(row);
  }
  if (activeFile && resumeTransferPending && resumeFile) {
    resumeTransferPending = false;
    sendData({ type: 'download', fileId: resumeFile.id, offset: received });
    show('接続が戻りました。受信済みの ' + size(received) + ' から続けています: ' + resumeFile.relativePath);
    return;
  }
  show('受信するファイルを選んでください。');
}

async function chooseDestination(file) {
  if (window.EZNativeReceiveEnabled) return chooseNativeDestination(file);
  if (window.showSaveFilePicker) {
    const suggestedName = file.relativePath.split('/').at(-1).replace(/[<>:"|?*]/g, '_') || 'download.bin';
    const handle = await window.showSaveFilePicker({ suggestedName });
    return { writer: await handle.createWritable(), parts: null };
  }
  if (file.length > 1024 * 1024 && typeof navigator.storage?.getDirectory === 'function') {
    let directory, temporaryName;
    try {
      directory = await navigator.storage.getDirectory();
      const randomName = Array.from(crypto.getRandomValues(new Uint8Array(16)), byte => byte.toString(16).padStart(2, '0')).join('');
      temporaryName = 'ez-share-' + randomName + '.tmp';
      const handle = await directory.getFileHandle(temporaryName, { create: true });
      const output = await handle.createWritable();
      const cleanup = async () => {
        try { await directory.removeEntry(temporaryName); }
        catch (error) { if (error?.name !== 'NotFoundError') throw error; }
      };
      return {
        writer: output,
        parts: null,
        finalize: async () => {
          const savedFile = await handle.getFile();
          if (savedFile.size !== file.length) throw Error('一時保存したファイルのサイズが一致しません。');
          const url = URL.createObjectURL(savedFile);
          const anchor = document.createElement('a');
          anchor.href = url;
          anchor.download = file.relativePath.split('/').at(-1);
          anchor.click();
          setTimeout(() => { URL.revokeObjectURL(url); cleanup().catch(() => {}); }, 5 * 60 * 1000);
        },
        cleanup
      };
    } catch (error) {
      if (directory && temporaryName) {
        try { await directory.removeEntry(temporaryName); } catch { }
      }
      if (file.length > 512 * 1024 * 1024)
        throw Error('ブラウザーの一時保存領域を利用できません。保存先選択に対応したEdgeまたはChromeをお使いください。' + (error?.message ? ' ' + error.message : ''));
    }
  }
  if (file.length > 512 * 1024 * 1024) throw Error('このブラウザーでは512 MiBを超えるファイルを保存できません。最新のEdgeまたはChromeをお使いください。');
  return { writer: null, parts: [] };
}

function transferFile(file, destination, button = null) {
  return new Promise((resolve, reject) => {
    if (channel?.readyState !== 'open' || activeFile) { reject(Error('P2P接続中に別のファイルを受信できません。')); return; }
    activeFile = file;
    writer = destination.writer;
    parts = destination.parts || [];
    finalizeDestination = destination.finalize || null;
    cleanupDestination = destination.cleanup || null;
    hash = new EZSha256();
    received = 0;
    total = file.length;
    activeButton = button;
    activeCompletion = { resolve, reject };
    updateCancelDownloadButton();
    progress.value = 0;
    transfer.hidden = false;
    sendData({ type: 'download', fileId: file.id });
    show('送信元PCから直接受信しています: ' + file.relativePath);
  });
}

async function download(file, button) {
  if (channel?.readyState !== 'open' || activeFile || batch || zipBatch) return;
  button.disabled = true;
  try {
    await transferFile(file, await chooseDestination(file), button);
  } catch (error) {
    if (error.name !== 'AbortError') show(error.message);
    button.disabled = false;
  }
}

async function downloadAll() {
  if (channel?.readyState !== 'open' || activeFile || batch || zipBatch || !manifestDraft) return;
  downloadFolderButton.disabled = true;
  downloadZipButton.disabled = true;
  const fileButtons = [...list.querySelectorAll('button')];
  for (const button of fileButtons) button.disabled = true;
  const renamed = [];
  try {
    const directory = await window.showDirectoryPicker({ id: 'ez-converter-share', mode: 'readwrite' });
    batch = { completed: 0, total: manifestDraft.files.reduce((sum, file) => sum + file.length, 0), cancelled: false };
    updateCancelDownloadButton();
    for (let index = 0; index < manifestDraft.files.length; index++) {
      if (batch.cancelled) throw new DOMException('受信をキャンセルしました。', 'AbortError');
      const file = manifestDraft.files[index];
      show('保存先フォルダーへ転送中 (' + (index + 1) + '/' + manifestDraft.files.length + '): ' + file.relativePath);
      const destination = await fileWriterIn(directory, file.relativePath);
      if (batch.cancelled) { await destination.writer.abort().catch(() => {}); throw new DOMException('受信をキャンセルしました。', 'AbortError'); }
      if (destination.relativePath !== file.relativePath) renamed.push(destination.relativePath);
      try { await transferFile(file, { writer: destination.writer, parts: null }); }
      catch (error) { await destination.writer.abort().catch(() => {}); throw error; }
      batch.completed += file.length;
      progress.value = batch.total ? 100 * batch.completed / batch.total : 100;
      transfer.textContent = size(batch.completed) + ' / ' + size(batch.total);
    }
    progress.value = 100;
    show('共有フォルダーをすべて保存し、各ファイルの整合性を確認しました。' +
      (renamed.length ? ' 既存ファイルを保護するため' + renamed.length + '件を別名で保存しました。' : ''));
  } catch (error) {
    if (error.name !== 'AbortError') show(error.message);
  } finally {
    batch = null;
    for (const button of list.querySelectorAll('button')) button.disabled = button.dataset.saved === 'true';
    downloadFolderButton.disabled = false;
    downloadZipButton.disabled = false;
    updateCancelDownloadButton();
  }
}

const zip32Max = 0xffffffff;
const zipCrcTable = Uint32Array.from({ length: 256 }, (_, value) => {
  for (let bit = 0; bit < 8; bit++) value = value & 1 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
  return value >>> 0;
});

function updateZipCrc(crc, bytes) {
  for (const byte of bytes) crc = zipCrcTable[(crc ^ byte) & 0xff] ^ (crc >>> 8);
  return crc >>> 0;
}

function zipU16(view, offset, value) { view.setUint16(offset, value, true); }
function zipU32(view, offset, value) { view.setUint32(offset, value, true); }
function zipU64(view, offset, value) { view.setBigUint64(offset, BigInt(value), true); }

function calculateZipLength(files) {
  const encoder = new TextEncoder();
  let dataEnd = 0, centralLength = 0, needsZip64 = files.length >= 0xffff;
  for (const file of files) {
    const nameLength = encoder.encode(file.relativePath).length;
    if (nameLength > 0xffff) throw Error('ZIP内のファイル名が長すぎます。');
    const usesZip64Size = file.length >= zip32Max;
    const localOffset = dataEnd;
    dataEnd += 30 + nameLength + (usesZip64Size ? 20 : 0) + file.length + (usesZip64Size ? 24 : 16);
    const usesZip64Offset = localOffset >= zip32Max;
    const extraLength = (usesZip64Size || usesZip64Offset ? 4 : 0) + (usesZip64Size ? 16 : 0) + (usesZip64Offset ? 8 : 0);
    centralLength += 46 + nameLength + extraLength;
    needsZip64 ||= usesZip64Size || usesZip64Offset;
    if (!Number.isSafeInteger(dataEnd) || !Number.isSafeInteger(centralLength)) throw Error('ZIPの合計サイズが大きすぎます。');
  }
  needsZip64 ||= dataEnd >= zip32Max || centralLength >= zip32Max;
  const result = dataEnd + centralLength + (needsZip64 ? 98 : 22);
  if (!Number.isSafeInteger(result)) throw Error('ZIPの合計サイズが大きすぎます。');
  return result;
}

function makeZipLocalHeader(name, length) {
  const zip64 = length >= zip32Max;
  const bytes = new Uint8Array(30 + name.length + (zip64 ? 20 : 0));
  const view = new DataView(bytes.buffer);
  zipU32(view, 0, 0x04034b50); zipU16(view, 4, zip64 ? 45 : 20);
  zipU16(view, 6, 0x0808); zipU16(view, 8, 0); zipU16(view, 10, 0); zipU16(view, 12, 33);
  zipU32(view, 14, 0); zipU32(view, 18, zip64 ? zip32Max : 0); zipU32(view, 22, zip64 ? zip32Max : 0);
  zipU16(view, 26, name.length); zipU16(view, 28, zip64 ? 20 : 0); bytes.set(name, 30);
  if (zip64) { const extra = 30 + name.length; zipU16(view, extra, 1); zipU16(view, extra + 2, 16); zipU64(view, extra + 4, 0); zipU64(view, extra + 12, 0); }
  return bytes;
}

function makeZipDescriptor(entry) {
  const zip64 = entry.file.length >= zip32Max;
  const bytes = new Uint8Array(zip64 ? 24 : 16), view = new DataView(bytes.buffer);
  zipU32(view, 0, 0x08074b50); zipU32(view, 4, entry.crc);
  if (zip64) { zipU64(view, 8, entry.file.length); zipU64(view, 16, entry.file.length); }
  else { zipU32(view, 8, entry.file.length); zipU32(view, 12, entry.file.length); }
  return bytes;
}

function makeZipCentralHeader(entry) {
  const zip64Size = entry.file.length >= zip32Max, zip64Offset = entry.localOffset >= zip32Max;
  const extraDataLength = (zip64Size ? 16 : 0) + (zip64Offset ? 8 : 0);
  const extraLength = extraDataLength ? 4 + extraDataLength : 0;
  const bytes = new Uint8Array(46 + entry.name.length + extraLength), view = new DataView(bytes.buffer);
  zipU32(view, 0, 0x02014b50); zipU16(view, 4, 45); zipU16(view, 6, zip64Size || zip64Offset ? 45 : 20);
  zipU16(view, 8, 0x0808); zipU16(view, 10, 0); zipU16(view, 12, 0); zipU16(view, 14, 33);
  zipU32(view, 16, entry.crc); zipU32(view, 20, zip64Size ? zip32Max : entry.file.length); zipU32(view, 24, zip64Size ? zip32Max : entry.file.length);
  zipU16(view, 28, entry.name.length); zipU16(view, 30, extraLength); zipU16(view, 32, 0);
  zipU16(view, 34, 0); zipU16(view, 36, 0); zipU32(view, 38, 0); zipU32(view, 42, zip64Offset ? zip32Max : entry.localOffset);
  bytes.set(entry.name, 46);
  if (extraLength) {
    let offset = 46 + entry.name.length;
    zipU16(view, offset, 1); zipU16(view, offset + 2, extraDataLength); offset += 4;
    if (zip64Size) { zipU64(view, offset, entry.file.length); zipU64(view, offset + 8, entry.file.length); offset += 16; }
    if (zip64Offset) zipU64(view, offset, entry.localOffset);
  }
  return bytes;
}

function createZipBatch(destination) {
  const archive = {
    writer: destination.writer,
    parts: destination.parts || [],
    cleanup: destination.cleanup,
    entries: [],
    position: 0,
    async write(bytes) {
      if (this.writer) await this.writer.write(bytes);
      else this.parts.push(new Uint8Array(bytes).slice());
      this.position += bytes.byteLength;
    },
    async truncate(position) {
      if (!Number.isSafeInteger(position) || position < 0 || position > this.position) throw Error('ZIPの再開位置が不正です。');
      if (this.writer) { await this.writer.truncate(position); await this.writer.seek(position); }
      else {
        let remaining = position;
        const kept = [];
        for (const part of this.parts) {
          if (remaining >= part.length) { kept.push(part); remaining -= part.length; }
          else { if (remaining > 0) kept.push(part.slice(0, remaining)); break; }
        }
        this.parts = kept;
      }
      this.position = position;
    },
    async seek(position) {
      if (position !== this.position) throw Error('ZIPの再開位置を復元できません。');
      if (this.writer) await this.writer.seek(position);
    },
    async abort() {
      if (this.writer) await this.writer.abort();
      this.parts.length = 0;
    }
  };
  return archive;
}

function makeZipEntryWriter(archive, file) {
  const encoder = new TextEncoder(), name = encoder.encode(file.relativePath), localOffset = archive.position;
  const usesZip64 = file.length >= zip32Max;
  let dataOffset, written = 0, crc = 0xffffffff, closed = false;
  const ready = archive.write(makeZipLocalHeader(name, file.length)).then(() => { dataOffset = archive.position; });
  return {
    async write(value) {
      await ready;
      if (closed) throw Error('ZIP項目はすでに閉じています。');
      const bytes = new Uint8Array(value);
      crc = updateZipCrc(crc, bytes);
      await archive.write(bytes);
      written += bytes.length;
    },
    async close() {
      await ready;
      if (closed || written !== file.length) throw Error('ZIP項目の受信サイズが一致しません。');
      const entry = { file, name, localOffset, crc: (crc ^ 0xffffffff) >>> 0 };
      await archive.write(makeZipDescriptor(entry));
      archive.entries.push(entry);
      closed = true;
    },
    async truncate(length) {
      await ready;
      if (length !== written) throw Error('ZIP受信位置を安全に再開できません。');
      await archive.truncate(dataOffset + length);
    },
    async seek(length) {
      await ready;
      if (length !== written) throw Error('ZIP受信位置を安全に復元できません。');
      await archive.seek(dataOffset + length);
    },
    async abort() { await archive.abort(); }
  };
}

async function finishZipBatch(archive, destination, expectedLength) {
  const centralOffset = archive.position;
  for (const entry of archive.entries) await archive.write(makeZipCentralHeader(entry));
  const centralLength = archive.position - centralOffset;
  const needsZip64 = archive.entries.length >= 0xffff || centralOffset >= zip32Max || centralLength >= zip32Max ||
    archive.entries.some(entry => entry.file.length >= zip32Max || entry.localOffset >= zip32Max);
  if (needsZip64) {
    const endOffset = archive.position;
    const end = new Uint8Array(56), endView = new DataView(end.buffer);
    zipU32(endView, 0, 0x06064b50); zipU64(endView, 4, 44); zipU16(endView, 12, 45); zipU16(endView, 14, 45);
    zipU32(endView, 16, 0); zipU32(endView, 20, 0); zipU64(endView, 24, archive.entries.length);
    zipU64(endView, 32, archive.entries.length); zipU64(endView, 40, centralLength); zipU64(endView, 48, centralOffset);
    await archive.write(end);
    const locator = new Uint8Array(20), locatorView = new DataView(locator.buffer);
    zipU32(locatorView, 0, 0x07064b50); zipU32(locatorView, 4, 0); zipU64(locatorView, 8, endOffset); zipU32(locatorView, 16, 1);
    await archive.write(locator);
  }
  const endRecord = new Uint8Array(22), endView = new DataView(endRecord.buffer);
  zipU32(endView, 0, 0x06054b50); zipU16(endView, 4, 0); zipU16(endView, 6, 0);
  zipU16(endView, 8, needsZip64 ? 0xffff : archive.entries.length); zipU16(endView, 10, needsZip64 ? 0xffff : archive.entries.length);
  zipU32(endView, 12, needsZip64 ? zip32Max : centralLength); zipU32(endView, 16, needsZip64 ? zip32Max : centralOffset); zipU16(endView, 20, 0);
  await archive.write(endRecord);
  if (archive.position !== expectedLength) throw Error('作成したZIPのサイズが一致しません。');
  if (archive.writer) {
    await archive.writer.close();
    await destination.finalize?.();
  } else {
    const url = URL.createObjectURL(new Blob(archive.parts, { type: 'application/zip' }));
    const anchor = document.createElement('a');
    anchor.href = url; anchor.download = 'EZ-Share.zip'; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
  }
}

async function downloadAllZip() {
  if (channel?.readyState !== 'open' || activeFile || batch || zipBatch || !manifestDraft || manifestDraft.files.length < 2) return;
  downloadZipButton.disabled = true;
  downloadFolderButton.disabled = true;
  for (const button of list.querySelectorAll('button')) button.disabled = true;
  let destination, archive;
  try {
    const expectedLength = calculateZipLength(manifestDraft.files);
    destination = await chooseDestination({ length: expectedLength, relativePath: 'EZ-Share.zip' });
    archive = createZipBatch(destination);
    zipBatch = archive;
    archive.cancelled = false;
    updateCancelDownloadButton();
    const total = manifestDraft.files.reduce((sum, file) => sum + file.length, 0);
    let completed = 0;
    for (let index = 0; index < manifestDraft.files.length; index++) {
      if (archive.cancelled) throw new DOMException('受信をキャンセルしました。', 'AbortError');
      const file = manifestDraft.files[index];
      const entryWriter = makeZipEntryWriter(archive, file);
      show('ZIPへまとめて受信中 (' + (index + 1) + '/' + manifestDraft.files.length + '): ' + file.relativePath);
      await transferFile(file, { writer: entryWriter, parts: null, cleanup: destination.cleanup });
      completed += file.length;
      progress.value = total ? 100 * completed / total : 100;
      transfer.textContent = size(completed) + ' / ' + size(total);
    }
    await finishZipBatch(archive, destination, expectedLength);
    zipBatch = null;
    cleanupDestination = null;
    progress.value = 100;
    show('すべてのファイルをフォルダー構造付きZIPにまとめ、整合性を確認しました。');
  } catch (error) {
    if (archive) await archive.abort().catch(() => {});
    if (destination?.cleanup) await destination.cleanup().catch(() => {});
    zipBatch = null;
    cleanupDestination = null;
    if (error.name !== 'AbortError') show(error.message);
  } finally {
    for (const button of list.querySelectorAll('button')) button.disabled = button.dataset.saved === 'true';
    downloadFolderButton.disabled = false;
    downloadZipButton.disabled = false;
    updateCancelDownloadButton();
  }
}

function cancelActiveDownload() {
  if (!activeFile && !batch && !zipBatch) return;
  if (batch) batch.cancelled = true;
  if (zipBatch) zipBatch.cancelled = true;
  const cancellingFile = Boolean(activeFile);
  if (cancellingFile) cancellingReceive = true;
  sendData({ type: 'cancel' });
  if (activeFile) {
    const isZipBatch = Boolean(zipBatch);
    failActive(new DOMException('受信をキャンセルしました。', 'AbortError'), {
      abortTarget: !batch && !isZipBatch,
      cleanupTemporary: !isZipBatch
    });
  }
  show('受信をキャンセルしました。' + (cancellingFile ? '送信元の停止を確認しています...' : ''));
  updateCancelDownloadButton();
}

async function receiveChunk(data, generation) {
  if (!activeFile) throw Error('受信するファイルが選択されていません。');
  const file = activeFile, destinationWriter = writer, fileHash = hash;
  const bytes = new Uint8Array(data);
  fileHash.update(bytes);
  if (destinationWriter) await destinationWriter.write(bytes);
  else parts.push(bytes);
  if (generation !== inboundGeneration || activeFile !== file) return;
  received += bytes.length;
  if (batch) {
    const completed = batch.completed + received;
    progress.value = batch.total ? 100 * completed / batch.total : 100;
    transfer.textContent = size(completed) + ' / ' + size(batch.total);
  } else {
    progress.value = total ? 100 * received / total : 100;
    transfer.textContent = size(received) + ' / ' + size(total);
  }
  const now = Date.now();
  if (now - lastNativeProgressAt >= 200 || received === file.length) {
    lastNativeProgressAt = now;
    reportNativeStatus('progress', {
      fileId: file.id,
      fileName: file.relativePath,
      completed: received,
      total: file.length,
      percent: file.length ? Math.floor(100 * received / file.length) : 100
    });
  }
  sendData({ type: 'ack', offset: received });
}

async function finishFile(data, generation) {
  const file = activeFile, destinationWriter = writer, fileParts = parts, fileHash = hash, byteCount = received;
  if (!file || data.fileId !== file.id || byteCount !== file.length ||
    fileHash.hex().toLowerCase() !== file.sha256.toLowerCase() || data.sha256.toLowerCase() !== file.sha256.toLowerCase())
    throw Error('SHA-256検証に失敗しました。ファイルを保存せず転送を停止しました。');

  if (destinationWriter) {
    await destinationWriter.close();
    if (generation !== inboundGeneration || activeFile !== file) return;
    if (finalizeDestination) await finalizeDestination();
    if (generation !== inboundGeneration || activeFile !== file) return;
    writer = null;
    finalizeDestination = null;
    cleanupDestination = null;
  } else {
    const blob = new Blob(fileParts, { type: 'application/octet-stream' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = file.relativePath.split('/').at(-1);
    anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 60000);
    parts = [];
  }
  if (generation !== inboundGeneration || activeFile !== file) return;
  sendData({ type: 'file-saved' });
  reportNativeStatus('file-saved', {
    fileId: file.id,
    fileName: file.relativePath,
    completed: byteCount,
    total: file.length,
    percent: 100
  });
  const completed = activeCompletion;
  const completedName = file.relativePath;
  if (activeButton) activeButton.dataset.saved = 'true';
  activeFile = null;
  activeCompletion = null;
  activeButton = null;
  updateCancelDownloadButton();
  show('保存して整合性を確認しました: ' + completedName);
  progress.value = 100;
  completed?.resolve();
}

async function handleControl(data, generation) {
  if (data.type === 'password-required') { form.hidden = false; show('共有パスワードを入力してください。'); return; }
  if (data.type === 'unlocked') { form.hidden = true; document.querySelector('#password').value = ''; show('パスワードを確認しました。共有内容を取得しています...'); return; }
  if (data.type === 'manifest-start') { beginManifest(data); return; }
  if (data.type === 'manifest-file' && manifestDraft) { manifestDraft.files.push(data.file); return; }
  if (data.type === 'manifest-end') { showManifest(); return; }
  if (data.type === 'file-start') {
    if (!activeFile || data.fileId !== activeFile.id || data.name !== activeFile.relativePath ||
        data.length !== activeFile.length || String(data.sha256).toLowerCase() !== activeFile.sha256.toLowerCase() ||
        !Number.isSafeInteger(data.offset) || data.offset !== received)
      throw Error('転送ファイルまたは再開位置が一致しません。保存を中止しました。');
    return;
  }
  if (data.type === 'chunk') {
    if (!activeFile || data.fileId !== activeFile.id || data.offset !== received) throw Error('受信データの順序が一致しません。');
    return;
  }
  if (data.type === 'file-end') { await finishFile(data, generation); return; }
  if (data.type === 'error') throw Error(String(data.message || '送信元でエラーが発生しました。'));
}

function handleReceiveFailure(error) {
  show(error.message || 'P2P通信でエラーが発生しました。');
  sendData({ type: 'error', message: String(error.message || '受信に失敗しました。') });
  failActive(error);
}

form.addEventListener('submit', event => {
  event.preventDefault();
  const password = document.querySelector('#password').value;
  sendData({ type: 'unlock', password });
  show('パスワードを確認しています...');
});

downloadFolderButton.addEventListener('click', downloadAll);
downloadZipButton.addEventListener('click', downloadAllZip);
cancelDownloadButton.addEventListener('click', cancelActiveDownload);
window.addEventListener('pagehide', () => {
  reconnectStopped = true;
  clearTimeout(reconnectTimer);
  clearTimeout(stableTimer);
  sendData({ type: 'cancel' });
  if (zipBatch) {
    const archive = zipBatch;
    zipBatch = null;
    archive.abort().catch(() => {}).then(() => archive.cleanup?.()).catch(() => {});
    cleanupDestination = null;
    return;
  }
  if (cleanupDestination) {
    const target = writer, cleanup = cleanupDestination;
    writer = null;
    cleanupDestination = null;
    finalizeDestination = null;
    Promise.resolve().then(() => target?.abort()).catch(() => {}).then(cleanup).catch(() => {});
  }
});
connect();
