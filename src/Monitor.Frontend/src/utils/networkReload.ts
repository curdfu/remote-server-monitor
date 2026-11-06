type ReloadSource = 'filter' | 'manual';

export function createNetworkReloadController(options: {
  schedule: (callback: () => void, delayMs: number) => number;
  cancel: (timer: number) => void;
  getPreset: () => number | null;
  advancePreset: (hours: number) => void;
  load: (source: ReloadSource) => void;
}) {
  let timer: number | null = null;
  let suppress = false;
  let disposed = false;

  function cancelPending() {
    if (timer !== null) options.cancel(timer);
    timer = null;
  }

  return {
    filtersChanged() {
      if (suppress || disposed) return;
      cancelPending();
      timer = options.schedule(() => {
        timer = null;
        if (!disposed) options.load('filter');
      }, 250);
    },
    refresh() {
      if (disposed) return;
      cancelPending();
      const preset = options.getPreset();
      if (preset != null) {
        suppress = true;
        try {
          options.advancePreset(preset);
        } finally {
          suppress = false;
        }
      }
      options.load('manual');
    },
    dispose() {
      disposed = true;
      cancelPending();
    }
  };
}
