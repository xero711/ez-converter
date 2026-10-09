'use strict';

const statusNode = document.querySelector('#status');
const senderNode = document.querySelector('#sender');
const fileList = document.querySelector('#files');
const pinForm = document.querySelector('#pin-form');
const pinInput = document.querySelector('#pin');
const pinButton = document.querySelector('#pin-button');
const bulkDownload = document.querySelector('#bulk-download');
const downloadAll = document.querySelector('#download-all');
let sessionId = sessionStorage.getItem('localsend-session') || '';

function formatSize(value) {
  if (value >= 1073741824) return (value / 1073741824).toFixed(2) + ' GB';
  if (value >= 1048576) return (value / 1048576).toFixed(2) + ' MB';
  if (value >= 1024) return (value / 1024).toFixed(1) + ' KB';
  return value + ' B';
}

async function prepare(query) {
  const response = await fetch('/api/localsend/v2/prepare-download' + (query ? '?' + query : ''), { method: 'POST', cache: 'no-store' });
  if (!response.ok) {
    const error = new Error('HTTP ' + response.status);
    error.status = response.status;
    throw error;
  }
  return response.json();
}

async function initialize(pin) {
  statusNode.textContent = '共有情報を確認しています…';
  pinButton.disabled = true;
  try {
    let data;
    if (sessionId && !pin) {
      try { data = await prepare('sessionId=' + encodeURIComponent(sessionId)); }
      catch (error) { if (error.status !== 403) throw error; sessionId = ''; sessionStorage.removeItem('localsend-session'); }
    }
    if (!data) {
      const query = new URLSearchParams();
      if (pin) query.set('pin', pin);
      data = await prepare(query.toString());
    }
    sessionId = data.sessionId;
    sessionStorage.setItem('localsend-session', sessionId);
    const entries = Object.entries(data.files);
    senderNode.textContent = data.info.alias + ' から ' + entries.length + ' 件のファイル';
    bulkDownload.hidden = entries.length < 2;
    const zipUrl = new URL('/api/localsend/v2/download-all', location.href);
    zipUrl.searchParams.set('sessionId', sessionId);
    downloadAll.href = zipUrl.toString();
    fileList.replaceChildren();
    for (const [fileId, file] of entries) {
      const item = document.createElement('li');
      const info = document.createElement('div');
      info.className = 'file-info';
      const name = document.createElement('div');
      name.className = 'file-name';
      name.textContent = file.fileName;
      const size = document.createElement('div');
      size.className = 'file-size';
      size.textContent = formatSize(file.size);
      const link = document.createElement('a');
      const url = new URL('/api/localsend/v2/download', location.href);
      url.searchParams.set('sessionId', sessionId);
      url.searchParams.set('fileId', fileId);
      link.href = url.toString();
      link.download = file.fileName.replaceAll('\\', '/').split('/').at(-1) || 'download';
      link.textContent = 'ダウンロード';
      info.append(name, size);
      item.append(info, link);
      fileList.append(item);
    }
    pinForm.style.display = 'none';
    statusNode.textContent = '受け取るファイルを選んでください。';
  } catch (error) {
    if (error.status === 401) {
      pinForm.style.display = 'flex';
      pinInput.focus();
      statusNode.textContent = 'この共有にはPINが必要です。';
    } else if (error.status === 410) {
      statusNode.textContent = '共有は停止されたか、有効期限が切れました。';
    } else if (error.status === 429) {
      statusNode.textContent = '試行回数または接続数の上限です。しばらくしてから再試行してください。';
    } else {
      statusNode.textContent = '送信元PCに接続できません。送信元の共有が有効か確認してください。';
    }
  } finally { pinButton.disabled = false; }
}

pinForm.addEventListener('submit', event => {
  event.preventDefault();
  if (!/^[0-9]{6}$/.test(pinInput.value)) { pinInput.focus(); return; }
  initialize(pinInput.value);
});

initialize('');
