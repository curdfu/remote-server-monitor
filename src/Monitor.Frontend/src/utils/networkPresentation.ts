export type TrafficDirection = 'total' | 'upload' | 'download';

export function isDirectionIncluded(filter: TrafficDirection, direction: 'upload' | 'download') {
  return filter === 'total' || filter === direction;
}

export function formatSegmentAxisTime(value: string, rangeFrom: string, rangeTo: string) {
  const date = new Date(value);
  const durationMs = Date.parse(rangeTo) - Date.parse(rangeFrom);
  if (!Number.isFinite(date.getTime()) || !Number.isFinite(durationMs) || durationMs <= 0) return '--';
  const pad = (number: number) => String(number).padStart(2, '0');
  const time = `${pad(date.getHours())}:${pad(date.getMinutes())}`;
  if (durationMs <= 24 * 60 * 60 * 1000) return time;
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
