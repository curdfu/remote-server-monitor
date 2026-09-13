export type SampleFreshness = 'waiting' | 'fresh' | 'stale' | 'invalid-time';

export function getStaleThresholdMs(intervalMs: number | null | undefined) {
  const safeInterval = Number.isFinite(intervalMs) && intervalMs != null && intervalMs > 0
    ? intervalMs
    : 60_000;
  return Math.max(15_000, safeInterval * 3);
}

export function inspectSampleTime(
  sampleTime: string | null | undefined,
  nowMs: number,
  thresholdMs: number,
  futureToleranceMs = 5_000
): { state: SampleFreshness; ageMs: number | null } {
  if (!sampleTime) return { state: 'waiting', ageMs: null };

  const sampleMs = Date.parse(sampleTime);
  if (!Number.isFinite(sampleMs) || sampleMs > nowMs + futureToleranceMs) {
    return { state: 'invalid-time', ageMs: null };
  }

  const ageMs = Math.max(0, nowMs - sampleMs);
  return {
    state: ageMs >= thresholdMs ? 'stale' : 'fresh',
    ageMs
  };
}

export function shouldAcceptSnapshot(
  currentSampleTime: string | null | undefined,
  incomingSampleTime: string | null | undefined
) {
  if (!incomingSampleTime || !Number.isFinite(Date.parse(incomingSampleTime))) return false;
  if (!currentSampleTime || !Number.isFinite(Date.parse(currentSampleTime))) return true;
  return Date.parse(incomingSampleTime) > Date.parse(currentSampleTime);
}

export type ResourcePressure = 'unknown' | 'normal' | 'warning' | 'danger';

export function getResourcePressure(
  value: number | null | undefined,
  warningThreshold: number,
  dangerThreshold: number
): ResourcePressure {
  if (value == null || !Number.isFinite(value)) return 'unknown';
  if (value >= dangerThreshold) return 'danger';
  if (value >= warningThreshold) return 'warning';
  return 'normal';
}
