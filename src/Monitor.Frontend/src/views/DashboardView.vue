<template>
  <section class="page dashboard-page" aria-label="概览">
    <div v-if="errorMessage" class="card state-card error-state" role="alert">
      {{ errorMessage }}
    </div>

    <section class="dashboard-section dashboard-core-metrics-section">
      <article class="card dashboard-core-metrics-panel">
        <div class="section-header section-header-rich dashboard-core-metrics-header">
          <div>
            <h3>核心运行指标</h3>
          </div>
          <button class="ghost-button" :disabled="isLoading" @click="refreshDashboard">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="16" /></span>
            {{ isLoading ? '刷新中...' : '立即刷新' }}
          </button>
        </div>

        <div class="dashboard-core-metrics-panel-body">
          <div class="dashboard-hero-grid page-tier-stats">
            <MetricCard
              label="CPU 占用"
              :value="formatPercent(hardware?.cpuUsagePercent)"
              :hint="formatCpuNameHint(hardware?.cpuName)"
              :badge="usageBadge(hardware?.cpuUsagePercent)"
              :tone="usageTone(hardware?.cpuUsagePercent)"
            />
            <MetricCard
              label="内存已使用"
              :value="formatMemoryUsage(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
              :hint="formatMemoryHint(hardware?.memoryTotalMb)"
              :badge="memoryUsageBadge(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
              :tone="memoryUsageTone(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
            />
            <MetricCard
              label="最高磁盘温度"
              :value="formatNullable(hottestDisk?.temperatureC, '°C')"
              :hint="hottestDiskName"
              :badge="temperatureBadge(hottestDisk?.temperatureC)"
              :tone="temperatureTone(hottestDisk?.temperatureC)"
            />
            <MetricCard
              label="CPU 当前频率"
              :value="formatNullable(hardware?.cpuFrequencyMhz, 'MHz')"
              :hint="formatCpuNameHint(hardware?.cpuName)"
              tone="info"
            />
            <MetricCard
              label="CPU 当前温度"
              :value="formatNullable(hardware?.cpuTemperatureC, '°C')"
              :hint="formatTemperatureHint(hardware?.cpuTemperatureC)"
              :badge="temperatureBadge(hardware?.cpuTemperatureC)"
              :tone="temperatureTone(hardware?.cpuTemperatureC)"
            />
            <MetricCard
              label="CPU 当前功耗"
              :value="formatNullable(hardware?.cpuPowerWatts, 'W')"
              :hint="formatPowerHint(hardware?.cpuPowerWatts)"
              tone="info"
            />
          </div>
          <details class="metric-model-details">
            <summary>查看硬件型号</summary>
            <p>{{ coreMetricDetails }}</p>
          </details>
        </div>
      </article>
    </section>

    <section class="card page-tier-panel dashboard-trends-panel">
      <div class="section-header section-header-rich">
        <div>
          <h3>最近一小时趋势</h3>
          <p class="section-subtitle">真实采样记录 · 缺失区间保留空白</p>
        </div>
      </div>
      <div v-if="historyErrorMessage" class="trend-inline-state">{{ historyErrorMessage }}</div>
      <div class="trend-grid">
        <MetricTrend
          class="trend-card-primary"
          title="CPU 使用率"
          eyebrow=""
          :points="cpuTrend"
          unit="%"
          :window-end-ms="currentTimeMs"
          :sample-interval-ms="hardwareSampleIntervalMs"
          tone="signal"
        />
        <MetricTrend
          title="内存使用率"
          eyebrow=""
          :points="memoryTrend"
          unit="%"
          :window-end-ms="currentTimeMs"
          :sample-interval-ms="hardwareSampleIntervalMs"
          tone="info"
        />
        <MetricTrend
          title="CPU 温度"
          eyebrow=""
          :points="temperatureTrend"
          unit="°C"
          scale-kind="temperature"
          :window-end-ms="currentTimeMs"
          :sample-interval-ms="hardwareSampleIntervalMs"
          tone="warning"
        />
      </div>
    </section>

    <CpuProcessPanel
      :process-cpu="processCpu"
      :state="processCpuState"
      :status-text="processCpuStatusText"
      :state-message="processCpuStateMessage"
      :sample-text="processCpuSampleText"
    />

    <section class="panel-grid dashboard-storage-grid">
      <article class="card dashboard-panel-card page-tier-panel">
        <div class="panel-header">
          <div class="panel-title">
            <div>
              <h3>磁盘温度</h3>
              <p class="panel-subtitle">各物理磁盘的当前传感器读数</p>
            </div>
          </div>
          <span class="section-tag">当前 {{ hardware?.disks?.length ?? 0 }} 块</span>
        </div>

        <div class="table-shell realtime-table-shell">
          <table class="data-table">
            <thead>
              <tr>
                <th>磁盘名称</th>
                <th class="align-right">
                  <button class="table-sort-button" @click="toggleDiskSort('temperatureC')">
                    当前温度 {{ sortIndicator('temperatureC') }}
                  </button>
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="disk in sortedDisks" :key="`${disk.name}-${disk.temperatureSource ?? 'none'}`">
                <td>{{ disk.name }}</td>
                <td class="align-right">
                  <span class="status-pill status-pill-temperature" :class="temperatureToneClass(disk.temperatureC)">
                    {{ formatNullable(disk.temperatureC, '°C') }}
                  </span>
                </td>
              </tr>
              <tr v-if="!sortedDisks.length">
                <td colspan="2" class="empty-cell">当前没有可展示的磁盘温度数据。</td>
              </tr>
            </tbody>
          </table>
        </div>
      </article>

      <article class="card dashboard-panel-card page-tier-panel">
        <div class="panel-header">
          <div class="panel-title">
            <div>
              <h3>磁盘空间</h3>
              <p class="panel-subtitle">各卷的容量、已用与剩余空间</p>
            </div>
          </div>
          <span class="section-tag">按卷汇总</span>
        </div>

        <div class="table-shell realtime-table-shell disk-space-table-shell">
          <table class="data-table disk-space-table">
            <thead>
              <tr>
                <th>磁盘 / 卷</th>
                <th class="align-right">总容量</th>
                <th class="align-right">已用空间</th>
                <th class="align-right">剩余空间</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="disk in sortedDiskSpaces" :key="disk.name">
                <td data-label="磁盘 / 卷">{{ disk.name }}</td>
                <td class="align-right" data-label="总容量">{{ formatDiskSize(disk.totalBytes) }}</td>
                <td class="align-right" data-label="已用空间">
                  <div class="table-metric">
                    <div class="table-metric-head">
                      <strong>{{ formatDiskSize(disk.usedBytes) }}</strong>
                      <small class="muted">{{ formatCapacityPercent(disk.usedBytes, disk.totalBytes) }}</small>
                    </div>
                    <div class="table-metric-subrow">
                      <div class="mini-progress">
                        <div
                          class="mini-progress-bar"
                          :style="{ width: `${getCapacityPercent(disk.usedBytes, disk.totalBytes)}%` }"
                        ></div>
                      </div>
                    </div>
                  </div>
                </td>
                <td class="align-right" data-label="剩余空间">
                  <strong class="disk-space-value">{{ formatDiskSize(disk.freeBytes) }}</strong>
                </td>
              </tr>
              <tr v-if="!sortedDiskSpaces.length">
                <td colspan="4" class="empty-cell">当前没有可展示的磁盘空间数据。</td>
              </tr>
            </tbody>
          </table>
        </div>
      </article>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import AppIcon from '../components/AppIcon.vue';
import CpuProcessPanel from '../components/CpuProcessPanel.vue';
import MetricCard from '../components/MetricCard.vue';
import MetricTrend from '../components/MetricTrend.vue';
import { useTelemetryClock, useTelemetryFreshness } from '../composables/useTelemetryFreshness';
import { getHardwareHistory, getOverview, getSettings } from '../services/api';
import { startRealtimeConnection, subscribeHardwareRealtime, subscribeHardwareTopicReady, subscribeProcessCpuRealtime, subscribeRealtimeConnectionState } from '../services/realtime';
import { findHottestDisk, mergeHistorySamples } from '../utils/hardwareHistory';
import { shouldAcceptSnapshot } from '../utils/telemetryState';
import type {
  DiskSpaceDto,
  DiskTemperatureDto,
  HardwareRealtimeDto,
  ProcessCpuRealtimeDto,
  RealtimeOverviewDto
} from '../types/monitor';

const hardware = ref<RealtimeOverviewDto['hardware'] | null>(null);
const processCpu = ref<ProcessCpuRealtimeDto | null>(null);
const isLoading = ref(false);
const overviewErrorMessage = ref('');
const realtimeErrorMessage = ref('');
const settingsMetadataErrorMessage = ref('');
const errorMessage = computed(() => [
  overviewErrorMessage.value,
  realtimeErrorMessage.value,
  settingsMetadataErrorMessage.value,
  hardwareFreshnessNotice.value
].filter(Boolean).join('；'));
const historyErrorMessage = ref('');
const history = ref<HardwareRealtimeDto[]>([]);
const diskSortField = ref<'temperatureC'>('temperatureC');
const diskSortDescending = ref(true);
let unsubscribeHardware: (() => void) | null = null;
let unsubscribeProcessCpu: (() => void) | null = null;
let unsubscribeConnectionState: (() => void) | null = null;
let unsubscribeTopicReady: (() => void) | null = null;
let wasConnected = false;
let isViewActive = false;
let overviewRequestVersion = 0;
let historyRequestVersion = 0;
const hardwareSampleIntervalMs = ref<number | null>(null);
const telemetryClock = useTelemetryClock();
const currentTimeMs = telemetryClock.nowMs;

const sortedDisks = computed(() => {
  const disks = [...(hardware.value?.disks ?? [])];
  return disks.sort(compareDisks);
});

const sortedDiskSpaces = computed(() => {
  const diskSpaces = [...(hardware.value?.diskSpaces ?? [])];
  return diskSpaces.sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
});

const hottestDisk = computed(() => findHottestDisk(hardware.value?.disks ?? []));

const hottestDiskName = computed(() => {
  return hottestDisk.value?.name.trim() || '磁盘名称不可用';
});

const coreMetricDetails = computed(() =>
  `CPU：${formatCpuNameHint(hardware.value?.cpuName)}；最高温磁盘：${hottestDiskName.value}`
);

const cpuTrend = computed(() => toTrendPoints((sample) => sample.cpuUsagePercent));
const memoryTrend = computed(() => toTrendPoints((sample) => sample.memoryUsagePercent));
const temperatureTrend = computed(() => toTrendPoints((sample) => sample.cpuTemperatureC));

const hardwareSampleTime = computed(() => hardware.value?.sampleTime);
const processCpuSampleTime = computed(() => processCpu.value?.sampleTime);
const hardwareFreshness = useTelemetryFreshness(
  hardwareSampleTime,
  hardwareSampleIntervalMs,
  telemetryClock.nowMs
);
const hardwareFreshnessNotice = computed(() => {
  if (hardwareFreshness.value.state === 'stale') return '监控数据已过期，当前显示最近一次采样结果。';
  if (hardwareFreshness.value.state === 'invalid-time') return '样本时间异常，请检查主机时间与采集服务。';
  return '';
});
const processCpuFreshness = useTelemetryFreshness(
  processCpuSampleTime,
  hardwareSampleIntervalMs,
  telemetryClock.nowMs
);

const processCpuState = computed<'waiting' | 'baseline' | 'empty' | 'fresh' | 'stale' | 'error'>(() => {
  if (!processCpu.value) return 'waiting';
  if (processCpuFreshness.value.state === 'stale') return 'stale';
  if (processCpuFreshness.value.state === 'invalid-time') return 'error';
  if (!processCpu.value.isReady) return 'baseline';
  return processCpu.value.processes.length > 0 ? 'fresh' : 'empty';
});

const processCpuStatusText = computed(() => {
  switch (processCpuState.value) {
    case 'fresh': return '新鲜样本';
    case 'stale': return '样本已过期';
    case 'baseline': return '建立基线';
    case 'empty': return '暂无数据';
    case 'error': return '时间异常';
    default: return '等待采样';
  }
});

const processCpuStateMessage = computed(() => {
  switch (processCpuState.value) {
    case 'waiting': return '等待进程 CPU 采样结果。';
    case 'baseline': return '正在建立进程 CPU 采样基线，约一个采样周期后显示。';
    case 'empty': return '当前没有可展示的进程 CPU 数据，可能是权限或进程瞬时退出。';
    case 'stale': return '样本已过期，保留最近一次排行；请检查实时通道或采集服务。';
    case 'error': return '样本时间异常，暂不判断排行是否为当前状态。';
    default: return '';
  }
});

const processCpuSampleText = computed(() => {
  if (!processCpu.value?.sampleTime) return '样本时间：--';
  if (processCpuFreshness.value.state === 'invalid-time') return '样本时间异常';
  return `样本时间：${formatDateTime(processCpu.value.sampleTime)}`;
});

onMounted(() => {
  isViewActive = true;
  // 进入首页时先调用后端 /api/overview，保证首屏有完整概览数据
  void loadOverview();
  void loadHistory();
  void loadHardwareSampleInterval();
  unsubscribeConnectionState = subscribeRealtimeConnectionState((state) => {
    if (state === 'connected') {
      if (wasConnected) {
        void loadHistory();
        void loadOverview();
        void loadHardwareSampleInterval();
      }
      wasConnected = true;
    }
  });
  // 再连接后端 /hubs/monitor，后续通过 SignalR 增量刷新硬件实时数据
  void startRealtimeConnection().catch(() => {
    realtimeErrorMessage.value = 'SignalR 实时通道连接失败，将继续保留当前页面数据。';
  });

  // 服务端确认订阅后再次回填，覆盖连接成功但主题重试期间的缺口。
  unsubscribeTopicReady = subscribeHardwareTopicReady(() => { void loadHistory(); });
  // 订阅后端 hardwareRealtime 推送：首页卡片收到新数据后直接更新本地状态
  unsubscribeHardware = subscribeHardwareRealtime((hardware) => {
    if (!shouldAcceptSnapshot(windowHardwareSampleTime(), hardware.sampleTime)) return;
    realtimeErrorMessage.value = '';
    updateHardwareState(mergeHardwarePayload(hardware, hardwareState()));
    appendHistorySample(hardware);
  });
  unsubscribeProcessCpu = subscribeProcessCpuRealtime((snapshot) => {
    if (!shouldAcceptSnapshot(processCpu.value?.sampleTime, snapshot.sampleTime)) return;
    realtimeErrorMessage.value = '';
    processCpu.value = snapshot;
  });
});

onUnmounted(() => {
  isViewActive = false;
  unsubscribeHardware?.();
  unsubscribeProcessCpu?.();
  unsubscribeConnectionState?.();
  unsubscribeTopicReady?.();
});

async function loadOverview() {
  const requestVersion = ++overviewRequestVersion;
  isLoading.value = true;
  overviewErrorMessage.value = '';

  try {
    // 调用后端 /api/overview：获取首页概览所需的硬件与网络快照
    const overview = await getOverview();
    if (!isViewActive || requestVersion !== overviewRequestVersion) return;
    if (shouldAcceptSnapshot(windowHardwareSampleTime(), overview.hardware.sampleTime)) {
      updateHardwareState(mergeHardwarePayload(overview.hardware, hardwareState()));
    }
    if (overview.processCpu && shouldAcceptSnapshot(processCpu.value?.sampleTime, overview.processCpu.sampleTime)) {
      processCpu.value = overview.processCpu;
    }
  } catch (error) {
    if (isViewActive && requestVersion === overviewRequestVersion) {
      overviewErrorMessage.value = error instanceof Error ? error.message : '加载总览数据失败。';
    }
  } finally {
    if (requestVersion === overviewRequestVersion) isLoading.value = false;
  }
}

async function loadHardwareSampleInterval() {
  try {
    const settings = await getSettings();
    if (isViewActive) {
      hardwareSampleIntervalMs.value = settings.hardwareSampleIntervalMs;
      settingsMetadataErrorMessage.value = '';
    }
  } catch {
    if (isViewActive) {
      hardwareSampleIntervalMs.value = null;
      settingsMetadataErrorMessage.value = '未读取到硬件采样间隔，已使用 180 秒的保守过期阈值。';
    }
  }
}

async function loadHistory() {
  const requestVersion = ++historyRequestVersion;
  historyErrorMessage.value = '';
  const to = new Date();
  const from = new Date(to.getTime() - 60 * 60 * 1000);

  try {
    const incoming = await getHardwareHistory(from.toISOString(), to.toISOString());
    if (!isViewActive || requestVersion !== historyRequestVersion) return;
    history.value = mergeHistorySamples(history.value, incoming, Date.now());
  } catch {
    if (isViewActive && requestVersion === historyRequestVersion) {
      historyErrorMessage.value = '历史趋势暂时不可用，实时指标仍会继续更新。';
    }
  }
}

async function refreshDashboard() {
  await Promise.allSettled([loadOverview(), loadHistory()]);
}

function appendHistorySample(sample: HardwareRealtimeDto) {
  const to = Date.parse(sample.sampleTime);
  if (!Number.isFinite(to)) return;
  history.value = mergeHistorySamples(history.value, [sample], Date.now());
}

function toTrendPoints(selector: (sample: HardwareRealtimeDto) => number | null) {
  const fromMs = currentTimeMs.value - 60 * 60 * 1000;
  return history.value
    .filter((sample) => {
      const sampleMs = Date.parse(sample.sampleTime);
      return Number.isFinite(sampleMs) && sampleMs >= fromMs && sampleMs <= currentTimeMs.value;
    })
    .map((sample) => ({
      time: sample.sampleTime,
      value: selector(sample)
    }));
}

function windowHardwareSampleTime() {
  return hardware.value?.sampleTime ?? null;
}

function formatPercent(value?: number | null) {
  return value == null || !Number.isFinite(value) ? '--' : `${value.toFixed(1)}%`;
}

function formatNullable(value: number | null | undefined, unit: string) {
  return value == null || !Number.isFinite(value) ? '--' : `${value.toFixed(1)} ${unit}`;
}

function formatMemoryUsage(value?: number | null, total?: number | null) {
  if (value == null || !Number.isFinite(value)) return '--';

  if (total != null && Number.isFinite(total) && total < 512) {
    return `${value.toFixed(1)} GB`;
  }

  if (value < 1024) return `${value.toFixed(1)} MB`;
  return `${(value / 1024).toFixed(1)} GB`;
}

function formatMemoryHint(total?: number | null) {
  if (total == null || !Number.isFinite(total)) {
    return '总容量 --';
  }

  return `总容量 ${total < 1024 ? `${total.toFixed(0)} GB` : `${(total / 1024).toFixed(1)} GB`}`;
}

function formatCpuNameHint(name?: string | null) {
  return name?.trim() || 'CPU 名称不可用';
}

function formatTemperatureHint(temperature?: number | null) {
  if (temperature == null) return '温度数据不可用';
  if (temperature >= 80) return '温度过高，建议检查散热';
  if (temperature >= 65) return '温度偏高，注意通风';
  return '温度正常，散热良好';
}

function formatPowerHint(power?: number | null) {
  if (power == null) return '功耗数据不可用';
  if (power >= 50) return '高负载运行';
  if (power >= 25) return '中等负载运行';
  return '低负载 / 空闲状态';
}

function formatDiskSize(value?: number | null) {
  if (value == null || !Number.isFinite(value) || value <= 0) return '--';

  const tebibyte = 1024 ** 4;
  const gibibyte = 1024 ** 3;

  if (value >= tebibyte) {
    return `${(value / tebibyte).toFixed(2)} TB`;
  }

  return `${(value / gibibyte).toFixed(1)} GB`;
}

function formatCapacityPercent(usedBytes?: number | null, totalBytes?: number | null) {
  const percent = getCapacityPercent(usedBytes, totalBytes);
  return percent == null ? '--' : `${percent.toFixed(1)}%`;
}

function formatDateTime(value?: string | null) {
  if (!value) return '--';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--';
  return date.toLocaleString();
}

function getCapacityPercent(usedBytes?: number | null, totalBytes?: number | null) {
  if (
    usedBytes == null || totalBytes == null ||
    !Number.isFinite(usedBytes) || !Number.isFinite(totalBytes) || totalBytes <= 0
  ) return null;
  return Math.max(0, Math.min(100, (usedBytes / totalBytes) * 100));
}

function getMemoryUsagePercent(usedMb?: number | null, totalMb?: number | null) {
  if (
    usedMb == null || totalMb == null ||
    !Number.isFinite(usedMb) || !Number.isFinite(totalMb) || totalMb <= 0
  ) return null;
  return Math.max(0, Math.min(100, (usedMb / totalMb) * 100));
}

function temperatureTone(value?: number | null): 'default' | 'success' | 'warning' | 'danger' {
  if (value == null) return 'default';
  if (value >= 80) return 'danger';
  if (value >= 65) return 'warning';
  return 'success';
}

function temperatureBadge(value?: number | null) {
  if (value == null) return '未知';
  if (value >= 80) return '高温';
  if (value >= 65) return '偏高';
  return '正常';
}

function usageTone(value?: number | null): 'default' | 'info' | 'warning' | 'danger' {
  if (value == null) return 'default';
  if (value >= 90) return 'danger';
  if (value >= 70) return 'warning';
  return 'info';
}

function usageBadge(value?: number | null) {
  if (value == null) return '未知';
  if (value >= 90) return '繁忙';
  if (value >= 70) return '较高';
  return '平稳';
}

function memoryUsageTone(usedMb?: number | null, totalMb?: number | null): 'default' | 'info' | 'warning' | 'danger' {
  const percent = getMemoryUsagePercent(usedMb, totalMb);
  if (percent == null) return 'default';
  if (percent >= 90) return 'danger';
  if (percent >= 75) return 'warning';
  return 'info';
}

function memoryUsageBadge(usedMb?: number | null, totalMb?: number | null) {
  const percent = getMemoryUsagePercent(usedMb, totalMb);
  if (percent == null) return '未知';
  if (percent >= 90) return '紧张';
  if (percent >= 75) return '偏高';
  return '正常';
}

function temperatureToneClass(value?: number | null) {
  const tone = temperatureTone(value);
  return `status-pill-${tone}`;
}

function toggleDiskSort(field: 'temperatureC') {
  if (diskSortField.value === field) {
    diskSortDescending.value = !diskSortDescending.value;
    return;
  }

  diskSortField.value = field;
  diskSortDescending.value = true;
}

function sortIndicator(field: 'temperatureC') {
  if (diskSortField.value !== field) return '↕';
  return diskSortDescending.value ? '↓' : '↑';
}

function compareDisks(left: DiskTemperatureDto, right: DiskTemperatureDto) {
  const primary = compareNullableNumber(left[diskSortField.value], right[diskSortField.value], diskSortDescending.value);
  if (primary !== 0) {
    return primary;
  }

  return left.name.localeCompare(right.name, 'zh-CN');
}

function compareNullableNumber(
  left: number | null | undefined,
  right: number | null | undefined,
  descending: boolean
) {
  const leftMissing = left == null;
  const rightMissing = right == null;

  if (leftMissing && rightMissing) return 0;
  if (leftMissing) return 1;
  if (rightMissing) return -1;

  return descending ? right - left : left - right;
}

function mergeHardwareDisks(
  hardware: RealtimeOverviewDto['hardware'],
  previousDisks: DiskTemperatureDto[]
) {
  const previousByName = new Map(previousDisks.map((disk) => [disk.name, disk]));

  return {
    ...hardware,
    disks: hardware.disks.map((disk) => {
      const previous = previousByName.get(disk.name);
      return {
        ...disk,
        sizeBytes: disk.sizeBytes ?? previous?.sizeBytes ?? null,
        usedBytes: disk.usedBytes ?? previous?.usedBytes ?? null
      };
    }),
    diskSpaces: hardware.diskSpaces
  };
}

function mergeHardwarePayload(
  hardware: RealtimeOverviewDto['hardware'],
  previousHardware?: RealtimeOverviewDto['hardware']
) {
  // SignalR 推送有时只带最新传感器值，磁盘容量/已用空间可能为空；这里保留上一帧可用的磁盘补充字段。
  return {
    ...mergeHardwareDisks(hardware, previousHardware?.disks ?? []),
    diskSpaces: mergeDiskSpaces(hardware.diskSpaces, previousHardware?.diskSpaces ?? [])
  };
}

function mergeDiskSpaces(currentDiskSpaces: DiskSpaceDto[], previousDiskSpaces: DiskSpaceDto[]) {
  return currentDiskSpaces.length > 0 ? currentDiskSpaces : previousDiskSpaces;
}

function hardwareState() {
  return hardware.value ?? undefined;
}

function updateHardwareState(nextHardware: RealtimeOverviewDto['hardware']) {
  if (!hardware.value) {
    hardware.value = nextHardware;
    return;
  }

  applyHardwarePayload(hardware.value, nextHardware);
}

function applyHardwarePayload(
  target: RealtimeOverviewDto['hardware'],
  next: RealtimeOverviewDto['hardware']
) {
  // 保持 disks/diskSpaces 数组引用稳定，避免表格和卡片在实时刷新时出现不必要的 DOM 重建。
  Object.assign(target, next, {
    disks: target.disks,
    diskSpaces: target.diskSpaces
  });

  syncNamedItems(target.disks, next.disks);
  syncNamedItems(target.diskSpaces, next.diskSpaces);
}

function syncNamedItems<T extends { name: string }>(target: T[], next: T[]) {
  // 按名称复用已有对象，既更新内容，又尽量保留 Vue 对列表项的响应式跟踪。
  const existingByName = new Map(target.map((item) => [item.name, item]));
  const normalizedItems = next.map((item) => {
    const existing = existingByName.get(item.name);
    if (existing) {
      Object.assign(existing, item);
      return existing;
    }

    return { ...item };
  });

  target.splice(0, target.length, ...normalizedItems);
}
</script>
