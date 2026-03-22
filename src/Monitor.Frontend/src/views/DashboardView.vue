<template>
  <section class="page">
    <PageHeader
      icon="▦"
      kicker="首页"
      title="系统概览"
      description="先看最关键的硬件状态，首页尽量保持清爽。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="loadOverview">
          {{ isLoading ? '刷新中...' : '立即刷新' }}
        </button>
      </template>
    </PageHeader>

    <div class="overview-meta card">
      <div class="overview-meta-item">
        <span class="muted">数据时间</span>
        <strong>{{ formatDateTime(overview?.hardware.sampleTime) }}</strong>
      </div>
      <div class="overview-meta-item">
        <span class="muted">刷新方式</span>
        <strong>硬件数据实时推送</strong>
      </div>
      <div class="overview-meta-item">
        <span class="muted">运行状态</span>
        <strong>{{ errorMessage ? '接口异常' : '运行中' }}</strong>
      </div>
    </div>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <section class="dashboard-section">
      <div class="section-header">
        <h3>当前硬件状态</h3>
        <span class="section-tag">CPU / 内存 / 温度 / 开机时长</span>
      </div>
      <div class="grid">
        <MetricCard
          label="已开机"
          :value="formatUptime(overview?.hardware.uptimeSeconds)"
          badge="运行"
          tone="info"
        />
        <MetricCard
          label="CPU 当前频率"
          :value="formatNullable(overview?.hardware.cpuFrequencyMhz, 'MHz')"
          badge="频率"
          tone="info"
        />
        <MetricCard
          label="CPU 当前温度"
          :value="formatNullable(overview?.hardware.cpuTemperatureC, '°C')"
          :badge="temperatureBadge(overview?.hardware.cpuTemperatureC)"
          :tone="temperatureTone(overview?.hardware.cpuTemperatureC)"
        />
        <MetricCard
          label="CPU 当前功耗"
          :value="formatNullable(overview?.hardware.cpuPowerWatts, 'W')"
          badge="功耗"
          tone="info"
        />
        <MetricCard
          label="CPU 占用"
          :value="formatPercent(overview?.hardware.cpuUsagePercent)"
          :badge="usageBadge(overview?.hardware.cpuUsagePercent)"
          :tone="usageTone(overview?.hardware.cpuUsagePercent)"
        />
        <MetricCard
          label="内存已使用"
          :value="formatMemoryUsage(overview?.hardware.memoryUsedMb, overview?.hardware.memoryTotalMb)"
          :badge="memoryUsageBadge(overview?.hardware.memoryUsedMb, overview?.hardware.memoryTotalMb)"
          :tone="memoryUsageTone(overview?.hardware.memoryUsedMb, overview?.hardware.memoryTotalMb)"
        />
        <MetricCard
          label="最热磁盘温度"
          :value="formatNullable(overview?.hardware.diskTemperatureC, '°C')"
          :badge="temperatureBadge(overview?.hardware.diskTemperatureC)"
          :tone="temperatureTone(overview?.hardware.diskTemperatureC)"
        />
      </div>
    </section>

    <section class="panel-grid panel-grid-single">
      <article class="card">
        <div class="panel-header">
          <h3>磁盘温度</h3>
          <span class="section-tag">当前 {{ overview?.hardware.disks?.length ?? 0 }} 块</span>
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

      <article class="card">
        <div class="panel-header">
          <h3>磁盘空间</h3>
          <span class="section-tag">按卷汇总（例如 C: / D:）</span>
        </div>

        <div class="table-shell realtime-table-shell">
          <table class="data-table">
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
                <td>{{ disk.name }}</td>
                <td class="align-right">{{ formatDiskSize(disk.totalBytes) }}</td>
                <td class="align-right">
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
                <td class="align-right">
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
import MetricCard from '../components/MetricCard.vue';
import PageHeader from '../components/PageHeader.vue';
import { getOverview } from '../services/api';
import { startRealtimeConnection, subscribeHardwareRealtime } from '../services/realtime';
import type { DiskSpaceDto, DiskTemperatureDto, RealtimeOverviewDto } from '../types/monitor';

const overview = ref<RealtimeOverviewDto | null>(null);
const isLoading = ref(false);
const errorMessage = ref('');
const diskSortField = ref<'temperatureC'>('temperatureC');
const diskSortDescending = ref(true);
let unsubscribeHardware: (() => void) | null = null;

const sortedDisks = computed(() => {
  const disks = [...(overview.value?.hardware.disks ?? [])];
  return disks.sort(compareDisks);
});

const sortedDiskSpaces = computed(() => {
  const diskSpaces = [...(overview.value?.hardware.diskSpaces ?? [])];
  return diskSpaces.sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
});

onMounted(() => {
  void loadOverview();
  void startRealtimeConnection().catch(() => {
    errorMessage.value = 'SignalR 实时通道连接失败，将继续保留当前页面数据。';
  });

  unsubscribeHardware = subscribeHardwareRealtime((hardware) => {
    errorMessage.value = '';
    overview.value = {
      hardware: mergeHardwarePayload(hardware, overview.value?.hardware),
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

  if (total != null && total < 512) {
    return `${value.toFixed(1)} GB`;
  }

  if (value < 1024) return `${value.toFixed(1)} MB`;
  return `${(value / 1024).toFixed(1)} GB`;
}

function formatDiskSize(value?: number | null) {
  if (value == null || value <= 0) return '--';

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

function getCapacityPercent(usedBytes?: number | null, totalBytes?: number | null) {
  if (usedBytes == null || totalBytes == null || totalBytes <= 0) return null;
  return Math.max(0, Math.min(100, (usedBytes / totalBytes) * 100));
}

function getMemoryUsagePercent(usedMb?: number | null, totalMb?: number | null) {
  if (usedMb == null || totalMb == null || totalMb <= 0) return null;
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
  if (value >= 85) return 'danger';
  if (value >= 65) return 'warning';
  return 'info';
}

function usageBadge(value?: number | null) {
  if (value == null) return '未知';
  if (value >= 85) return '繁忙';
  if (value >= 65) return '较高';
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
  return {
    ...mergeHardwareDisks(hardware, previousHardware?.disks ?? []),
    diskSpaces: mergeDiskSpaces(hardware.diskSpaces, previousHardware?.diskSpaces ?? [])
  };
}

function mergeDiskSpaces(currentDiskSpaces: DiskSpaceDto[], previousDiskSpaces: DiskSpaceDto[]) {
  return currentDiskSpaces.length > 0 ? currentDiskSpaces : previousDiskSpaces;
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
