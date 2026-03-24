<template>
  <section class="page network-page">
    <!-- 页面头部：标题、说明和手动刷新按钮 -->
    <PageHeader
      iconName="network"
      kicker="网络"
      title="网络"
      description="查看一段时间内的流量汇总、占比和应用排行。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="refreshApps">
          <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
          {{ isLoading ? '刷新中...' : '刷新' }}
        </button>
      </template>
    </PageHeader>

    <!-- 查询条件区：按工具栏分组展示时间、范围、方向和 TopN -->
    <article class="card filters-card filters-card-elevated">
      <div class="filters-toolbar">
        <section class="filter-group filter-group-time">
          <div class="filter-group-title">
            <strong>时间</strong>
            <small>选择统计区间</small>
          </div>
          <div class="filter-group-fields filter-group-fields-time">
            <label class="filter-field">
              <span>开始时间</span>
              <input v-model="filters.from" type="datetime-local" />
            </label>
            <label class="filter-field">
              <span>结束时间</span>
              <input v-model="filters.to" type="datetime-local" />
            </label>
          </div>
        </section>

        <section class="filter-group">
          <div class="filter-group-title">
            <strong>范围</strong>
            <small>选择网络范围</small>
          </div>
          <div class="filter-group-fields">
            <label class="filter-field">
              <span>统计范围</span>
              <select v-model="filters.scope">
                <option value="all">全部</option>
                <option value="wan">WAN</option>
                <option value="lan">LAN</option>
                <option value="loopback">Loopback</option>
              </select>
            </label>
          </div>
        </section>

        <section class="filter-group">
          <div class="filter-group-title">
            <strong>方向</strong>
            <small>选择统计方向</small>
          </div>
          <div class="filter-group-fields">
            <label class="filter-field">
              <span>统计方向</span>
              <select v-model="filters.direction">
                <option value="total">总流量</option>
                <option value="upload">上传</option>
                <option value="download">下载</option>
              </select>
            </label>
          </div>
        </section>

        <section class="filter-group filter-group-topn">
          <div class="filter-group-title">
            <strong>TopN</strong>
            <small>控制排行数量</small>
          </div>
          <div class="filter-group-fields">
            <label class="filter-field">
              <span>排行数量</span>
              <input v-model.number="filters.topN" type="number" min="1" max="100" />
            </label>
          </div>
        </section>
      </div>

      <div class="preset-toolbar">
        <div class="preset-toolbar-title">
          <strong>常用预设</strong>
          <small>快速切换常见时间范围</small>
        </div>
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
      </div>
    </article>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <!-- 概览指标区：展示累计上传、累计下载、应用数量和当前统计时间范围 -->
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
          <span class="metric-label metric-label-inline"><AppIcon name="apps" :size="14" />应用数量</span>
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
      <!-- 占比面板：展示 WAN / LAN / Loopback 在当前时间范围和方向下的占比 -->
      <article class="card dashboard-panel-card network-ratio-panel">
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

          <div class="ratio-summary-card ratio-summary-card-loopback">
            <div class="ratio-summary-top">
              <span class="ratio-summary-label">Loopback 流量</span>
              <span class="ratio-summary-badge">本地</span>
            </div>
            <strong>{{ formatBytes(loopbackTotalBytes) }}</strong>
            <small>{{ loopbackPercent.toFixed(1) }}%</small>
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

          <div class="ratio-item">
            <div class="ratio-header">
              <strong>Loopback</strong>
              <span>{{ loopbackPercent.toFixed(1) }}%</span>
            </div>
            <div class="ratio-track">
              <div class="ratio-bar ratio-bar-loopback" :style="{ width: `${loopbackPercent}%` }"></div>
            </div>
            <small class="muted">{{ formatBytes(loopbackTotalBytes) }}</small>
          </div>
        </div>
      </article>

      <!-- 排行面板：展示按当前 scope + direction 排序后的应用流量排行 -->
      <article class="card dashboard-panel-card network-ranking-panel">
        <div class="panel-header">
          <div class="panel-title">
            <span class="panel-icon"><AppIcon name="apps" :size="16" /></span>
            <div>
              <h3>应用流量排行</h3>
              <p class="panel-subtitle">按选择的统计范围和方向排序显示应用流量排行。</p>
            </div>
          </div>
          <span class="section-tag">{{ rankingDescription }}</span>
        </div>
        <ol class="ranking-list">
          <li
            v-for="(item, index) in topRanking"
            :key="item.appKey"
            class="ranking-item"
            :class="index < 3 ? [`ranking-item-top`, `ranking-item-top-${index + 1}`] : []"
          >
            <div class="ranking-main">
              <span class="ranking-index">{{ index + 1 }}</span>
              <strong>{{ item.displayName || item.processName }}</strong>
              <small class="muted">{{ item.processName }}</small>
            </div>
            <div class="ranking-side">
              <span class="ranking-value">{{ formatBytes(getRankingValue(item)) }}</span>
              <div class="ranking-breakdown">
                <span class="ranking-flow">
                  <AppIcon name="upload" :size="12" />
                  {{ formatBytes(getScopedUploadBytes(item)) }}
                </span>
                <span class="ranking-flow">
                  <AppIcon name="download" :size="12" />
                  {{ formatBytes(getScopedDownloadBytes(item)) }}
                </span>
              </div>
              <div class="ranking-progress" aria-hidden="true">
                <div class="ranking-progress-bar" :style="{ width: `${getRankingPercent(item)}%` }"></div>
              </div>
            </div>
          </li>
          <li v-if="!topRanking.length" class="muted">当前还没有可展示的排行数据。</li>
        </ol>
      </article>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import AppIcon from '../components/AppIcon.vue';
import PageHeader from '../components/PageHeader.vue';
import { getNetworkApps, getNetworkSummary } from '../services/api';
import type { AppTrafficSummaryDto, NetworkPeriodSummaryDto } from '../types/monitor';

// 常用时间预设，对应页面顶部的快捷时间按钮
const presetOptions = [
  { hours: 1, label: '最近 1 小时' },
  { hours: 6, label: '最近 6 小时' },
  { hours: 12, label: '最近 12 小时' },
  { hours: 24, label: '最近 24 小时' },
  { hours: 72, label: '最近 3 天' },
  { hours: 24 * 7, label: '最近 1 周' },
  { hours: 24 * 30, label: '最近 30 天' }
] as const;

// 页面主数据：应用排行、汇总卡片、占比面板、加载状态
const items = ref<AppTrafficSummaryDto[]>([]);
const summary = ref<NetworkPeriodSummaryDto | null>(null);
const overviewSummary = ref<NetworkPeriodSummaryDto | null>(null);
const totalsSummary = ref<NetworkPeriodSummaryDto | null>(null);
const isLoading = ref(false);
const errorMessage = ref('');
let autoRefreshTimer: number | null = null;

// 查询条件：分别驱动筛选区、占比面板和应用排行
const filters = reactive({
  from: toLocalInputValue(new Date(Date.now() - 24 * 60 * 60 * 1000)),
  to: toLocalInputValue(new Date()),
  topN: 10,
  scope: 'wan' as 'all' | 'wan' | 'lan' | 'loopback',
  direction: 'upload' as 'total' | 'upload' | 'download'
});

// 累计上传/下载 - 使用 totalsSummary（不受 direction 筛选影响）
const totalUploadBytes = computed(() =>
  totalsSummary.value?.totalUploadBytes ?? 0
);

const totalDownloadBytes = computed(() =>
  totalsSummary.value?.totalDownloadBytes ?? 0
);

// 占比面板数据 - 使用 overviewSummary（不受 scope 筛选影响，但受 direction 和时间范围影响）
const overviewWanTotalBytes = computed(() =>
  (overviewSummary.value?.wanUploadBytes ?? 0) + (overviewSummary.value?.wanDownloadBytes ?? 0)
);

const overviewLanTotalBytes = computed(() =>
  (overviewSummary.value?.lanUploadBytes ?? 0) + (overviewSummary.value?.lanDownloadBytes ?? 0)
);

const overviewLoopbackTotalBytes = computed(() =>
  (overviewSummary.value?.loopbackUploadBytes ?? 0) + (overviewSummary.value?.loopbackDownloadBytes ?? 0)
);

const overviewTotalBytes = computed(() =>
  overviewWanTotalBytes.value + overviewLanTotalBytes.value + overviewLoopbackTotalBytes.value
);

const wanPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewWanTotalBytes.value / overviewTotalBytes.value) * 100
);

const lanPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewLanTotalBytes.value / overviewTotalBytes.value) * 100
);

const loopbackPercent = computed(() =>
  overviewTotalBytes.value === 0 ? 0 : (overviewLoopbackTotalBytes.value / overviewTotalBytes.value) * 100
);

// 兼容旧代码，使用 overview 数据
const wanTotalBytes = overviewWanTotalBytes;
const lanTotalBytes = overviewLanTotalBytes;
const loopbackTotalBytes = overviewLoopbackTotalBytes;

const totalScopeBytes = computed(() => wanTotalBytes.value + lanTotalBytes.value + loopbackTotalBytes.value);

const topRanking = computed(() => items.value.slice(0, filters.topN));
const rankingMaxValue = computed(() =>
  topRanking.value.reduce((max, item) => Math.max(max, getRankingValue(item)), 0)
);

const rankingDescription = computed(() => {
  const scopeLabel = filters.scope === 'wan' ? 'WAN' : filters.scope === 'lan' ? 'LAN' : filters.scope === 'loopback' ? 'Loopback' : '全部';
  const directionLabel =
    filters.direction === 'upload' ? '上传' : filters.direction === 'download' ? '下载' : '总流量';
  return `${scopeLabel} / ${directionLabel}`;
});

const activePresetHours = computed(() => getMatchedPresetHours(filters.from, filters.to));

const rangeParts = computed(() => formatRangeParts());

const dominantScopeLabel = computed(() => {
  if (overviewTotalBytes.value === 0) return '--';
  const max = Math.max(wanPercent.value, lanPercent.value, loopbackPercent.value);
  if (max === loopbackPercent.value && loopbackPercent.value > 50) return 'Loopback 为主';
  if (Math.abs(wanPercent.value - lanPercent.value) < 5) return '基本均衡';
  return wanPercent.value >= lanPercent.value ? 'WAN 为主' : 'LAN 为主';
});

const dominantScopeHint = computed(() => {
  if (overviewTotalBytes.value === 0) return '当前没有流量数据';
  const max = Math.max(wanPercent.value, lanPercent.value, loopbackPercent.value);
  if (max === loopbackPercent.value && loopbackPercent.value > 50) return `本地回环占 ${loopbackPercent.value.toFixed(1)}%`;
  return `差值 ${Math.abs(wanPercent.value - lanPercent.value).toFixed(1)}%`;
});

onMounted(() => {
  void loadApps();
});

onUnmounted(() => {
  if (autoRefreshTimer !== null) {
    window.clearTimeout(autoRefreshTimer);
    autoRefreshTimer = null;
  }
});

watch(
  () => [filters.from, filters.to, filters.topN, filters.scope, filters.direction],
  () => {
    if (autoRefreshTimer !== null) {
      window.clearTimeout(autoRefreshTimer);
    }

    autoRefreshTimer = window.setTimeout(() => {
      autoRefreshTimer = null;
      void loadApps();
    }, 250);
  }
);

async function loadApps() {
  isLoading.value = true;
  errorMessage.value = '';

  try {
    const from = toIsoString(filters.from);
    const to = toIsoString(filters.to);
    // 这里集中调用网络页相关后端接口：
    // 1. /api/network/apps：获取应用流量排行
    // 2. /api/network/summary（当前 scope + 当前 direction）：获取当前筛选条件下的汇总
    // 3. /api/network/summary（scope=all + 当前 direction）：获取占比面板数据，忽略 scope，但保留时间和方向
    // 4. /api/network/summary（当前 scope + direction=total）：获取顶部累计上传/下载卡片数据，忽略方向
    const [apps, periodSummary, overviewData, totalsData] = await Promise.all([
      getNetworkApps({
        from,
        to,
        topN: filters.topN,
        scope: filters.scope,
        direction: filters.direction
      }),
      getNetworkSummary({
        from,
        to,
        scope: filters.scope,
        direction: filters.direction
      }),
      getNetworkSummary({
        from,
        to,
        scope: 'all',
        direction: filters.direction
      }),
      getNetworkSummary({
        from,
        to,
        scope: filters.scope,
        direction: 'total'  // 累计上传/下载不受 direction 影响
      })
    ]);

    items.value = apps;
    summary.value = periodSummary;
    overviewSummary.value = overviewData;
    totalsSummary.value = totalsData;
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

  if (filters.scope === 'loopback') {
    if (filters.direction === 'upload') return item.loopbackUploadBytes;
    if (filters.direction === 'download') return item.loopbackDownloadBytes;
    return item.loopbackUploadBytes + item.loopbackDownloadBytes;
  }

  if (filters.direction === 'upload') return item.totalUploadBytes;
  if (filters.direction === 'download') return item.totalDownloadBytes;
  return item.totalUploadBytes + item.totalDownloadBytes;
}

function getScopedUploadBytes(item: AppTrafficSummaryDto) {
  if (filters.scope === 'wan') return item.wanUploadBytes;
  if (filters.scope === 'lan') return item.lanUploadBytes;
  if (filters.scope === 'loopback') return item.loopbackUploadBytes;
  return item.totalUploadBytes;
}

function getScopedDownloadBytes(item: AppTrafficSummaryDto) {
  if (filters.scope === 'wan') return item.wanDownloadBytes;
  if (filters.scope === 'lan') return item.lanDownloadBytes;
  if (filters.scope === 'loopback') return item.loopbackDownloadBytes;
  return item.totalDownloadBytes;
}

function getRankingPercent(item: AppTrafficSummaryDto) {
  const value = getRankingValue(item);
  if (rankingMaxValue.value <= 0) {
    return 0;
  }

  if (value <= 0) {
    return 0;
  }

  return Math.max(6, (value / rankingMaxValue.value) * 100);
}
</script>
