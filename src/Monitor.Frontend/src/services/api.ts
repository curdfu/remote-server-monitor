import type {
  AppSettingsDto,
  AppTrafficSummaryDto,
  HardwareRealtimeDto,
  NetworkPeriodSummaryDto,
  NetworkRealtimeDto,
  RealtimeOverviewDto
} from '../types/monitor';

const apiBase = import.meta.env.VITE_API_BASE_URL?.replace(/\/$/, '') ?? '';

// 通用 HTTP 请求封装：统一拼接 API 基地址，并处理后端返回的错误文本
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${apiBase}${path}`, {
    headers: {
      'Content-Type': 'application/json',
      ...(init?.headers ?? {})
    },
    ...init
  });

  if (!response.ok) {
    const message = await response.text();
    throw new Error(message || `Request failed: ${response.status}`);
  }

  return (await response.json()) as T;
}

// 调用后端 /api/overview：获取首页概览数据
export function getOverview() {
  return request<RealtimeOverviewDto>('/api/overview');
}

// 调用后端 /api/hardware/history：获取硬件历史曲线数据
export function getHardwareHistory(from?: string, to?: string) {
  const query = new URLSearchParams();
  if (from) query.set('from', from);
  if (to) query.set('to', to);
  return request<HardwareRealtimeDto[]>(`/api/hardware/history${query.toString() ? `?${query}` : ''}`);
}

// 调用后端 /api/network/apps：获取网络页“应用流量排行”列表
export function getNetworkApps(params?: {
  from?: string;
  to?: string;
  topN?: number;
  scope?: 'all' | 'wan' | 'lan';
  direction?: 'total' | 'upload' | 'download';
}) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.topN) query.set('topN', String(params.topN));
  if (params?.scope) query.set('scope', params.scope);
  if (params?.direction) query.set('direction', params.direction);
  return request<AppTrafficSummaryDto[]>(`/api/network/apps${query.toString() ? `?${query}` : ''}`);
}

// 调用后端 /api/network/summary：获取网络页“汇总卡片 / 占比面板”使用的汇总数据
export function getNetworkSummary(params?: {
  from?: string;
  to?: string;
  scope?: 'all' | 'wan' | 'lan';
  direction?: 'total' | 'upload' | 'download';
}) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.scope) query.set('scope', params.scope);
  if (params?.direction) query.set('direction', params.direction);
  return request<NetworkPeriodSummaryDto>(`/api/network/summary${query.toString() ? `?${query}` : ''}`);
}

// 调用后端 /api/settings：读取设置页表单初始值
export function getSettings() {
  return request<AppSettingsDto>('/api/settings');
}

// 调用后端 /api/settings：保存设置页修改后的配置
export function saveSettings(payload: AppSettingsDto) {
  return request<AppSettingsDto>('/api/settings', {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}
