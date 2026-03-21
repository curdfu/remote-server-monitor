<template>
  <section class="page">
    <PageHeader
      kicker="Overview"
      title="总览页"
      description="这一页优先展示核心硬件指标，让首页保持更简洁。"
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
        <strong>{{ formatDateTime(overview?.hardware.sampleTime) }}</strong>
      </div>
      <div>
        <span class="muted">刷新策略</span>
        <strong>硬件数据实时推送</strong>
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
        <MetricCard label="开机时长" :value="formatUptime(overview?.hardware.uptimeSeconds)" />
        <MetricCard label="CPU 频率" :value="formatNullable(overview?.hardware.cpuFrequencyMhz, 'MHz')" />
        <MetricCard label="CPU 温度" :value="formatNullable(overview?.hardware.cpuTemperatureC, '°C')" />
        <MetricCard label="CPU 功耗" :value="formatNullable(overview?.hardware.cpuPowerWatts, 'W')" />
        <MetricCard label="CPU 使用率" :value="formatPercent(overview?.hardware.cpuUsagePercent)" />
        <MetricCard
          label="当前内存占用"
          :value="formatMemoryUsage(overview?.hardware.memoryUsedMb, overview?.hardware.memoryTotalMb)"
        />
        <MetricCard label="最高磁盘温度" :value="formatNullable(overview?.hardware.diskTemperatureC, '°C')" />
      </div>
    </section>

    <section class="panel-grid panel-grid-single">
      <article class="card">
        <div class="panel-header">
          <h3>磁盘温度</h3>
          <span class="muted">当前共 {{ overview?.hardware.disks?.length ?? 0 }} 块</span>
        </div>
        <ul class="simple-list realtime-list">
          <li v-for="disk in overview?.hardware.disks ?? []" :key="disk.name">
            <strong>{{ disk.name }}</strong>
            <span>{{ formatNullable(disk.temperatureC, '°C') }}</span>
          </li>
          <li v-if="!(overview?.hardware.disks?.length)">
            <span class="muted">当前没有可展示的磁盘温度数据。</span>
          </li>
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
import { startRealtimeConnection, subscribeHardwareRealtime } from '../services/realtime';
import type { HardwareRealtimeDto, RealtimeOverviewDto } from '../types/monitor';

const overview = ref<RealtimeOverviewDto | null>(null);
const isLoading = ref(false);
const errorMessage = ref('');
let unsubscribeHardware: (() => void) | null = null;

onMounted(() => {
  void loadOverview();
  void startRealtimeConnection().catch(() => {
    errorMessage.value = 'SignalR 实时通道连接失败，将继续保留当前页面数据。';
  });

  unsubscribeHardware = subscribeHardwareRealtime((hardware) => {
    errorMessage.value = '';
    overview.value = {
      hardware,
      network: overview.value?.network ?? emptyNetworkRealtime(),
      topApps: overview.value?.topApps ?? []
    };
  });
});

onUnmounted(() => {
  unsubscribeHardware?.();
});

async function loadOverview() {
  isLoading.value = true;
  errorMessage.value = '';

  try {
    overview.value = await getOverview();
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

function formatMemoryUsage(value?: number | null, total?: number | null) {
  if (value == null) return '--';

  // LibreHardwareMonitor 的内存 Data 传感器通常直接给出 GB，
  // 这里根据总量做一次兼容判断，避免把 23 GB 显示成 23 MB。
  if (total != null && total < 512) {
    return `${value.toFixed(1)} GB`;
  }

  if (value < 1024) return `${value.toFixed(1)} MB`;
  return `${(value / 1024).toFixed(1)} GB`;
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
</script>
