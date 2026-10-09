'use strict';
const token=location.pathname.split('/').filter(Boolean).at(-1),secret=location.hash.slice(1),status=document.querySelector('#status'),title=document.querySelector('#title'),bar=document.querySelector('#progress');
history.replaceState(null,'',location.pathname);
if(!/^[a-f0-9]{64}$/i.test(secret)){status.textContent='アプリから開始した接続ではありません。EZ Converterの画面へ戻ってください。';throw Error('Host credential is missing.');}
const api='/host-api/'+encodeURIComponent(token),header={'X-EZ-Host':secret},sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let info,manifest,pc,channel,remoteIce,ws,receipt,expectedChunk,ackWaiter,transferWindow,receiveQueue=Promise.resolve(),receiveFailure=null,receiveGeneration=0,shareBusy=false,activeShareCancellation=null,incomingDraft,resumeTimer,hostReconnectTimer,hostReconnectAttempt=0,hostReconnectStopped=false,peerRecoveryTimer,peerStableTimer,peerRecoveryAttempts=0;
async function hostFetch(path,options={}){const response=await fetch(api+path,{...options,cache:'no-store',headers:{...header,...(receipt?{'X-EZ-Session':receipt.secret}:{}),...options.headers}});if(!response.ok){const error=Error(response.status===410?'このURLは停止または期限切れです。':response.status===401?'パスワードを確認してください。':response.status===429?'試行回数が多すぎます。1分後にやり直してください。':'接続エラー（'+response.status+'）');error.status=response.status;throw error;}return response;}
function show(text){status.textContent=text;if(info?.mode!=='share'||!window.chrome?.webview)return;let report=null;if(text.startsWith('相手がURLを開く'))report={state:'waiting'};else if(text.startsWith('相手がファイルを選ぶ')||text.startsWith('P2P直接接続が確立')||text.startsWith('P2P直接接続中'))report={state:'connected'};else if(text.startsWith('送信中: ')){const match=/^送信中: (.+) · (\d+)%$/.exec(text);if(match)report={state:'progress',fileName:match[1],percent:Number(match[2])};}else if(text.startsWith('送信完了: '))report={state:'file-saved',fileName:text.slice('送信完了: '.length)};else if(text.includes('復旧できません')||text.includes('P2P接続が終了')||text.includes('中継候補が選ばれた'))report={state:'disconnected'};else if(text.startsWith('送信先でエラー:'))report={state:'failed'};if(report)window.chrome.webview.postMessage({channel:'ez-share-status',...report});}
function sendSignal(data){if(ws?.readyState===WebSocket.OPEN)ws.send(JSON.stringify(data));}
function sendData(data){if(channel?.readyState==='open')channel.send(JSON.stringify(data));}
function waitAck(type){return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{if(ackWaiter?.type===type)ackWaiter=null;reject(Error('相手から応答がありません。接続を確認してください。'));},30000);ackWaiter={type,resolve:value=>{clearTimeout(timer);ackWaiter=null;resolve(value);},reject:error=>{clearTimeout(timer);ackWaiter=null;reject(error);}};});}
function discardReceipt(pending=receipt){if(!pending||receipt!==pending)return;receipt=null;clearTimeout(resumeTimer);resumeTimer=null;fetch(api+'/offers/'+encodeURIComponent(pending.id),{method:'DELETE',cache:'no-store',headers:{...header,'X-EZ-Session':pending.secret}}).catch(()=>{});}
function closePeer(preserveReceive=false){receiveGeneration++;receiveFailure=Error('P2P接続が終了しました。');transferWindow?.fail(receiveFailure);if(ackWaiter)ackWaiter.reject(receiveFailure);ackWaiter=null;if(receipt){if(preserveReceive){const pending=receipt;clearTimeout(resumeTimer);resumeTimer=setTimeout(()=>{if(receipt===pending){discardReceipt(pending);show('再開できる時間を過ぎました。URLから新しい送信を開始してください。');}},10*60*1000);}else discardReceipt();}try{channel?.close();pc?.close();}catch{}channel=null;pc=null;remoteIce=null;expectedChunk=null;shareBusy=false;}
async function verifyDirectPath(){if(!pc)return;const stats=await pc.getStats();let pair;for(const entry of stats.values())if(entry.type==='transport'&&entry.selectedCandidatePairId)pair=stats.get(entry.selectedCandidatePairId);if(!pair)return;const local=stats.get(pair.localCandidateId),remote=stats.get(pair.remoteCandidateId);if(local?.candidateType==='relay'||remote?.candidateType==='relay'){if(!window.EZAllowRelay){pc.close();show('中継候補が選ばれたため接続を終了しました。ファイルは中継しません。');return;}show('P2P接続中（設定したTURNサーバーを経由）');return;}show('P2P直接接続中（'+(local?.candidateType||'peer')+' ↔ '+(remote?.candidateType||'peer')+'）');}
function schedulePeerRecovery(connection){if(peerRecoveryTimer||peerRecoveryAttempts>=2||hostReconnectStopped)return;peerRecoveryTimer=setTimeout(()=>{peerRecoveryTimer=null;if(pc!==connection||!['disconnected','failed'].includes(connection.connectionState))return;peerRecoveryAttempts++;show('P2P接続を復旧できません。受信データを保持して接続を再確立しています...');if(ws?.readyState===WebSocket.OPEN)ws.close();},8000);}
async function sendManifest(){manifest=await(await hostFetch('/manifest')).json();sendData({type:'manifest-start',name:manifest.name,count:manifest.files.length,expiresAt:manifest.expiresAt,passwordRequired:info.passwordRequired});for(const file of manifest.files){while(channel?.bufferedAmount>256*1024)await sleep(20);sendData({type:'manifest-file',file});}sendData({type:'manifest-end'});show('相手がファイルを選ぶのを待っています。');}
async function sendResumeState(generation){const pending=receipt;if(!pending)return false;try{const state=await(await hostFetch('/offers/'+encodeURIComponent(pending.id))).json();if(generation!==receiveGeneration||receipt!==pending||channel?.readyState!=='open')return true;sendData({type:'resume-start',id:pending.id,state:state.state});for(const[fileId,offset]of Object.entries(state.offsets||{})){while(channel?.bufferedAmount>256*1024)await sleep(20);sendData({type:'resume-offset',fileId,offset});}sendData({type:'resume-end'});if(state.state==='pending'){show('受信許可を待っています。許可後、切断位置から再開します。');monitorIncomingOffer(pending,generation).catch(error=>{if(generation===receiveGeneration&&receipt===pending)show(error.message);});}else if(state.state==='accepted'){sendData({type:'accepted',id:pending.id});show('再接続しました。受信位置を確認しています。');}else if(state.state==='completed'){clearTimeout(resumeTimer);resumeTimer=null;receipt=null;sendData({type:'done'});}else if(state.state==='rejected'){clearTimeout(resumeTimer);resumeTimer=null;receipt=null;sendData({type:'rejected',message:state.error||'受信が許可されませんでした。'});}else{discardReceipt(pending);sendData({type:'request-files',name:info.name});}return true;}catch(error){if(generation===receiveGeneration&&receipt===pending){discardReceipt(pending);sendData({type:'request-files',name:info.name});show('前回の転送状態を再開できません。新しい送信を開始してください。');}return true;}}
async function startPeer(){const previousReceiveQueue=receiveQueue;closePeer(Boolean(receipt));await previousReceiveQueue.catch(()=>{});const generation=receiveGeneration;receiveQueue=Promise.resolve();receiveFailure=null;transferWindow=window.EZTransferWindow();const flow=transferWindow;pc=new RTCPeerConnection({iceServers:window.EZIceServers});remoteIce=window.EZRemoteIceQueue(pc);channel=pc.createDataChannel('ez-transfer',{ordered:true});channel.binaryType='arraybuffer';channel.onopen=()=>{show('P2P直接接続が確立しました。');if(info.mode==='receive'){if(receipt)sendResumeState(generation);else sendData({type:'request-files',name:info.name});}else if(info.passwordRequired)sendData({type:'password-required'});else sendManifest();};channel.onclose=()=>{const error=Error('P2P接続が終了しました。');flow.fail(error);if(ackWaiter)ackWaiter.reject(error);};channel.onerror=()=>show('P2P通信でエラーが発生しました。');channel.onmessage=event=>{const data=event.data;if(info?.mode==='receive'){receiveQueue=receiveQueue.then(async()=>{if(generation!==receiveGeneration||receiveFailure)return;if(typeof data==='string')await handleDataMessage(data);else await receiveBinary(data,generation);}).catch(error=>{if(generation!==receiveGeneration)return;receiveFailure=error;show(error.message);sendData({type:'error',message:error.message});});}else if(typeof data==='string')handleDataMessage(data).catch(error=>{show(error.message);sendData({type:'error',message:error.message});});else receiveBinary(data,generation).catch(error=>{show(error.message);sendData({type:'error',message:error.message});});};pc.onicecandidate=event=>{if(event.candidate)sendSignal({type:'candidate',candidate:event.candidate.candidate,sdpMid:event.candidate.sdpMid,sdpMLineIndex:event.candidate.sdpMLineIndex});};pc.onconnectionstatechange=()=>{if(pc?.connectionState==='connected'){show('P2P直接接続が確立しました。');verifyDirectPath().catch(()=>{});}else if(pc?.connectionState==='failed')window.EZReportIceFailure(pc,show);};const offer=await pc.createOffer();await pc.setLocalDescription(offer);sendSignal({type:'offer',sdp:offer.sdp});}
const startPeerWithRecovery=startPeer;
startPeer=async function(){await startPeerWithRecovery();const connection=pc,reportState=connection.onconnectionstatechange;connection.onconnectionstatechange=()=>{if(pc!==connection)return;reportState?.();if(connection.connectionState==='connected'){clearTimeout(peerRecoveryTimer);peerRecoveryTimer=null;clearTimeout(peerStableTimer);peerStableTimer=setTimeout(()=>{if(pc===connection&&connection.connectionState==='connected')peerRecoveryAttempts=0;},30000);}else if(connection.connectionState==='failed'||connection.connectionState==='disconnected')schedulePeerRecovery(connection);};};
async function createIncomingOffer(message,generation){if(receipt)throw Error('別の送信がすでに進行中です。');if(!Array.isArray(message.files)||message.files.length<1||message.files.length>2000)throw Error('ファイル一覧が不正です。');const created=await(await hostFetch('/offers',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sender:String(message.sender||'ブラウザーのゲスト').slice(0,64),files:message.files})})).json();if(generation!==receiveGeneration||channel?.readyState!=='open'){await fetch(api+'/offers/'+encodeURIComponent(created.id),{method:'DELETE',cache:'no-store',headers:{...header,'X-EZ-Session':created.secret}}).catch(()=>{});return;}receipt=created;show('受信許可を確認しています。');monitorIncomingOffer(created,generation).catch(error=>{if(generation===receiveGeneration&&receipt===created){show(error.message);sendData({type:'error',message:error.message});receipt=null;}});}
async function monitorIncomingOffer(pending,generation){while(generation===receiveGeneration&&receipt===pending&&channel?.readyState==='open'){const state=await(await hostFetch('/offers/'+pending.id)).json();if(generation!==receiveGeneration||receipt!==pending)return;if(state.state==='accepted'){sendData({type:'accepted',id:pending.id});show('受信中です。');return;}if(state.state!=='pending'){sendData({type:'rejected',message:state.error||'受信が許可されませんでした。'});clearTimeout(resumeTimer);resumeTimer=null;receipt=null;show('受信が許可されませんでした。');return;}await sleep(700);}}
async function receiveBinary(data,generation=receiveGeneration){if(info.mode!=='receive'||!receipt||!expectedChunk)return;const chunk=expectedChunk;expectedChunk=null;if(data.byteLength!==chunk.length)throw Error('受信データの長さが一致しません。');await hostFetch('/offers/'+receipt.id+'/files/'+encodeURIComponent(chunk.fileId)+'?offset='+chunk.offset,{method:'PUT',body:data});if(generation!==receiveGeneration)return;sendData({type:'ack',offset:chunk.offset+data.byteLength});}
async function sendShareFile(fileId){if(shareBusy)throw Error('別のファイルを送信中です。');const file=manifest?.files.find(item=>item.id===fileId);if(!file)throw Error('ファイルが見つかりません。');shareBusy=true;const flow=transferWindow;try{flow.reset();const size=Math.max(16384,Math.min(48*1024,pc?.sctp?.maxMessageSize||65536));let offset=0;sendData({type:'file-start',fileId,name:file.relativePath,length:file.length,sha256:file.sha256});while(offset<file.length&&channel?.readyState==='open'){const windowEnd=Math.min(file.length,offset+flow.windowBytes);while(offset<windowEnd&&channel?.readyState==='open'){const length=Math.min(size,file.length-offset),data=await(await hostFetch('/files/'+encodeURIComponent(file.id)+'?offset='+offset+'&length='+length)).arrayBuffer();sendData({type:'chunk',fileId:file.id,offset,length});channel.send(data);offset+=length;flow.markSent(offset);while(channel.bufferedAmount>flow.windowBytes*2)await sleep(10);}if(channel?.readyState!=='open')throw Error('P2P接続が終了しました。');await flow.waitFor(windowEnd);bar.value=file.length?flow.acknowledgedOffset/file.length*100:100;show('送信中: '+file.relativePath+' · '+Math.floor(bar.value)+'%');}sendData({type:'file-end',fileId:file.id,sha256:file.sha256});await waitAck('file-saved');bar.value=100;show('送信完了: '+file.relativePath);}finally{shareBusy=false;}}
async function handleDataMessage(raw,generation=receiveGeneration){const message=JSON.parse(raw);if(message.type==='unlock'&&info.mode==='share'){await hostFetch('/authorize',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password:message.password})});sendData({type:'unlocked'});await sendManifest();return;}if(message.type==='download'&&info.mode==='share'){await sendShareFile(message.fileId);return;}if(message.type==='ack'&&info.mode==='share'){transferWindow?.acknowledge(Number(message.offset));return;}if(message.type==='file-saved'&&ackWaiter?.type==='file-saved'){ackWaiter.resolve(message);return;}if(message.type==='resume-unavailable'&&info.mode==='receive'&&receipt){discardReceipt();sendData({type:'request-files',name:info.name});show('前回の続きは見つかりません。ファイルを選び直して送信してください。');return;}if(message.type==='offer-start'&&info.mode==='receive'){incomingDraft={sender:message.sender,files:[]};return;}if(message.type==='offer-file'&&incomingDraft){incomingDraft.files.push(message.file);return;}if(message.type==='offer-end'&&incomingDraft){const draft=incomingDraft;incomingDraft=null;await createIncomingOffer(draft,generation);return;}if(message.type==='chunk'&&info.mode==='receive'){if(receipt)expectedChunk=message;return;}if(message.type==='file-complete'&&info.mode==='receive'&&receipt){const pending=receipt;await hostFetch('/offers/'+pending.id+'/files/'+encodeURIComponent(message.fileId)+'/complete',{method:'POST'});if(generation!==receiveGeneration||receipt!==pending)return;sendData({type:'file-accepted',fileId:message.fileId});return;}if(message.type==='complete'&&info.mode==='receive'&&receipt){const pending=receipt;await hostFetch('/offers/'+pending.id+'/complete',{method:'POST'});if(generation!==receiveGeneration||receipt!==pending)return;sendData({type:'done'});clearTimeout(resumeTimer);resumeTimer=null;receipt=null;show('受信したファイルを保存しました。');return;}if(message.type==='cancel'&&receipt){discardReceipt();if(generation===receiveGeneration)show('相手が送信をキャンセルしました。');return;}if(message.type==='error')show('相手側: '+String(message.message||'通信エラー'));}
sendShareFile = async function(fileId, requestedOffset = 0, generation = receiveGeneration) {
  if (generation !== receiveGeneration) return;
  if (shareBusy) throw Error('別のファイルを送信中です。');
  const file = manifest?.files.find(item => item.id === fileId);
  if (!file) throw Error('ファイルが見つかりません。');
  if (!Number.isSafeInteger(requestedOffset) || requestedOffset < 0 || requestedOffset > file.length)
    throw Error('再開位置が不正です。');
  const sourceChannel = channel, sourcePeer = pc, flow = transferWindow;
  const cancellation = new AbortController();
  const isCurrent = () => generation === receiveGeneration && channel === sourceChannel && pc === sourcePeer &&
    transferWindow === flow && sourceChannel?.readyState === 'open' && !cancellation.signal.aborted;
  if (!isCurrent()) return;
  shareBusy = true;
  activeShareCancellation = cancellation;
  try {
    flow.reset(requestedOffset);
    const chunkSize = Math.max(16384, Math.min(48 * 1024, sourcePeer?.sctp?.maxMessageSize || 65536));
    let offset = requestedOffset;
    sourceChannel.send(JSON.stringify({ type: 'file-start', fileId, name: file.relativePath,
      length: file.length, sha256: file.sha256, offset }));
    while (offset < file.length && isCurrent()) {
      const windowEnd = Math.min(file.length, offset + flow.windowBytes);
      while (offset < windowEnd && isCurrent()) {
        const length = Math.min(chunkSize, file.length - offset);
        const response = await hostFetch('/files/' + encodeURIComponent(file.id) + '?offset=' + offset + '&length=' + length,
          { signal: cancellation.signal });
        if (!isCurrent()) return;
        const data = await response.arrayBuffer();
        if (!isCurrent()) return;
        sourceChannel.send(JSON.stringify({ type: 'chunk', fileId: file.id, offset, length }));
        sourceChannel.send(data);
        offset += length;
        flow.markSent(offset);
        while (sourceChannel.bufferedAmount > flow.windowBytes * 2 && isCurrent()) await sleep(10);
      }
      if (!isCurrent()) return;
      await flow.waitFor(windowEnd);
      if (!isCurrent()) return;
      bar.value = file.length ? flow.acknowledgedOffset / file.length * 100 : 100;
      show('送信中: ' + file.relativePath + ' · ' + Math.floor(bar.value) + '%');
    }
    if (!isCurrent()) return;
    sourceChannel.send(JSON.stringify({ type: 'file-end', fileId: file.id, sha256: file.sha256 }));
    await waitAck('file-saved');
    if (!isCurrent()) return;
    bar.value = 100;
    show('送信完了: ' + file.relativePath);
  } catch (error) {
    if (isCurrent()) throw error;
  } finally {
    if (activeShareCancellation === cancellation) activeShareCancellation = null;
    if (transferWindow === flow) shareBusy = false;
  }
};

const handleDataMessageBeforeDownloadResume = handleDataMessage;
handleDataMessage = async function(raw, generation = receiveGeneration) {
  const message = JSON.parse(raw);
  if (message.type === 'download' && info.mode === 'share')
    return sendShareFile(message.fileId, message.offset ?? 0, generation);
  return handleDataMessageBeforeDownloadResume(raw, generation);
};

function cancelSharedDownload() {
  if (!activeShareCancellation) return false;
  activeShareCancellation.abort();
  const error = new DOMException('受信側がダウンロードをキャンセルしました。', 'AbortError');
  transferWindow?.fail(error);
  ackWaiter?.reject(error);
  return true;
}

const handleDataMessageBeforeDownloadCancel = handleDataMessage;
handleDataMessage = async function(raw, generation = receiveGeneration) {
  const message = JSON.parse(raw);
  if (message.type === 'cancel' && info?.mode === 'share' && shareBusy && cancelSharedDownload()) {
    while (shareBusy && generation === receiveGeneration) await sleep(10);
    if (generation === receiveGeneration && channel?.readyState === 'open') sendData({ type: 'cancelled' });
    show('相手が受信をキャンセルしました。');
    return;
  }
  try { return await handleDataMessageBeforeDownloadCancel(raw, generation); }
  catch (error) { if (error?.name !== 'AbortError') throw error; }
};

const startPeerBeforeChannelGuard = startPeer;
startPeer = async function() {
  await startPeerBeforeChannelGuard();
  const activeChannel = channel, generation = receiveGeneration, onmessage = activeChannel?.onmessage;
  if (!activeChannel || !onmessage) return;
  activeChannel.onmessage = event => {
    if (channel !== activeChannel || receiveGeneration !== generation) return;
    return onmessage.call(activeChannel, event);
  };
};

function scheduleHostReconnect(){if(hostReconnectStopped||hostReconnectTimer)return;const delay=Math.min(30000,1000*2**Math.min(hostReconnectAttempt++,5));show(receipt?'シグナリング接続が切れました。受信データを保持して自動再接続しています...':'シグナリング接続が切れました。自動再接続しています...');hostReconnectTimer=setTimeout(async()=>{hostReconnectTimer=null;try{await hostFetch('/info');connectHost();}catch(error){if(error.status===410){hostReconnectStopped=true;closePeer(false);show(error.message);return;}scheduleHostReconnect();}},delay);}
function connectHost(){if(hostReconnectStopped||ws&&[WebSocket.CONNECTING,WebSocket.OPEN].includes(ws.readyState))return;const scheme=location.protocol==='https:'?'wss:':'ws:',socket=new WebSocket(scheme+'//'+location.host+'/rtc/'+encodeURIComponent(token)+'?role=host');let openedAt=0;ws=socket;socket.onopen=()=>{openedAt=Date.now();socket.send(JSON.stringify({type:'auth',secret}));show(receipt?'シグナリングへ再接続しました。相手の再接続を待っています。':'相手がURLを開くのを待っています。ファイル本体は相手と直接転送します。');};socket.onmessage=async event=>{if(ws!==socket)return;try{const message=JSON.parse(event.data);if(message.type==='peer-ready')await startPeer();else if(message.type==='peer-left'){closePeer(true);show(receipt?'接続が切れました。10分以内に同じURLで再接続すると続きから送れます。':'相手との接続が終了しました。URLを再度開くと接続できます。');}else if(message.type==='offer'&&pc){await remoteIce.setRemoteDescription(message);const answer=await pc.createAnswer();await pc.setLocalDescription(answer);sendSignal({type:'answer',sdp:answer.sdp});}else if(message.type==='answer'&&pc)await remoteIce.setRemoteDescription(message);else if(message.type==='candidate'&&pc)await remoteIce.addCandidate({candidate:message.candidate,sdpMid:message.sdpMid,sdpMLineIndex:message.sdpMLineIndex});}catch(error){show(error.message);}};socket.onclose=event=>{if(ws!==socket)return;if(Date.now()-openedAt>=10000)hostReconnectAttempt=0;closePeer(Boolean(receipt));if(event.code===1008){hostReconnectStopped=true;closePeer(false);show('接続認証に失敗しました。共有元の画面でURLを作り直してください。');return;}scheduleHostReconnect();};socket.onerror=()=>show('接続案内へ接続できません。ネットワークを確認しています...');}
async function start(){if(!token)throw Error('接続情報が不正です。');info=await(await hostFetch('/info')).json();title.textContent=info.mode==='share'?'ブラウザー受信を待機中':'ブラウザーからの受信を待機中';connectHost();}
start().catch(error=>show(error.message));
