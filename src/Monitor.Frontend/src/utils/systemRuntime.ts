export interface RuntimeSnapshot {
  sampleTime: string;
  uptimeSeconds: number;
}

function getBootTimeMs(sampleTime?: string | null, uptimeSeconds?: number | null) {
  if (!sampleTime || uptimeSeconds == null || !Number.isFinite(uptimeSeconds) || uptimeSeconds < 0) return null;
  const sampleMs = Date.parse(sampleTime);
  const bootMs = sampleMs - uptimeSeconds * 1000;
  return Number.isFinite(sampleMs) && Number.isFinite(new Date(bootMs).getTime()) ? bootMs : null;
}

export function formatBootTime(sampleTime?: string | null, uptimeSeconds?: number | null) {
  const bootMs = getBootTimeMs(sampleTime, uptimeSeconds);
  return bootMs === null ? '--' : new Date(bootMs).toLocaleString('zh-CN', { hour12: false });
}

export function formatUptime(value?: number | null) {
  if (value == null || !Number.isFinite(value) || value < 0) return '--';
  const days = Math.floor(value / 86400);
  const hours = Math.floor((value % 86400) / 3600);
  const minutes = Math.floor((value % 3600) / 60);
  return days > 0 ? `${days}天 ${hours}小时 ${minutes}分` : `${hours}小时 ${minutes}分`;
}

export function isRuntimeSnapshotNewer(current: RuntimeSnapshot | null, incoming: RuntimeSnapshot) {
  if (getBootTimeMs(incoming.sampleTime, incoming.uptimeSeconds) === null) return false;
  return !current || Date.parse(incoming.sampleTime) > Date.parse(current.sampleTime);
}
