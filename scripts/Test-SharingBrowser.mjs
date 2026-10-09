import fs from 'node:fs';
import vm from 'node:vm';
import crypto from 'node:crypto';
const hashScript = fs.readFileSync(new URL('../Sharing/Web/sha256.js', import.meta.url), 'utf8');
const hashContext = { window: {} };
vm.runInNewContext(hashScript, hashContext);
const Sha256 = hashContext.window.EZSha256;
for (const length of [0, 1, 3, 55, 56, 63, 64, 65, 1024, 4194321]) {
  const data = crypto.randomBytes(length);
  const hasher = new Sha256();
  for (let offset = 0; offset < data.length; offset += 113) hasher.update(data.subarray(offset, offset + 113));
  const actual = hasher.hex();
  const expected = crypto.createHash('sha256').update(data).digest('hex');
  if (actual !== expected) throw Error(`Browser SHA-256 failed at length ${length}`);
  console.log(`PASS browser streaming SHA-256 length=${length}`);
}
for (const name of ['host', 'share', 'invite']) {
  const source = fs.readFileSync(new URL(`../Sharing/Web/${name}.js`, import.meta.url), 'utf8');
  new vm.Script(source);
  if (name === 'host' && !source.includes("response.status===429?'試行回数が多すぎます。1分後にやり直してください。'"))
    throw new Error('The host browser must explain the password-guessing cooldown.');
  console.log(`PASS ${name} browser script syntax`);
}

const iceSource = fs.readFileSync(new URL('../Sharing/Web/ice.js', import.meta.url), 'utf8');
new vm.Script(iceSource);
const iceContext = { window: {} };
vm.runInNewContext(iceSource, iceContext);
const iceUrls = iceContext.window.EZIceServers.flatMap(server => Array.isArray(server.urls) ? server.urls : [server.urls]);
if (iceUrls.length !== 2 || !iceUrls.every(url => /^stun:/i.test(url)) ||
    !iceUrls.includes('stun:stun.l.google.com:19302') || !iceUrls.includes('stun:stun.cloudflare.com:3478'))
  throw new Error('Direct ICE must use both configured STUN providers and must not use TURN relays.');
console.log('PASS direct ICE uses independent Google and Cloudflare STUN endpoints only');

const turnContext = {
  window: { EZTurnConfiguration: { server: 'turn:turn.example.invalid:3478', username: 'temporary-user', credential: 'temporary-password' } },
  document: { querySelector: () => null }
};
vm.runInNewContext(iceSource, turnContext);
const turnServers = turnContext.window.EZIceServers;
if (!turnContext.window.EZAllowRelay || turnServers.length !== 3 ||
    !turnServers.some(server => server.urls === 'turn:turn.example.invalid:3478' && server.username === 'temporary-user' && server.credential === 'temporary-password'))
  throw new Error('Explicit TURN configuration was not added to the shared ICE settings.');
console.log('PASS explicit short-lived TURN configuration is shared by browser peers');

for (const name of ['host', 'share', 'invite']) {
  const source = fs.readFileSync(new URL(`../Sharing/Web/${name}.js`, import.meta.url), 'utf8');
  if (!/iceServers\s*:\s*window\.EZIceServers/.test(source))
    throw new Error(`${name} peer connection does not use the shared ICE configuration.`);
  if (/\bturns?:/i.test(source))
    throw new Error(`${name} peer connection must not configure a TURN relay.`);
  if (!/selectedCandidatePairId/.test(source) ||
      !/local\?\.candidateType\s*===\s*['"]relay['"]/.test(source) ||
      !/remote\?\.candidateType\s*===\s*['"]relay['"]/.test(source))
    throw new Error(`${name} peer connection does not reject a relay candidate pair.`);
  if (!source.includes('EZAllowRelay'))
    throw new Error(`${name} peer connection does not require explicit TURN opt-in before using a relay candidate.`);
  if (!source.includes('EZReportIceFailure'))
    throw new Error(`${name} peer connection does not report ICE diagnostics on failure.`);
  console.log(`PASS ${name} WebRTC transfers reject relays by default and permit configured TURN only`);
}
const senderScript = fs.readFileSync(new URL('../Sharing/Web/host.js', import.meta.url), 'utf8');
const guestScript = fs.readFileSync(new URL('../Sharing/Web/invite.js', import.meta.url), 'utf8');
const receiverScript = fs.readFileSync(new URL('../Sharing/Web/share.js', import.meta.url), 'utf8');
if (!senderScript.includes('flow.waitFor(windowEnd)') || !guestScript.includes('context.flow.waitFor(windowEnd)') ||
    !receiverScript.includes('inboundQueue = inboundQueue.then') || !senderScript.includes('receiveQueue=receiveQueue.then'))
  throw new Error('P2P chunk pipeline or ordered receive queue is missing.');
console.log('PASS both P2P directions pipeline bounded chunks and serialize durable receiver writes');
if (!senderScript.includes('sendResumeState(generation)') || !guestScript.includes('continueResume(draft)') ||
    !guestScript.includes('context.flow.reset(offset)') || !guestScript.includes('resumeBatch'))
  throw new Error('P2P upload resume state or byte-offset restoration is missing.');
console.log('PASS interrupted browser-to-PC uploads retain the selected files and resume from durable receiver offsets');

const flowSource = fs.readFileSync(new URL('../Sharing/Web/transfer-window.js', import.meta.url), 'utf8');
new vm.Script(flowSource);
const flowContext = { window: {}, setTimeout, clearTimeout };
vm.runInNewContext(flowSource, flowContext);
const flow = flowContext.window.EZTransferWindow();
const chunkSize = 48 * 1024;
for (let offset = chunkSize; offset <= flow.windowBytes; offset += chunkSize) flow.markSent(offset);
let windowResolved = false;
const windowAck = flow.waitFor(flow.windowBytes).then(() => { windowResolved = true; });
flow.acknowledge(flow.windowBytes / 2);
await Promise.resolve();
if (windowResolved) throw new Error('Transfer window released before its cumulative acknowledgement.');
flow.acknowledge(flow.windowBytes);
await windowAck;
if (!windowResolved) throw new Error('Transfer window did not release after the cumulative acknowledgement.');
try {
  flow.acknowledge(flow.windowBytes + 1);
  throw new Error('Transfer window accepted an acknowledgement beyond sent data.');
} catch (error) {
  if (!error.message.includes('exceeds sent data')) throw error;
}
console.log('PASS bounded multi-chunk window waits for cumulative durable acknowledgement');

let finishDescription;
let finishFirstCandidate;
let firstCandidateStarted;
const firstCandidateStartedPromise = new Promise(resolve => { firstCandidateStarted = resolve; });
const acceptedCandidates = [];
const peerConnection = {
  remoteDescription: null,
  async setRemoteDescription(description) {
    await new Promise(resolve => { finishDescription = resolve; });
    this.remoteDescription = description;
  },
  async addIceCandidate(candidate) {
    if (!this.remoteDescription) throw new Error('Candidate arrived before remote description.');
    if (candidate.candidate === 'candidate-1') {
      firstCandidateStarted();
      await new Promise(resolve => { finishFirstCandidate = resolve; });
    }
    acceptedCandidates.push(candidate.candidate);
  }
};
const iceQueue = iceContext.window.EZRemoteIceQueue(peerConnection);
await iceQueue.addCandidate({ candidate: 'candidate-1' });
const setDescription = iceQueue.setRemoteDescription({ type: 'answer', sdp: 'test' });
await iceQueue.addCandidate({ candidate: 'candidate-2' });
finishDescription();
await firstCandidateStartedPromise;
const liveCandidate = iceQueue.addCandidate({ candidate: 'candidate-3' });
finishFirstCandidate();
await Promise.all([setDescription, liveCandidate]);
if (acceptedCandidates.join(',') !== 'candidate-1,candidate-2,candidate-3')
  throw new Error(`ICE candidate order changed: ${acceptedCandidates.join(',')}`);
console.log('PASS early and concurrent ICE candidates wait for remote SDP and preserve order');

const overflowQueue = iceContext.window.EZRemoteIceQueue({
  async setRemoteDescription() {},
  async addIceCandidate() {}
});
for (let index = 0; index < 128; index++) await overflowQueue.addCandidate({ candidate: `candidate-${index}` });
try {
  await overflowQueue.addCandidate({ candidate: 'overflow' });
  throw new Error('ICE candidate queue accepted more than its safety limit.');
} catch (error) {
  if (!error.message.includes('多すぎる')) throw error;
}
console.log('PASS ICE candidate queue has a bounded size');

let displayedFailure = '';
const fakeFailedPeer = {
  connectionState: 'failed',
  async getStats() {
    return new Map([
      ['local-host', { type: 'local-candidate', candidateType: 'host', address: '192.0.2.10' }],
      ['local-server', { type: 'local-candidate', candidateType: 'srflx', address: '198.51.100.20' }],
      ['remote-host', { type: 'remote-candidate', candidateType: 'host', address: '203.0.113.30' }]
    ]);
  }
};
await iceContext.window.EZReportIceFailure(fakeFailedPeer, message => { displayedFailure = message; });
if (!displayedFailure.includes('外部アドレス') || !displayedFailure.includes('端末内') ||
    displayedFailure.includes('192.0.2.10') || displayedFailure.includes('198.51.100.20') || displayedFailure.includes('203.0.113.30'))
  throw new Error('ICE failure diagnostics must show candidate types without exposing IP addresses.');
console.log('PASS ICE failure diagnostics show candidate types without exposing IP addresses');
