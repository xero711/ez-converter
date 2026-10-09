'use strict';

window.EZTransferWindow = function (windowBytes = 384 * 1024) {
  if (!Number.isSafeInteger(windowBytes) || windowBytes < 48 * 1024 || windowBytes > 4 * 1024 * 1024)
    throw new RangeError('Invalid transfer window size.');

  let sentThrough = 0;
  let acknowledgedThrough = 0;
  let failure = null;
  const waiters = new Set();

  function settle() {
    for (const waiter of waiters) {
      if (failure) {
        clearTimeout(waiter.timer);
        waiters.delete(waiter);
        waiter.reject(failure);
      } else if (acknowledgedThrough >= waiter.offset) {
        clearTimeout(waiter.timer);
        waiters.delete(waiter);
        waiter.resolve();
      }
    }
  }

  return {
    windowBytes,
    get acknowledgedOffset() { return acknowledgedThrough; },
    reset(offset = 0) {
      if (!Number.isSafeInteger(offset) || offset < 0)
        throw new RangeError('Initial offset must be a non-negative safe integer.');
      if (waiters.size) throw new Error('Cannot reset a transfer window with pending acknowledgements.');
      sentThrough = offset;
      acknowledgedThrough = offset;
      failure = null;
    },
    markSent(offset) {
      if (!Number.isSafeInteger(offset) || offset <= sentThrough)
        throw new RangeError('Sent offsets must increase monotonically.');
      sentThrough = offset;
    },
    acknowledge(offset) {
      if (!Number.isSafeInteger(offset) || offset < 0)
        throw new RangeError('Invalid acknowledgement offset.');
      if (offset <= acknowledgedThrough) return;
      if (offset > sentThrough) throw new RangeError('Acknowledgement exceeds sent data.');
      acknowledgedThrough = offset;
      settle();
    },
    waitFor(offset, timeoutMs = 30000) {
      if (!Number.isSafeInteger(offset) || offset < 0 || offset > sentThrough)
        return Promise.reject(new RangeError('Cannot wait for an unsent offset.'));
      if (failure) return Promise.reject(failure);
      if (acknowledgedThrough >= offset) return Promise.resolve();
      return new Promise((resolve, reject) => {
        const waiter = { offset, resolve, reject, timer: null };
        waiter.timer = setTimeout(() => {
          waiters.delete(waiter);
          reject(new Error('相手から応答がありません。接続を確認してください。'));
        }, timeoutMs);
        waiters.add(waiter);
      });
    },
    fail(error = new Error('P2P接続が終了しました。')) {
      failure = error;
      settle();
    }
  };
};
