import type { DiskTemperatureDto, HardwareRealtimeDto } from '../types/monitor';

export function findHottestDisk(disks: DiskTemperatureDto[]) {
  return disks.reduce<DiskTemperatureDto | null>((hottest, disk) => {
    const temperature = disk.temperatureC;
    if (temperature == null || !Number.isFinite(temperature)) return hottest;
    return !hottest || temperature > (hottest.temperatureC ?? -Infinity) ? disk : hottest;
  }, null);
}

// Incoming history fills gaps; already received realtime samples remain authoritative.
export function mergeHistorySamples(
  current: HardwareRealtimeDto[],
  incoming: HardwareRealtimeDto[],
  nowMs: number,
  durationMs = 60 * 60 * 1000
) {
  const fromMs = nowMs - durationMs;
  const byTime = new Map<number, HardwareRealtimeDto>();
  for (const sample of [...incoming, ...current]) {
    const time = Date.parse(sample.sampleTime);
    if (Number.isFinite(time) && time >= fromMs) byTime.set(time, sample);
  }
  return [...byTime.entries()].sort(([left], [right]) => left - right).map(([, sample]) => sample);
}
