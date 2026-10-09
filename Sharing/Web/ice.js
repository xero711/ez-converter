'use strict';

(() => {
  const maximumQueuedCandidates = 128;
  window.EZIceServers = Object.freeze([
    Object.freeze({ urls: 'stun:stun.l.google.com:19302' }),
    Object.freeze({ urls: 'stun:stun.cloudflare.com:3478' }),
    ...(window.EZTurnConfiguration ? [Object.freeze({
      urls: window.EZTurnConfiguration.server,
      username: window.EZTurnConfiguration.username,
      credential: window.EZTurnConfiguration.credential
    })] : [])
  ]);
  window.EZAllowRelay = Boolean(window.EZTurnConfiguration);
  if (window.EZAllowRelay) {
    const routeLabel = document.querySelector('.pill');
    if (routeLabel) routeLabel.textContent = '暗号化されたP2P通信（TURN中継を含む）';
    const footer = document.querySelector('footer');
    if (footer) footer.textContent = 'Google／CloudflareのSTUNが接続候補の確認に使われ、外部IPアドレスが見える場合があります。通常は端末間で直接転送します。直接接続できない場合は、共有元が設定したTURNサーバーを経由することがあります。ファイルはWebRTCの暗号化通信で転送されます。';
  }

  window.EZIceFailureSummary = async peerConnection => {
    const candidates = { local: new Set(), remote: new Set() };
    const labels = { host: '端末内', srflx: '外部アドレス', prflx: '接続候補', relay: '中継' };
    const stats = await peerConnection.getStats();
    for (const item of stats.values()) {
      if (item.type === 'local-candidate' && item.candidateType) candidates.local.add(item.candidateType);
      if (item.type === 'remote-candidate' && item.candidateType) candidates.remote.add(item.candidateType);
    }
    const summarize = types => types.size
      ? [...types].sort().map(type => labels[type] || 'その他').join(' / ')
      : 'なし';
    return `通信候補（自分: ${summarize(candidates.local)}、相手: ${summarize(candidates.remote)}）`;
  };

  window.EZReportIceFailure = (peerConnection, display) => {
    const message = window.EZAllowRelay
      ? 'P2P接続を確立できませんでした。TURNサーバー、NAT、ファイアウォールの設定を確認してください。'
      : '直接接続を確立できませんでした。NATやファイアウォールの設定を確認してください。中継には切り替えません。';
    return window.EZIceFailureSummary(peerConnection)
      .then(summary => { if (peerConnection.connectionState !== 'closed') display(`${message} ${summary}`); })
      .catch(() => { if (peerConnection.connectionState !== 'closed') display(message); });
  };

  window.EZRemoteIceQueue = peerConnection => {
    let remoteDescriptionReady = false;
    const pending = [];
    let operations = Promise.resolve();
    window.EZRelayCandidateBlocked = false;

    const isBlockedRelayCandidate = candidate => !window.EZAllowRelay &&
      typeof candidate?.candidate === 'string' && /\btyp\s+relay\b/i.test(candidate.candidate);

    const filterRemoteDescription = description => {
      if (window.EZAllowRelay || typeof description?.sdp !== 'string') return description;
      const lines = description.sdp.split(/\r?\n/);
      const filtered = lines.filter(line => {
        const relayCandidate = /^a=candidate:.*\btyp\s+relay\b/i.test(line);
        if (relayCandidate) window.EZRelayCandidateBlocked = true;
        return !relayCandidate;
      });
      return filtered.length === lines.length ? description : { ...description, sdp: filtered.join('\r\n') };
    };

    const enqueue = candidate => {
      const operation = operations.then(() => peerConnection.addIceCandidate(candidate));
      operations = operation.catch(() => {});
      return operation;
    };

    return {
      async setRemoteDescription(description) {
        await peerConnection.setRemoteDescription(filterRemoteDescription(description));
        const queued = pending.splice(0).map(enqueue);
        remoteDescriptionReady = true;
        await Promise.all(queued);
      },

      async addCandidate(candidate) {
        // A remote peer can include its own TURN relay candidates in SDP/ICE
        // signaling. Do not let that silently route file bytes through a relay
        // unless the sender explicitly configured and enabled TURN.
        if (isBlockedRelayCandidate(candidate)) {
          window.EZRelayCandidateBlocked = true;
          return false;
        }
        if (!remoteDescriptionReady) {
          if (pending.length >= maximumQueuedCandidates)
            throw new Error('接続候補が多すぎるため、P2P接続を停止しました。');
          pending.push(candidate);
          return true;
        }
        await enqueue(candidate);
        return true;
      }
    };
  };
})();
