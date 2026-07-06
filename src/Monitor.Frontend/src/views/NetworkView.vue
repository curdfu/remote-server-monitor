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
    <article class="card filters-card filters-card-elevated network-section network-section-filters">
      <div id="network-filters-toolbar" v-show="showAdvancedFilters" class="filters-toolbar">
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

      <button
        v-if="isMobileViewport"
        type="button"
        class="ghost-button network-mobile-toggle"
        :class="{ 'network-mobile-toggle-active': isMobileFiltersExpanded }"
        :aria-expanded="isMobileFiltersExpanded"
        aria-controls="network-filters-toolbar"
        @click="toggleMobileFilters"
      >
        <span>{{ isMobileFiltersExpanded ? '收起更多筛选' : '展开更多筛选' }}</span>
        <span class="network-mobile-toggle-icon" aria-hidden="true">{{ isMobileFiltersExpanded ? '▴' : '▾' }}</span>
      </button>
    </article>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <section class="panel-grid network-section network-section-analytics">
      <div class="network-section network-section-column network-section-column-analytics">
        <article class="card dashboard-panel-card network-section network-section-upload-download">
          <div class="panel-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="traffic" :size="16" /></span>
              <div>
                <h3>累计上传 / 下载</h3>
                <p class="panel-subtitle">基于累计总流量展示上传与下载方向的整体分布。</p>
              </div>
            </div>
            <span class="section-tag">仅基于累计总量</span>
          </div>

          <div class="ratio-overview">
            <div class="ratio-summary-card ratio-summary-card-wan">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">累计上传</span>
                <span class="ratio-summary-badge">发送</span>
              </div>
              <strong>{{ formatBytes(totalUploadBytes) }}</strong>
              <small>{{ uploadPercent.toFixed(1) }}%</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-lan">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">累计下载</span>
                <span class="ratio-summary-badge">接收</span>
              </div>
              <strong>{{ formatBytes(totalDownloadBytes) }}</strong>
              <small>{{ downloadPercent.toFixed(1) }}%</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-neutral">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">累计总流量</span>
                <span class="ratio-summary-badge">总计</span>
              </div>
              <strong>{{ formatBytes(uploadDownloadTotalBytes) }}</strong>
              <small>上传 + 下载</small>
            </div>

            <div class="ratio-summary-card ratio-summary-card-neutral">
              <div class="ratio-summary-top">
                <span class="ratio-summary-label">主导方向</span>
                <span class="ratio-summary-badge">概览</span>
              </div>
              <strong>{{ dominantTrafficDirectionLabel }}</strong>
              <small>{{ dominantTrafficDirectionHint }}</small>
            </div>
          </div>

          <div class="ratio-group">
            <div class="ratio-item">
              <div class="ratio-header">
                <strong>上传</strong>
                <span>{{ uploadPercent.toFixed(1) }}%</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-wan" :style="{ width: `${uploadPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(totalUploadBytes) }}</small>
            </div>

            <div class="ratio-item">
              <div class="ratio-header">
                <strong>下载</strong>
                <span>{{ downloadPercent.toFixed(1) }}%</span>
              </div>
              <div class="ratio-track">
                <div class="ratio-bar ratio-bar-lan" :style="{ width: `${downloadPercent}%` }"></div>
              </div>
              <small class="muted">{{ formatBytes(totalDownloadBytes) }}</small>
            </div>
          </div>
        </article>

        <!-- 占比面板：展示 WAN / LAN / Loopback 在当前时间范围和方向下的占比 -->
        <article class="card dashboard-panel-card network-ratio-panel network-section network-section-wan-lan">
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
      </div>

      <div class="network-section network-section-column network-section-column-ranking">
        <!-- 排行面板：展示按当前 scope + direction 排序后的应用流量排行 -->
        <article class="card dashboard-panel-card network-ranking-panel network-section network-section-ranking">
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

          <div class="ranking-meta">
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

          <ol v-if="showRankingSkeleton" class="ranking-list ranking-list-skeleton" aria-hidden="true">
            <li v-for="placeholder in rankingSkeletonRows" :key="placeholder" class="ranking-item ranking-item-skeleton">
              <div class="ranking-main">
                <span class="ranking-index ranking-skeleton-badge"></span>
                <span class="ranking-skeleton-block ranking-skeleton-title"></span>
                <span class="ranking-skeleton-block ranking-skeleton-subtitle"></span>
              </div>
              <div class="ranking-side">
                <span class="ranking-skeleton-block ranking-skeleton-value"></span>
                <div class="ranking-breakdown">
                  <span class="ranking-skeleton-block ranking-skeleton-flow"></span>
                  <span class="ranking-skeleton-block ranking-skeleton-flow"></span>
                </div>
                <div class="ranking-progress ranking-progress-skeleton">
                  <div class="ranking-progress-bar ranking-progress-bar-skeleton"></div>
                </div>
              </div>
            </li>
          </ol>

          <ol v-else class="ranking-list">
            <li
              v-for="(item, index) in topRanking"
              :key="item.appKey"
              class="ranking-item"
              :class="[
                index < 3 ? [`ranking-item-top`, `ranking-item-top-${index + 1}`] : [],
                { 'ranking-item-active': selectedApp?.appKey === item.appKey }
              ]"
              role="button"
              tabindex="0"
              :aria-pressed="selectedApp?.appKey === item.appKey"
              @click="selectApp(item)"
              @keydown.enter.prevent="selectApp(item)"
              @keydown.space.prevent="selectApp(item)"
            >
              <div class="ranking-main">
                <span class="ranking-index">{{ index + 1 }}</span>
                <strong>{{ item.displayName || item.processName }}</strong>
                <small class="muted">{{ item.executablePath || item.processName }}</small>
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
      </div>
    </section>

    <Teleport to="body">
      <div v-if="selectedApp" class="app-segments-overlay" @click.self="closeAppSegmentsPanel">
        <section class="app-segments-panel" role="dialog" aria-modal="true" aria-live="polite">
          <button type="button" class="app-segments-close" aria-label="关闭应用流量明细" @click="closeAppSegmentsPanel">
            <AppIcon name="close" :size="16" />
          </button>

          <div class="app-segments-header">
            <div class="app-segments-title">
              <span class="panel-icon"><AppIcon name="network" :size="16" /></span>
              <div>
                <h4>{{ selectedApp.displayName || selectedApp.processName }}</h4>
                <p class="panel-subtitle">{{ selectedApp.executablePath || selectedApp.processName }}</p>
              </div>
            </div>
            <div class="app-segments-actions">
              <div class="app-segments-tags">
                <span class="section-tag">{{ segmentDurationLabel }}</span>
                <span class="section-tag">{{ appSegments.length }} 段</span>
              </div>
            </div>
          </div>

          <p v-if="selectedAppOutsideRanking" class="app-segments-note">
            该应用不在当前排行范围内，详情仍按当前筛选条件查询。
          </p>

          <div v-if="segmentsErrorMessage" class="app-segments-state app-segments-error">
            {{ segmentsErrorMessage }}
          </div>
          <div v-else-if="isSegmentsLoading" class="app-segments-state">
            正在加载应用分段流量...
          </div>
          <div v-else-if="!appSegments.length" class="app-segments-state">
            当前筛选范围内没有该应用的分段流量。
          </div>
          <div v-else class="app-segments-chart" aria-label="应用分段流量柱状图">
            <div class="app-segments-chart-grid" aria-hidden="true"></div>
            <ol class="app-segments-bars">
              <li
                v-for="(segment, segmentIndex) in appSegments"
                :key="`${segment.from}-${segment.to}`"
                class="app-segment-bar-item"
                :class="getSegmentTooltipPlacementClass(segmentIndex)"
              >
                <div
                  class="app-segment-bar"
                  :style="{ height: `${getSegmentPercent(segment)}%` }"
                  tabindex="0"
                  :aria-label="getSegmentTooltip(segment)"
                >
                  <span class="app-segment-tooltip">{{ getSegmentTooltip(segment) }}</span>
                </div>
                <span class="app-segment-axis-label">{{ formatSegmentAxisLabel(segment) }}</span>
              </li>
            </ol>
          </div>
        </section>
      </div>
    </Teleport>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import AppIcon from '../components/AppIcon.vue';
import PageHeader from '../components/PageHeader.vue';
import { getNetworkAppSegments, getNetworkApps, getNetworkSummary } from '../services/api';
import type { AppTrafficSegmentDto, AppTrafficSummaryDto, NetworkPeriodSummaryDto } from '../types/monitor';

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
const selectedApp = ref<AppTrafficSummaryDto | null>(null);
const appSegments = ref<AppTrafficSegmentDto[]>([]);
const overviewSummary = ref<NetworkPeriodSummaryDto | null>(null);
const totalsSummary = ref<NetworkPeriodSummaryDto | null>(null);
const isLoading = ref(false);
const isSegmentsLoading = ref(false);
const loadingSource = ref<'filter' | 'manual'>('filter');
const errorMessage = ref('');
const segmentsErrorMessage = ref('');
const isMobileViewport = ref(false);
const isMobileFiltersExpanded = ref(false);
let autoRefreshTimer: number | null = null;
let mobileViewportQuery: MediaQueryList | null = null;
let pendingReloadSource: 'filter' | 'manual' = 'filter';
// 应用明细请求可能被切换筛选条件、关闭弹层或重新选择应用打断；版本号用于丢弃过期响应。
let segmentsRequestVersion = 0;

// 查询条件：分别驱动筛选区、占比面板和应用排行
const filters = reactive({
  from: toLocalInputValue(new Date(Date.now() - 24 * 60 * 60 * 1000)),
  to: toLocalInputValue(new Date()),
  topN: 10,
  scope: 'wan' as 'all' | 'wan' | 'lan' | 'loopback',
  direction: 'upload' as 'total' | 'upload' | 'download'
});

// 累计上传/下载 - 使用 totalsSummary（保持当前 scope，但不受 direction 筛选影响）
const totalUploadBytes = computed(() =>
  totalsSummary.value?.totalUploadBytes ?? 0
);

const totalDownloadBytes = computed(() =>
  totalsSummary.value?.totalDownloadBytes ?? 0
);

const uploadDownloadTotalBytes = computed(() =>
  totalUploadBytes.value + totalDownloadBytes.value
);

const uploadPercent = computed(() =>
  uploadDownloadTotalBytes.value === 0 ? 0 : (totalUploadBytes.value / uploadDownloadTotalBytes.value) * 100
);

const downloadPercent = computed(() =>
  uploadDownloadTotalBytes.value === 0 ? 0 : (totalDownloadBytes.value / uploadDownloadTotalBytes.value) * 100
);

const dominantTrafficDirectionLabel = computed(() => {
  if (uploadDownloadTotalBytes.value === 0) return '--';
  if (Math.abs(uploadPercent.value - downloadPercent.value) < 5) return '基本均衡';
  return uploadPercent.value >= downloadPercent.value ? '上传为主' : '下载为主';
});

const dominantTrafficDirectionHint = computed(() => {
  if (uploadDownloadTotalBytes.value === 0) return '当前没有流量数据';
  return `差值 ${Math.abs(uploadPercent.value - downloadPercent.value).toFixed(1)}%`;
});

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

const topRanking = computed(() => items.value.slice(0, filters.topN));
const rankingSkeletonRows = [1, 2, 3, 4] as const;
const showRankingSkeleton = computed(() => isLoading.value && (!topRanking.value.length || loadingSource.value === 'filter'));
const rankingMaxValue = computed(() =>
  topRanking.value.reduce((max, item) => Math.max(max, getRankingValue(item)), 0)
);
const segmentMaxValue = computed(() =>
  appSegments.value.reduce((max, segment) => Math.max(max, getSegmentValue(segment)), 0)
);

const rankingDescription = computed(() => {
  const scopeLabel = filters.scope === 'wan' ? 'WAN' : filters.scope === 'lan' ? 'LAN' : filters.scope === 'loopback' ? 'Loopback' : '全部';
  const directionLabel =
    filters.direction === 'upload' ? '上传' : filters.direction === 'download' ? '下载' : '总流量';
  return `${scopeLabel} / ${directionLabel}`;
});

const segmentDurationLabel = computed(() => {
  const from = filters.from ? new Date(filters.from) : null;
  const to = filters.to ? new Date(filters.to) : null;
  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return '--';
  }

  return to.getTime() - from.getTime() <= 24 * 60 * 60 * 1000 ? '1 小时' : '12 小时';
});

const selectedAppOutsideRanking = computed(() =>
  selectedApp.value !== null && !topRanking.value.some((item) => item.appKey === selectedApp.value?.appKey)
);

const activePresetHours = computed(() => getMatchedPresetHours(filters.from, filters.to));
const showAdvancedFilters = computed(() => !isMobileViewport.value || isMobileFiltersExpanded.value);

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
  if (typeof window !== 'undefined' && 'matchMedia' in window) {
    mobileViewportQuery = window.matchMedia('(max-width: 720px)');
    syncMobileViewportState(mobileViewportQuery.matches);
    mobileViewportQuery.addEventListener('change', handleMobileViewportChange);
  }

  void loadApps('filter');
});

onUnmounted(() => {
  segmentsRequestVersion++;

  if (autoRefreshTimer !== null) {
    window.clearTimeout(autoRefreshTimer);
    autoRefreshTimer = null;
  }

  if (mobileViewportQuery) {
    mobileViewportQuery.removeEventListener('change', handleMobileViewportChange);
    mobileViewportQuery = null;
  }
});

watch(
  () => [filters.from, filters.to, filters.topN, filters.scope, filters.direction],
  () => {
    // 多个筛选控件可能连续变化，统一 debounce 后再请求，减少无效接口调用和骨架屏闪烁。
    if (autoRefreshTimer !== null) {
      window.clearTimeout(autoRefreshTimer);
    }

    autoRefreshTimer = window.setTimeout(() => {
      autoRefreshTimer = null;
      const source = pendingReloadSource;
      pendingReloadSource = 'filter';
      void loadApps(source);
    }, 250);
  }
);

async function loadApps(source: 'filter' | 'manual' = 'filter') {
  loadingSource.value = source;
  isLoading.value = true;
  errorMessage.value = '';

  try {
    const from = toIsoString(filters.from);
    const to = toIsoString(filters.to);
    // 这里集中调用网络页相关后端接口：
    // 1. /api/network/apps：获取应用流量排行
    // 2. /api/network/summary（scope=all + 当前 direction）：获取占比面板数据，忽略 scope，但保留时间和方向
    // 3. /api/network/summary（scope=当前 scope + direction=total）：获取累计上传/下载卡片数据，保持当前范围并忽略方向
    const [apps, overviewData, totalsData] = await Promise.all([
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
        scope: 'all',
        direction: filters.direction
      }),
      getNetworkSummary({
        from,
        to,
        scope: filters.scope,
        direction: 'total'
      })
    ]);

    items.value = apps;
    overviewSummary.value = overviewData;
    totalsSummary.value = totalsData;
    syncSelectedAppAfterRankingLoad(apps);
    if (selectedApp.value) {
      void loadSelectedAppSegments();
    }
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '加载网络汇总失败。';
  } finally {
    isLoading.value = false;
  }
}

function refreshApps() {
  // 当前时间范围命中预设时，手动刷新会先重算“到当前时间”的范围，再由 watcher 触发加载。
  const presetHours = activePresetHours.value;
  if (presetHours) {
    pendingReloadSource = 'manual';
    applyPreset(presetHours);
    return;
  }

  void loadApps('manual');
}

function selectApp(item: AppTrafficSummaryDto) {
  selectedApp.value = item;
  void loadSelectedAppSegments();
}

function closeAppSegmentsPanel() {
  selectedApp.value = null;
  appSegments.value = [];
  segmentsErrorMessage.value = '';
  isSegmentsLoading.value = false;
  segmentsRequestVersion++;
}

async function loadSelectedAppSegments() {
  const app = selectedApp.value;
  if (!app) {
    appSegments.value = [];
    segmentsErrorMessage.value = '';
    return;
  }

  // 记录本次请求版本；响应回来时只有仍是最新版本，才允许写入弹层状态。
  const requestVersion = ++segmentsRequestVersion;
  isSegmentsLoading.value = true;
  segmentsErrorMessage.value = '';

  try {
    const segments = await getNetworkAppSegments(app.appKey, {
      from: toIsoString(filters.from),
      to: toIsoString(filters.to),
      scope: filters.scope,
      direction: filters.direction
    });

    if (requestVersion !== segmentsRequestVersion) {
      return;
    }

    appSegments.value = segments;
  } catch (error) {
    if (requestVersion !== segmentsRequestVersion) {
      return;
    }

    segmentsErrorMessage.value = error instanceof Error ? error.message : '加载应用分段流量失败。';
    appSegments.value = [];
  } finally {
    if (requestVersion === segmentsRequestVersion) {
      isSegmentsLoading.value = false;
    }
  }
}

function syncSelectedAppAfterRankingLoad(apps: AppTrafficSummaryDto[]) {
  // 排行刷新后如果选中应用仍在结果中，用新汇总数据替换旧对象，保持弹层标题和数值同步。
  if (!selectedApp.value) {
    return;
  }

  const refreshedApp = apps.find((item) => item.appKey === selectedApp.value?.appKey);
  if (refreshedApp) {
    selectedApp.value = refreshedApp;
  }
}

function toggleMobileFilters() {
  if (!isMobileViewport.value) {
    return;
  }

  isMobileFiltersExpanded.value = !isMobileFiltersExpanded.value;
}

function handleMobileViewportChange(event: MediaQueryListEvent) {
  syncMobileViewportState(event.matches);
}

function syncMobileViewportState(matches: boolean) {
  isMobileViewport.value = matches;
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

function getSegmentValue(segment: AppTrafficSegmentDto) {
  if (filters.scope === 'wan') {
    if (filters.direction === 'upload') return segment.wanUploadBytes;
    if (filters.direction === 'download') return segment.wanDownloadBytes;
    return segment.wanUploadBytes + segment.wanDownloadBytes;
  }

  if (filters.scope === 'lan') {
    if (filters.direction === 'upload') return segment.lanUploadBytes;
    if (filters.direction === 'download') return segment.lanDownloadBytes;
    return segment.lanUploadBytes + segment.lanDownloadBytes;
  }

  if (filters.scope === 'loopback') {
    if (filters.direction === 'upload') return segment.loopbackUploadBytes;
    if (filters.direction === 'download') return segment.loopbackDownloadBytes;
    return segment.loopbackUploadBytes + segment.loopbackDownloadBytes;
  }

  if (filters.direction === 'upload') return segment.totalUploadBytes;
  if (filters.direction === 'download') return segment.totalDownloadBytes;
  return segment.totalUploadBytes + segment.totalDownloadBytes;
}

function getSegmentUploadBytes(segment: AppTrafficSegmentDto) {
  if (filters.scope === 'wan') return segment.wanUploadBytes;
  if (filters.scope === 'lan') return segment.lanUploadBytes;
  if (filters.scope === 'loopback') return segment.loopbackUploadBytes;
  return segment.totalUploadBytes;
}

function getSegmentDownloadBytes(segment: AppTrafficSegmentDto) {
  if (filters.scope === 'wan') return segment.wanDownloadBytes;
  if (filters.scope === 'lan') return segment.lanDownloadBytes;
  if (filters.scope === 'loopback') return segment.loopbackDownloadBytes;
  return segment.totalDownloadBytes;
}

function getSegmentPercent(segment: AppTrafficSegmentDto) {
  const value = getSegmentValue(segment);
  if (segmentMaxValue.value <= 0 || value <= 0) {
    return 0;
  }

  return Math.max(6, (value / segmentMaxValue.value) * 100);
}

function formatSegmentRange(segment: AppTrafficSegmentDto) {
  const from = new Date(segment.from);
  const to = new Date(segment.to);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return '--';
  }

  return `${from.toLocaleString()} ~ ${to.toLocaleString()}`;
}

function formatSegmentAxisLabel(segment: AppTrafficSegmentDto) {
  const from = new Date(segment.from);
  if (Number.isNaN(from.getTime())) {
    return '--';
  }

  const month = String(from.getMonth() + 1).padStart(2, '0');
  const day = String(from.getDate()).padStart(2, '0');
  const hour = String(from.getHours()).padStart(2, '0');
  return segmentDurationLabel.value === '1 小时' ? `${hour}:00` : `${month}-${day}`;
}

function getSegmentTooltip(segment: AppTrafficSegmentDto) {
  return [
    formatSegmentRange(segment),
    `流量 ${formatBytes(getSegmentValue(segment))}`,
    `上传 ${formatBytes(getSegmentUploadBytes(segment))}`,
    `下载 ${formatBytes(getSegmentDownloadBytes(segment))}`
  ].join('\n');
}

function getSegmentTooltipPlacementClass(index: number) {
  if (index < 2) {
    return 'app-segment-bar-item-left-edge';
  }

  if (index >= appSegments.value.length - 2) {
    return 'app-segment-bar-item-right-edge';
  }

  return '';
}
</script>
