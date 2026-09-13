import { computed, onMounted, onUnmounted, ref, type Ref } from 'vue';
import { getStaleThresholdMs, inspectSampleTime } from '../utils/telemetryState';

export function useTelemetryClock(tickMs = 1000) {
  const nowMs = ref(Date.now());
  let timer: number | null = null;

  const start = () => {
    if (timer !== null) return;
    timer = window.setInterval(() => {
      nowMs.value = Date.now();
    }, tickMs);
  };

  const stop = () => {
    if (timer === null) return;
    window.clearInterval(timer);
    timer = null;
  };

  onMounted(start);
  onUnmounted(stop);

  return { nowMs, start, stop };
}

export function useTelemetryFreshness(
  sampleTime: Ref<string | null | undefined>,
  intervalMs: Ref<number | null | undefined>,
  nowMs: Ref<number>
) {
  return computed(() => inspectSampleTime(
    sampleTime.value,
    nowMs.value,
    getStaleThresholdMs(intervalMs.value)
  ));
}
