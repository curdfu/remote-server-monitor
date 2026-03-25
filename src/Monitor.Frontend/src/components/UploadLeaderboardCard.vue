<template>
  <article class="card homepage-leaderboard-card dashboard-surface-card">
    <header class="homepage-leaderboard-head">
      <div class="homepage-leaderboard-title">
        <span class="section-tag">Upload Focus</span>
        <div>
          <h3>当前上传 Top Apps</h3>
          <p class="panel-subtitle">快速查看最近时间窗内上传量最高的应用。</p>
        </div>
      </div>

      <div class="homepage-window-chips" role="tablist" aria-label="上传排行时间范围">
        <button
          v-for="hours in windowOptions"
          :key="hours"
          type="button"
          class="chip-button"
          :class="{ 'chip-button-active': selectedWindowHours === hours }"
          :disabled="isLoading && selectedWindowHours === hours"
          :aria-pressed="selectedWindowHours === hours"
          @click="loadLeaderboard(hours)"
        >
          {{ hours }} 小时
        </button>
      </div>
    </header>

    <div class="homepage-leaderboard-meta-row">
      <p class="homepage-leaderboard-meta">
        {{ windowSummaryText }}
      </p>
      <p v-if="lastLoadedAt" class="homepage-leaderboard-meta homepage-leaderboard-meta-quiet">
        刷新于 {{ formatDateTime(lastLoadedAt) }}
      </p>
    </div>

    <div v-if="errorMessage" class="homepage-leaderboard-state homepage-leaderboard-state-error">
      {{ errorMessage }}
    </div>
    <div v-else-if="isLoading && !apps.length" class="homepage-leaderboard-state">
      正在加载上传排行...
    </div>
    <div v-else-if="!apps.length" class="homepage-leaderboard-state">
      当前时间窗内没有可展示的上传排行数据。
    </div>

    <ol v-else class="homepage-leaderboard-list">
      <li
        v-for="(app, index) in apps"
        :key="app.appKey"
        class="homepage-leaderboard-item"
        :class="`homepage-leaderboard-item-top-${Math.min(index + 1, 3)}`"
      >
        <div class="homepage-leaderboard-rank">{{ index + 1 }}</div>

        <div class="homepage-leaderboard-main">
          <div class="homepage-leaderboard-copy">
            <strong class="homepage-leaderboard-name">{{ app.displayName || app.processName }}</strong>
            <span class="homepage-leaderboard-process">{{ app.processName }}</span>
          </div>

          <div class="homepage-leaderboard-side">
            <strong class="homepage-leaderboard-bytes">{{ formatTraffic(getUploadBytes(app)) }}</strong>
            <span class="homepage-leaderboard-caption">上传总量</span>
          </div>
        </div>

        <div class="homepage-leaderboard-progress-track" aria-hidden="true">
          <div class="homepage-leaderboard-progress-bar" :style="{ width: `${progressWidth(app)}%` }"></div>
        </div>
      </li>
    </ol>
  </article>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { getHomepageUploadLeaderboard } from '../services/api';
import type { AppTrafficSummaryDto, HomepageLeaderboardWindowHours } from '../types/monitor';

const windowOptions: HomepageLeaderboardWindowHours[] = [3, 6, 12, 24];
const selectedWindowHours = ref<HomepageLeaderboardWindowHours>(6);
const isLoading = ref(false);
const errorMessage = ref('');
const apps = ref<AppTrafficSummaryDto[]>([]);
const lastLoadedAt = ref<string | null>(null);

let requestId = 0;
let activeController: AbortController | null = null;

const maxUploadBytes = computed(() => {
  return apps.value.reduce((currentMax, app) => Math.max(currentMax, getUploadBytes(app)), 0);
});

const windowSummaryText = computed(() => `最近 ${selectedWindowHours.value} 小时 · 已按上传量排序`);

onMounted(() => {
  void loadLeaderboard(selectedWindowHours.value);
});

onUnmounted(() => {
  activeController?.abort();
});

async function loadLeaderboard(hours: HomepageLeaderboardWindowHours) {
  const currentRequestId = ++requestId;
  activeController?.abort();

  const controller = new AbortController();
  activeController = controller;
  selectedWindowHours.value = hours;
  isLoading.value = true;
  errorMessage.value = '';

  try {
    const result = await getHomepageUploadLeaderboard(hours, 8, controller.signal);
    if (currentRequestId !== requestId) {
      return;
    }

    apps.value = result;
    lastLoadedAt.value = new Date().toISOString();
  } catch (error) {
    if (controller.signal.aborted) {
      return;
    }

    errorMessage.value = error instanceof Error ? error.message : '加载上传排行失败。';
  } finally {
    if (currentRequestId === requestId) {
      isLoading.value = false;
    }
  }
}

function getUploadBytes(app: AppTrafficSummaryDto) {
  return app.totalUploadBytes;
}

function progressWidth(app: AppTrafficSummaryDto) {
  if (maxUploadBytes.value <= 0) {
    return 0;
  }

  return Math.max(10, Math.min(100, (getUploadBytes(app) / maxUploadBytes.value) * 100));
}

function formatTraffic(bytes: number) {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(2)} GB`;
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(1)} MB`;
  if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${bytes} B`;
}

function formatDateTime(value?: string | null) {
  if (!value) return '--';

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--';
  return date.toLocaleString();
}
</script>
