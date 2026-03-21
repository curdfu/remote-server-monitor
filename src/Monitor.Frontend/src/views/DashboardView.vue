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
        <strong>每 2 秒自动刷新</strong>
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
          <span class="muted">{{ isLoading ? '正在更新…' : `共 ${overview?.topApps?.length ?? 0} 项` }}</span>
        </div>
        <ul class="simple-list">
          <li v-for="item in overview?.topApps ?? []" :key="item.appKey">
            <strong>{{ item.displayName || item.processName }}</strong>
            <span>{{ formatRate(item.downloadBytesPerSecond) }} ↓ / {{ formatRate(item.uploadBytesPerSecond) }} ↑</span>
          </li>
          <li v-if="!overview?.topApps?.length" class="muted">
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
import type { RealtimeOverviewDto } from '../types/monitor';

const overview = ref<RealtimeOverviewDto | null>(null);
const isLoading = ref(false);
const errorMessage = ref('');

let refreshTimer: number | null = null;

onMounted(() => {
  void loadOverview();
  refreshTimer = window.setInterval(() => {
    void loadOverview();
  }, 2000);
});

onUnmounted(() => {
  if (refreshTimer !== null) {
    window.clearInterval(refreshTimer);
  }
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
</script>
