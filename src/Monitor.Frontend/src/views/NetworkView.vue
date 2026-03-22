<template>
  <section class="page network-page">
    <PageHeader
      iconName="network"
      kicker="网络"
      title="网络流量"
      description="这里主要看一段时间内的累计流量、占比和应用排行。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="refreshApps">
          <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
          {{ isLoading ? '查询中...' : '重新查询' }}
        </button>
      </template>
    </PageHeader>

    <article class="card filters-card filters-card-elevated">
      <label>
        开始时间
        <input v-model="filters.from" type="datetime-local" />
      </label>
      <label>
        结束时间
        <input v-model="filters.to" type="datetime-local" />
      </label>
      <label>
        排行数量
        <input v-model.number="filters.topN" type="number" min="1" max="100" />
      </label>
      <label>
        统计范围
        <select v-model="filters.scope">
          <option value="all">全部</option>
          <option value="wan">WAN</option>
          <option value="lan">LAN</option>
        </select>
      </label>
      <label>
        统计方向
        <select v-model="filters.direction">
          <option value="total">总流量</option>
          <option value="upload">上传</option>
          <option value="download">下载</option>
        </select>
      </label>
    </article>

    <div class="preset-row">
      <button
        v-for="preset in presetOptions"
        :key="preset.hours"
        class="chip-button"
        :class="{ 'chip-button-active': activePresetHours === preset.hours }"
        @click="applyPreset(preset.hours)"
      >
        {{ preset.label }}
      </button>
    </div>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <div class="grid">
      <div class="card metric-card metric-card-compact network-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="upload" :size="14" />累计上传</span>
        </div>
        <strong class="metric-value">{{ formatBytes(totalUploadBytes) }}</strong>
      </div>
      <div class="card metric-card metric-card-compact network-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="download" :size="14" />累计下载</span>
        </div>
        <strong class="metric-value">{{ formatBytes(totalDownloadBytes) }}</strong>
      </div>
      <div class="card metric-card metric-card-compact network-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="apps" :size="14" />涉及应用数</span>
        </div>
        <strong class="metric-value">{{ items.length }}</strong>
      </div>
      <div class="card metric-card metric-card-compact network-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="uptime" :size="14" />统计时间范围</span>
        </div>
        <div v-if="rangeParts" class="metric-value metric-small range-value">
          <span>{{ rangeParts.from }}&nbsp;~</span>
          <span>{{ rangeParts.to }}</span>
        </div>
        <strong v-else class="metric-value metric-small">--</strong>
      </div>
    </div>

    <section class="panel-grid">
      <article class="card dashboard-panel-card">
        <div class="panel-header">
          <div class="panel-title">
            <span class="panel-icon"><AppIcon name="traffic" :size="16" /></span>
            <div>
              <h3>WAN / LAN 占比</h3>
              <p class="panel-subtitle">按当前筛选结果汇总的累计流量分布。</p>
            </div>
          </div>
          <span class="section-tag">按当前筛选结果汇总</span>
        </div>

        <div class="ratio-overview">
          <div class="ratio-summary-card ratio-summary-card-wan">
            <div class="ratio-summary-top">
              <span class="ratio-summary-label">WAN 流量</span>
              <span class="ratio-summary-badge">外网</span>
            </div>
            <strong>{{ formatBytes(wanTotalBytes) }}</strong>
            <small>{{ wanPercent.toFixed(1) }}%</small>
          </div>

          <div class="ratio-summary-card ratio-summary-card-lan">
            <div class="ratio-summary-top">
              <span class="ratio-summary-label">LAN 流量</span>
              <span class="ratio-summary-badge">内网</span>
            </div>
            <strong>{{ formatBytes(lanTotalBytes) }}</strong>
            <small>{{ lanPercent.toFixed(1) }}%</small>
          </div>

          <div class="ratio-summary-card ratio-summary-card-neutral">
            <div class="ratio-summary-top">
              <span class="ratio-summary-label">主导网络</span>
              <span class="ratio-summary-badge">概览</span>
            </div>
            <strong>{{ dominantScopeLabel }}</strong>
            <small>{{ dominantScopeHint }}</small>
          </div>
        </div>

        <div class="ratio-group">
          <div class="ratio-item">
            <div class="ratio-header">
              <strong>WAN</strong>
              <span>{{ wanPercent.toFixed(1) }}%</span>
            </div>
            <div class="ratio-track">
              <div class="ratio-bar ratio-bar-wan" :style="{ width: `${wanPercent}%` }"></div>
            </div>
            <small class="muted">{{ formatBytes(wanTotalBytes) }}</small>
          </div>

          <div class="ratio-item">
            <div class="ratio-header">
              <strong>LAN</strong>
              <span>{{ lanPercent.toFixed(1) }}%</span>
            </div>
            <div class="ratio-track">
              <div class="ratio-bar ratio-bar-lan" :style="{ width: `${lanPercent}%` }"></div>
            </div>
            <small class="muted">{{ formatBytes(lanTotalBytes) }}</small>
          </div>
        </div>
      </article>

      <article class="card dashboard-panel-card">
        <div class="panel-header">
          <div class="panel-title">
            <span class="panel-icon"><AppIcon name="apps" :size="16" /></span>
            <div>
              <h3>应用流量排行</h3>
              <p class="panel-subtitle">保持现有筛选逻辑，仅增强视觉层次。</p>
            </div>
          </div>
          <span class="section-tag">{{ rankingDescription }}</span>
        </div>
        <ol class="ranking-list">
          <li v-for="(item, index) in topRanking" :key="item.appKey">
            <div class="ranking-main">
              <span class="ranking-index">{{ index + 1 }}</span>
              <strong>{{ item.displayName || item.processName }}</strong>
              <small class="muted">{{ item.processName }}</small>
            </div>
            <span class="ranking-value">{{ formatBytes(getRankingValue(item)) }}</span>
          </li>
          <li v-if="!topRanking.length" class="muted">当前还没有可展示的排行数据。</li>
        </ol>
      </article>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import AppIcon from '../components/AppIcon.vue';
import PageHeader from '../components/PageHeader.vue';
import { getNetworkApps } from '../services/api';
import type { AppTrafficSummaryDto } from '../types/monitor';

const presetOptions = [
  { hours: 1, label: '最近 1 小时' },
  { hours: 6, label: '最近 6 小时' },
  { hours: 24, label: '最近 24 小时' },
  { hours: 72, label: '最近 3 天' },
  { hours: 24 * 7, label: '最近 1 周' },
  { hours: 24 * 30, label: '最近 30 天' }
] as const;

const items = ref<AppTrafficSummaryDto[]>([]);
const isLoading = ref(false);
const errorMessage = ref('');
const filters = reactive({
  from: toLocalInputValue(new Date(Date.now() - 24 * 60 * 60 * 1000)),
  to: toLocalInputValue(new Date()),
  topN: 10,
  scope: 'wan' as 'all' | 'wan' | 'lan',
  direction: 'upload' as 'total' | 'upload' | 'download'
});

const totalUploadBytes = computed(() =>
  items.value.reduce((sum, item) => sum + item.totalUploadBytes, 0)
);

const totalDownloadBytes = computed(() =>
  items.value.reduce((sum, item) => sum + item.totalDownloadBytes, 0)
);

const wanTotalBytes = computed(() =>
  items.value.reduce((sum, item) => sum + item.wanUploadBytes + item.wanDownloadBytes, 0)
);

const lanTotalBytes = computed(() =>
  items.value.reduce((sum, item) => sum + item.lanUploadBytes + item.lanDownloadBytes, 0)
);

const totalScopeBytes = computed(() => wanTotalBytes.value + lanTotalBytes.value);

const wanPercent = computed(() =>
  totalScopeBytes.value === 0 ? 0 : (wanTotalBytes.value / totalScopeBytes.value) * 100
);

const lanPercent = computed(() =>
  totalScopeBytes.value === 0 ? 0 : (lanTotalBytes.value / totalScopeBytes.value) * 100
);

const topRanking = computed(() =>
  [...items.value]
    .sort((left, right) => getRankingValue(right) - getRankingValue(left))
    .slice(0, filters.topN)
);

const rankingDescription = computed(() => {
  const scopeLabel = filters.scope === 'wan' ? 'WAN' : filters.scope === 'lan' ? 'LAN' : '全部';
  const directionLabel =
    filters.direction === 'upload' ? '上传' : filters.direction === 'download' ? '下载' : '总流量';
  return `${scopeLabel} / ${directionLabel}`;
});

const activePresetHours = computed(() => getMatchedPresetHours(filters.from, filters.to));

const rangeParts = computed(() => formatRangeParts());

const dominantScopeLabel = computed(() => {
  if (wanTotalBytes.value === 0 && lanTotalBytes.value === 0) return '--';
  if (Math.abs(wanPercent.value - lanPercent.value) < 1) return '基本均衡';
  return wanPercent.value >= lanPercent.value ? 'WAN 为主' : 'LAN 为主';
});

const dominantScopeHint = computed(() => {
  if (wanTotalBytes.value === 0 && lanTotalBytes.value === 0) return '当前没有流量数据';
  return `差值 ${Math.abs(wanPercent.value - lanPercent.value).toFixed(1)}%`;
});

onMounted(() => {
  void loadApps();
});

async function loadApps() {
  isLoading.value = true;
  errorMessage.value = '';

  try {
    items.value = await getNetworkApps({
      from: toIsoString(filters.from),
      to: toIsoString(filters.to),
      topN: filters.topN,
      scope: filters.scope,
      direction: filters.direction
    });
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '加载网络汇总失败。';
  } finally {
    isLoading.value = false;
  }
}

function refreshApps() {
  const presetHours = activePresetHours.value;
  if (presetHours) {
    applyPreset(presetHours);
    return;
  }

  void loadApps();
}

function applyPreset(hours: number) {
  const now = new Date();
  filters.to = toLocalInputValue(now);
  filters.from = toLocalInputValue(new Date(now.getTime() - hours * 60 * 60 * 1000));
  void loadApps();
}

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(2)} MB`;
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

function formatRangeParts() {
  const from = filters.from ? new Date(filters.from) : null;
  const to = filters.to ? new Date(filters.to) : null;

  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return null;
  }

  return {
    from: from.toLocaleString(),
    to: to.toLocaleString()
  };
}

function toLocalInputValue(value: Date) {
  const timezoneOffset = value.getTimezoneOffset() * 60_000;
  return new Date(value.getTime() - timezoneOffset).toISOString().slice(0, 16);
}

function toIsoString(value: string) {
  return value ? new Date(value).toISOString() : undefined;
}

function getMatchedPresetHours(fromValue: string, toValue: string) {
  const from = fromValue ? new Date(fromValue) : null;
  const to = toValue ? new Date(toValue) : null;

  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return null;
  }

  const diffHours = (to.getTime() - from.getTime()) / (60 * 60 * 1000);
  const rounded = Math.round(diffHours * 100) / 100;
  return presetOptions.find((preset) => rounded === preset.hours)?.hours ?? null;
}

function getRankingValue(item: AppTrafficSummaryDto) {
  if (filters.scope === 'wan') {
    if (filters.direction === 'upload') return item.wanUploadBytes;
    if (filters.direction === 'download') return item.wanDownloadBytes;
    return item.wanUploadBytes + item.wanDownloadBytes;
  }

  if (filters.scope === 'lan') {
    if (filters.direction === 'upload') return item.lanUploadBytes;
    if (filters.direction === 'download') return item.lanDownloadBytes;
    return item.lanUploadBytes + item.lanDownloadBytes;
  }

  if (filters.direction === 'upload') return item.totalUploadBytes;
  if (filters.direction === 'download') return item.totalDownloadBytes;
  return item.totalUploadBytes + item.totalDownloadBytes;
}
</script>
