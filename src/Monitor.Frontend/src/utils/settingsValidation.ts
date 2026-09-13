import type { AppSettingsDto } from '../types/monitor';

export function validateSettings(form: AppSettingsDto): Record<string, string> {
  const errors: Record<string, string> = {};

  addRangeError(errors, 'httpPort', form.httpPort, 1, 65535, 'HTTP 端口必须在 1 ~ 65535 之间。');
  addRangeError(errors, 'hardwareSampleIntervalMs', form.hardwareSampleIntervalMs, 500, 60000, '硬件采样间隔必须在 500 ~ 60000 ms 之间。');
  addRangeError(errors, 'networkProcessingIntervalMs', form.networkProcessingIntervalMs, 500, 60000, '网络处理间隔必须在 500 ~ 60000 ms 之间。');
  addRangeError(errors, 'aggregateIntervalSeconds', form.aggregateIntervalSeconds, 1, 3600, '默认统计粒度必须在 1 ~ 3600 秒之间。');
  addRangeError(errors, 'historyRetentionDays', form.historyRetentionDays, 1, 3650, '历史保留天数必须在 1 ~ 3650 天之间。');
  addRangeError(errors, 'topNDefault', form.topNDefault, 1, 100, '默认排行数量必须在 1 ~ 100 之间。');

  return errors;
}

function addRangeError(
  errors: Record<string, string>,
  key: keyof AppSettingsDto,
  value: number,
  minimum: number,
  maximum: number,
  message: string
) {
  if (!Number.isInteger(value) || !Number.isFinite(value) || value < minimum || value > maximum) {
    errors[key] = message;
  }
}
