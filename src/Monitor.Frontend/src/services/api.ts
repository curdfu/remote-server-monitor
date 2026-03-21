import type {
  AppSettingsDto,
  AppTrafficSummaryDto,
  HardwareRealtimeDto,
  NetworkRealtimeDto,
  RealtimeOverviewDto
} from '../types/monitor';

const apiBase = import.meta.env.VITE_API_BASE_URL?.replace(/\/$/, '') ?? '';

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

export function getOverview() {
  return request<RealtimeOverviewDto>('/api/overview');
}

export function getHardwareHistory(from?: string, to?: string) {
  const query = new URLSearchParams();
  if (from) query.set('from', from);
  if (to) query.set('to', to);
  return request<HardwareRealtimeDto[]>(`/api/hardware/history${query.toString() ? `?${query}` : ''}`);
}

export function getNetworkApps(params?: { from?: string; to?: string; topN?: number }) {
  const query = new URLSearchParams();
  if (params?.from) query.set('from', params.from);
  if (params?.to) query.set('to', params.to);
  if (params?.topN) query.set('topN', String(params.topN));
  return request<AppTrafficSummaryDto[]>(`/api/network/apps${query.toString() ? `?${query}` : ''}`);
}

export function getSettings() {
  return request<AppSettingsDto>('/api/settings');
}

export function saveSettings(payload: AppSettingsDto) {
  return request<AppSettingsDto>('/api/settings', {
    method: 'POST',
    body: JSON.stringify(payload)
  });
}
