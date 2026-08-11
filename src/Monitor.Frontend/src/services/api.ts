import type {
  AppSettingsDto,
  AppTrafficSegmentDto,
  AppTrafficSummaryDto,
  HardwareRealtimeDto,
  IgnoredNetworkAppDto,
  NetworkDashboardDto,
  NetworkPeriodSummaryDto,
  RealtimeOverviewDto
} from '../types/monitor';

const apiBase = import.meta.env.VITE_API_BASE_URL?.replace(/\/$/, '') ?? '';

// 通用 HTTP 请求封装：统一拼接 API 基地址，并处理后端返回的错误文本
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${apiBase}${path}`, {
      headers: {
        'Content-Type': 'application/json',
        ...(init?.headers ?? {})
      },
      ...init
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error;
    }

    throw new Error('无法连接监控服务，请稍后重试；实时通道会在服务恢复后自动重连。');
  }

  if (!response.ok) {
    throw new Error(await resolveResponseError(response));
  }

  return (await response.json()) as T;
}

async function resolveResponseError(response: Response) {
  if ([502, 503, 504].includes(response.status)) {
    return '监控服务暂时不可用，请稍后重试；实时通道会在服务恢复后自动重连。';
  }

  const contentType = response.headers.get('content-type')?.toLowerCase() ?? '';
  if (contentType.includes('json')) {
    try {
      const payload = await response.json() as {
        title?: unknown;
        detail?: unknown;
        errors?: Record<string, unknown>;
      };
      const validationMessages = Object.values(payload.errors ?? {})
        .flatMap((value) => Array.isArray(value) ? value : [value])
        .filter((value): value is string => typeof value === 'string');

      if (validationMessages.length > 0) {
        return validationMessages.join('；');
      }

      if (typeof payload.detail === 'string' && payload.detail.trim()) {
        return payload.detail;
      }

      if (typeof payload.title === 'string' && payload.title.trim()) {
        return payload.title;
      }
    } catch {
      // 非标准 JSON 错误体继续使用状态码兜底，不把解析异常暴露给页面。
    }
  }

  return response.status >= 500
    ? '服务处理请求时发生异常，请稍后重试。'
    : `请求失败（HTTP ${response.status}）。`;
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
  scope?: 'all' | 'wan' | 'lan' | 'loopback';
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

// 调用后端 /api/network/apps/{appKey}/segments：获取单个应用的历史分段流量
export function getNetworkAppSegments(appKey: string, params?: {
  from?: string;
  to?: string;
  scope?: 'all' | 'wan' | 'lan' | 'loopback';
  direction?: 'total' | 'upload' | 'download';
}) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.scope) query.set('scope', params.scope);
  if (params?.direction) query.set('direction', params.direction);
  return request<AppTrafficSegmentDto[]>(`/api/network/apps/${encodeURIComponent(appKey)}/segments${query.toString() ? `?${query}` : ''}`);
}

// 调用后端 /api/network/summary：获取网络页“汇总卡片 / 占比面板”使用的汇总数据
export function getNetworkSummary(params?: {
  from?: string;
  to?: string;
  scope?: 'all' | 'wan' | 'lan' | 'loopback';
  direction?: 'total' | 'upload' | 'download';
}) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.scope) query.set('scope', params.scope);
  if (params?.direction) query.set('direction', params.direction);
  return request<NetworkPeriodSummaryDto>(`/api/network/summary${query.toString() ? `?${query}` : ''}`);
}

export function getNetworkDashboard(params?: {
  from?: string;
  to?: string;
  topN?: number;
  scope?: 'all' | 'wan' | 'lan' | 'loopback';
  direction?: 'total' | 'upload' | 'download';
  signal?: AbortSignal;
}) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.topN) query.set('topN', String(params.topN));
  if (params?.scope) query.set('scope', params.scope);
  if (params?.direction) query.set('direction', params.direction);
  return request<NetworkDashboardDto>(`/api/network/dashboard${query.toString() ? `?${query}` : ''}`, {
    signal: params?.signal
  });
}

export function getIgnoredNetworkApps() {
  return request<IgnoredNetworkAppDto[]>('/api/network/ignored-apps');
}

export function ignoreNetworkApp(payload: IgnoredNetworkAppDto) {
  return request<IgnoredNetworkAppDto[]>('/api/network/ignored-apps', {
    method: 'PUT',
    body: JSON.stringify(payload)
  });
}

export function restoreNetworkApp(appKey: string) {
  return request<IgnoredNetworkAppDto[]>(`/api/network/ignored-apps/${encodeURIComponent(appKey)}`, {
    method: 'DELETE'
  });
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
