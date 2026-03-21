<template>
  <section class="page">
    <PageHeader
      kicker="Network"
      title="网络页"
      description="当前聚焦累计流量查询，保留按时间区间查看总量、占比和 Top N 排行。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="loadApps">
          {{ isLoading ? '查询中...' : '查询' }}
        </button>
      </template>
    </PageHeader>

    <article class="card filters-card">
      <label>
        开始时间
        <input v-model="filters.from" type="datetime-local" />
      </label>
      <label>
        结束时间
        <input v-model="filters.to" type="datetime-local" />
      </label>
      <label>
        Top N
        <input v-model.number="filters.topN" type="number" min="1" max="100" />
      </label>
      <label>
        范围
        <select v-model="filters.scope">
          <option value="all">全部</option>
          <option value="wan">WAN</option>
          <option value="lan">LAN</option>
        </select>
      </label>
      <label>
        方向
        <select v-model="filters.direction">
          <option value="total">总量</option>
          <option value="upload">上行</option>
          <option value="download">下行</option>
        </select>
      </label>
    </article>

    <div class="preset-row">
      <button class="chip-button" @click="applyPreset(1)">最近 1 小时</button>
      <button class="chip-button" @click="applyPreset(6)">最近 6 小时</button>
      <button class="chip-button" @click="applyPreset(24)">最近 24 小时</button>
    </div>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <div class="grid">
      <div class="card metric-card">
        <span class="metric-label">总上传流量</span>
        <strong class="metric-value">{{ formatBytes(totalUploadBytes) }}</strong>
      </div>
      <div class="card metric-card">
        <span class="metric-label">总下载流量</span>
        <strong class="metric-value">{{ formatBytes(totalDownloadBytes) }}</strong>
      </div>
      <div class="card metric-card">
        <span class="metric-label">参与统计 App 数</span>
        <strong class="metric-value">{{ items.length }}</strong>
      </div>
      <div class="card metric-card">
        <span class="metric-label">查询时间范围</span>
        <strong class="metric-value metric-small">{{ formatRangeText() }}</strong>
      </div>
    </div>

    <section class="panel-grid">
      <article class="card">
        <div class="panel-header">
          <h3>WAN / LAN 占比</h3>
          <span class="muted">按当前查询结果汇总</span>
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

      <article class="card">
        <div class="panel-header">
          <h3>Top N 排行</h3>
          <span class="muted">{{ rankingDescription }}</span>
        </div>
        <ol class="ranking-list">
          <li v-for="item in topRanking" :key="item.appKey">
            <div>
              <strong>{{ item.displayName || item.processName }}</strong>
              <small class="muted">{{ item.processName }}</small>
            </div>
            <span>{{ formatBytes(getRankingValue(item)) }}</span>
          </li>
          <li v-if="!topRanking.length" class="muted">当前还没有可展示的排行数据。</li>
        </ol>
      </article>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import PageHeader from '../components/PageHeader.vue';
import { getNetworkApps } from '../services/api';
import type { AppTrafficSummaryDto } from '../types/monitor';

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
  const directionLabel = filters.direction === 'upload' ? '上行' : filters.direction === 'download' ? '下行' : '总量';
  return `${scopeLabel} / ${directionLabel} 排序`;
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

function formatRangeText() {
  const from = filters.from ? new Date(filters.from) : null;
  const to = filters.to ? new Date(filters.to) : null;

  if (!from || !to || Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
    return '--';
  }

  return `${from.toLocaleString()} ~ ${to.toLocaleString()}`;
}

function toLocalInputValue(value: Date) {
  const timezoneOffset = value.getTimezoneOffset() * 60_000;
  return new Date(value.getTime() - timezoneOffset).toISOString().slice(0, 16);
}

function toIsoString(value: string) {
  return value ? new Date(value).toISOString() : undefined;
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
