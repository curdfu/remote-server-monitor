<template>
  <section class="page">
    <PageHeader
      kicker="Overview"
      title="总览页"
      description="这一页优先把核心硬件指标与当前总上传/下载速率展示完整，并保持自动刷新。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="loadOverview">
          {{ isLoading ? '刷新中...' : '立即刷新' }}
        </button>
      </template>
    </PageHeader>

    <div class="overview-meta card">
      <div>
        <span class="muted">最后更新时间</span>
        <strong>{{ formatDateTime(overview?.hardware.sampleTime || overview?.network.sampleTime) }}</strong>
      </div>
      <div>
        <span class="muted">刷新策略</span>
        <strong>SignalR 实时推送</strong>
      </div>
      <div>
        <span class="muted">当前状态</span>
        <strong>{{ errorMessage ? '接口异常' : '运行中' }}</strong>
      </div>
    </div>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <section class="dashboard-section">
      <div class="section-header">
        <h3>硬件实时指标</h3>
        <span class="muted">CPU / 内存 / 温度 / 开机时长</span>
      </div>
      <div class="grid">
        <MetricCard label="CPU 使用率" :value="formatPercent(overview?.hardware.cpuUsagePercent)" />
        <MetricCard label="内存使用率" :value="formatPercent(overview?.hardware.memoryUsagePercent)" />
        <MetricCard label="CPU 温度" :value="formatNullable(overview?.hardware.cpuTemperatureC, '°C')" />
        <MetricCard label="磁盘温度" :value="formatNullable(overview?.hardware.diskTemperatureC, '°C')" />
        <MetricCard label="CPU 频率" :value="formatNullable(overview?.hardware.cpuFrequencyMhz, 'MHz')" />
        <MetricCard label="开机时长" :value="formatUptime(overview?.hardware.uptimeSeconds)" />
      </div>
    </section>

    <section class="dashboard-section">
      <div class="section-header">
        <h3>网络实时指标</h3>
        <span class="muted">当前总上传 / 下载速率</span>
      </div>
      <div class="grid">
        <MetricCard label="当前上传" :value="formatRate(overview?.network.totalUploadBytesPerSecond)" />
        <MetricCard label="当前下载" :value="formatRate(overview?.network.totalDownloadBytesPerSecond)" />
        <MetricCard label="WAN 上传" :value="formatRate(overview?.network.wanUploadBytesPerSecond)" />
        <MetricCard label="WAN 下载" :value="formatRate(overview?.network.wanDownloadBytesPerSecond)" />
        <MetricCard label="LAN 上传" :value="formatRate(overview?.network.lanUploadBytesPerSecond)" />
        <MetricCard label="LAN 下载" :value="formatRate(overview?.network.lanDownloadBytesPerSecond)" />
      </div>
    </section>

    <section class="panel-grid">
      <article class="card">
        <div class="panel-header">
          <h3>Top App 预览</h3>
          <span class="muted">{{ isLoading ? '正在更新…' : `共 ${topApps.length} 项` }}</span>
        </div>
        <ul class="simple-list realtime-list">
          <li v-for="item in topApps" :key="item.appKey">
            <strong>
              {{ item.displayName || item.processName }}
              <small v-if="item.isStale" class="muted">（暂时空闲）</small>
            </strong>
            <span>{{ formatRate(item.downloadBytesPerSecond) }} ↓ / {{ formatRate(item.uploadBytesPerSecond) }} ↑</span>
          </li>
          <li v-if="!topApps.length" class="muted">
            当前还没有实时排行数据。
          </li>
        </ul>
      </article>

      <article class="card">
        <h3>当前页已完成项</h3>
        <ul class="simple-list compact">
          <li>CPU 使用率</li>
          <li>内存使用率</li>
          <li>CPU 温度</li>
          <li>磁盘温度</li>
          <li>CPU 频率</li>
          <li>开机时长</li>
          <li>当前总上传 / 下载速率</li>
        </ul>
      </article>
    </section>
  </section>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import MetricCard from '../components/MetricCard.vue';
import PageHeader from '../components/PageHeader.vue';
import { getOverview } from '../services/api';
import { createFrameUpdater } from '../services/frameUpdater';
import {
  startRealtimeConnection,
  subscribeHardwareRealtime,
  subscribeNetworkRealtime,
  subscribeTopAppsRealtime
} from '../services/realtime';
import {
  mergeRetainedTopApps,
  pruneRetainedTopApps,
  type RetainedAppTrafficItem
} from '../services/topAppsRetention';
import type {
  AppTrafficItemDto,
  HardwareRealtimeDto,
  NetworkRealtimeDto,
  RealtimeOverviewDto
} from '../types/monitor';

const overview = ref<RealtimeOverviewDto | null>(null);
const topApps = ref<RetainedAppTrafficItem[]>([]);
const isLoading = ref(false);
const errorMessage = ref('');
let cleanupTimer: number | null = null;
let unsubscribeHardware: (() => void) | null = null;
let unsubscribeNetwork: (() => void) | null = null;
let unsubscribeTopApps: (() => void) | null = null;
const topAppRetentionMs = 10_000;
const pendingHardware = ref<HardwareRealtimeDto | null>(null);
const pendingNetwork = ref<NetworkRealtimeDto | null>(null);
const pendingTopApps = ref<AppTrafficItemDto[] | null>(null);
const frameUpdater = createFrameUpdater(flushRealtimeState);

onMounted(() => {
  void loadOverview();
  void startRealtimeConnection().catch(() => {
    errorMessage.value = 'SignalR 实时通道连接失败，将继续保留当前页面数据。';
  });

  unsubscribeHardware = subscribeHardwareRealtime((hardware) => {
    errorMessage.value = '';
    pendingHardware.value = hardware;
    frameUpdater.schedule();
  });

  unsubscribeNetwork = subscribeNetworkRealtime((network) => {
    errorMessage.value = '';
    pendingNetwork.value = network;
    frameUpdater.schedule();
  });

  unsubscribeTopApps = subscribeTopAppsRealtime((items) => {
    errorMessage.value = '';
    pendingTopApps.value = items;
    frameUpdater.schedule();
  });
});

onUnmounted(() => {
  frameUpdater.cancel();
  unsubscribeHardware?.();
  unsubscribeNetwork?.();
  unsubscribeTopApps?.();
});

async function loadOverview() {
  isLoading.value = true;
  errorMessage.value = '';

  try {
    const loaded = await getOverview();
    overview.value = loaded;
    topApps.value = mergeRetainedTopApps([], loaded.topApps, topAppRetentionMs);
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '加载总览数据失败。';
  } finally {
    isLoading.value = false;
  }
}

function formatPercent(value?: number | null) {
  return value == null ? '--' : `${value.toFixed(1)}%`;
}

function formatNullable(value: number | null | undefined, unit: string) {
  return value == null ? '--' : `${value.toFixed(1)} ${unit}`;
}

function formatRate(value?: number | null) {
  if (value == null) return '--';
  if (value < 1024) return `${value.toFixed(0)} B/s`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB/s`;
  return `${(value / 1024 / 1024).toFixed(2)} MB/s`;
}

function formatUptime(value?: number | null) {
  if (value == null) return '--';

  const days = Math.floor(value / 86400);
  const hours = Math.floor((value % 86400) / 3600);
  const minutes = Math.floor((value % 3600) / 60);

  if (days > 0) {
    return `${days}d ${hours}h ${minutes}m`;
  }

  return `${hours}h ${minutes}m`;
}

function formatDateTime(value?: string | null) {
  if (!value) return '--';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--';
  return date.toLocaleString();
}

function emptyHardwareRealtime() {
  return {
    sampleTime: new Date(0).toISOString(),
    cpuUsagePercent: null,
    cpuTemperatureC: null,
    cpuFrequencyMhz: null,
    memoryTotalMb: null,
    memoryUsedMb: null,
    memoryUsagePercent: null,
    diskTemperatureC: null,
    uptimeSeconds: 0
  };
}

function emptyNetworkRealtime() {
  return {
    sampleTime: new Date(0).toISOString(),
    totalUploadBytesPerSecond: 0,
    totalDownloadBytesPerSecond: 0,
    wanUploadBytesPerSecond: 0,
    wanDownloadBytesPerSecond: 0,
    lanUploadBytesPerSecond: 0,
    lanDownloadBytesPerSecond: 0
  };
}

function flushRealtimeState() {
  const hasHardware = pendingHardware.value !== null;
  const hasNetwork = pendingNetwork.value !== null;
  const hasTopApps = pendingTopApps.value !== null;

  if (!hasHardware && !hasNetwork && !hasTopApps) {
    return;
  }

  const nextTopAppsSource = pendingTopApps.value;
  const retainedTopApps = nextTopAppsSource
    ? mergeRetainedTopApps(topApps.value, nextTopAppsSource, topAppRetentionMs)
    : pruneRetainedTopApps(topApps.value, topAppRetentionMs);

  topApps.value = retainedTopApps;

  overview.value = {
    hardware: pendingHardware.value ?? overview.value?.hardware ?? emptyHardwareRealtime(),
    network: pendingNetwork.value ?? overview.value?.network ?? emptyNetworkRealtime(),
    topApps: nextTopAppsSource ?? overview.value?.topApps ?? []
  };

  pendingHardware.value = null;
  pendingNetwork.value = null;
  pendingTopApps.value = null;
}
</script>
