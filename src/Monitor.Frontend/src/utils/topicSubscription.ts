export interface TopicSubscriptionOptions {
  isConnected: () => boolean;
  invoke: (method: string) => Promise<void>;
  schedule: (callback: () => void, delayMs: number) => number;
  cancel: (timer: number) => void;
  onError?: (error: unknown) => void;
  onSubscribed?: () => void;
}

// Each connection generation owns its own pending invocation and retry timer.
export function createTopicSubscription(options: TopicSubscriptionOptions) {
  let desired = false;
  let subscribed = false;
  let generation = 0;
  let inFlight = false;
  let timer: number | null = null;
  let failures = 0;

  function clearRetry() {
    if (timer !== null) options.cancel(timer);
    timer = null;
  }

  function sync() {
    if (!options.isConnected() || inFlight || timer !== null || desired === subscribed) return;
    const invocationGeneration = generation;
    const target = desired;
    inFlight = true;
    void options.invoke(target ? 'SubscribeHardware' : 'UnsubscribeHardware').then(() => {
      if (invocationGeneration !== generation) return;
      subscribed = target;
      failures = 0;
      inFlight = false;
      sync();
      if (target && desired) options.onSubscribed?.();
    }, (error: unknown) => {
      if (invocationGeneration !== generation) return;
      inFlight = false;
      options.onError?.(error);
      if (!options.isConnected() || desired === subscribed) return;
      const delayMs = Math.min(30_000, 1_000 * 2 ** Math.min(failures++, 5));
      timer = options.schedule(() => {
        timer = null;
        if (invocationGeneration === generation) sync();
      }, delayMs);
    });
  }

  return {
    setDesired(value: boolean) {
      if (desired !== value) {
        desired = value;
        clearRetry();
      }
      sync();
    },
    reset() {
      generation++;
      subscribed = false;
      inFlight = false;
      failures = 0;
      clearRetry();
    },
    sync
  };
}
