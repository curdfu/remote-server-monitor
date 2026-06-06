export interface HardwareRealtimeDto {
  sampleTime: string;
  cpuName?: string | null;
  cpuUsagePercent: number | null;
  cpuTemperatureC: number | null;
  cpuFrequencyMhz: number | null;
  cpuPowerWatts: number | null;
  memoryTotalMb: number | null;
  memoryUsedMb: number | null;
  memoryUsagePercent: number | null;
  diskTemperatureC: number | null;
  disks: DiskTemperatureDto[];
  diskSpaces: DiskSpaceDto[];
  uptimeSeconds: number;
}

export interface DiskTemperatureDto {
  name: string;
  sizeBytes?: number | null;
  usedBytes?: number | null;
  temperatureC: number | null;
  temperatureSource?: string | null;
}

export interface DiskSpaceDto {
  name: string;
  totalBytes?: number | null;
  usedBytes?: number | null;
  freeBytes?: number | null;
}

export interface NetworkRealtimeDto {
  sampleTime: string;
  totalUploadBytesPerSecond: number;
  totalDownloadBytesPerSecond: number;
  wanUploadBytesPerSecond: number;
  wanDownloadBytesPerSecond: number;
  lanUploadBytesPerSecond: number;
  lanDownloadBytesPerSecond: number;
}

export interface AppTrafficSummaryDto {
  appKey: string;
  processName: string;
  displayName?: string | null;
  executablePath?: string | null;
  totalUploadBytes: number;
  totalDownloadBytes: number;
  wanUploadBytes: number;
  wanDownloadBytes: number;
  lanUploadBytes: number;
  lanDownloadBytes: number;
  loopbackUploadBytes: number;
  loopbackDownloadBytes: number;
  otherUploadBytes: number;
  otherDownloadBytes: number;
}

export interface AppTrafficSegmentDto {
  from: string;
  to: string;
  totalUploadBytes: number;
  totalDownloadBytes: number;
  wanUploadBytes: number;
  wanDownloadBytes: number;
  lanUploadBytes: number;
  lanDownloadBytes: number;
  loopbackUploadBytes: number;
  loopbackDownloadBytes: number;
  otherUploadBytes: number;
  otherDownloadBytes: number;
}

export interface NetworkPeriodSummaryDto {
  totalUploadBytes: number;
  totalDownloadBytes: number;
  wanUploadBytes: number;
  wanDownloadBytes: number;
  lanUploadBytes: number;
  lanDownloadBytes: number;
  loopbackUploadBytes: number;
  loopbackDownloadBytes: number;
  otherUploadBytes: number;
  otherDownloadBytes: number;
}

export interface AppSettingsDto {
  httpPort: number;
  hardwareSampleIntervalMs: number;
  aggregateIntervalSeconds: number;
  historyRetentionDays: number;
  topNDefault: number;
}

export interface RealtimeOverviewDto {
  hardware: HardwareRealtimeDto;
  network: NetworkRealtimeDto;
}
