<template>
  <section class="page dashboard-page">
    <PageHeader
      iconName="dashboard"
      kicker="首页"
      title="概览"
      description="查看 CPU、内存、磁盘等关键硬件状态。"
    >
      <template #actions>
        <button class="ghost-button" :disabled="isLoading" @click="loadOverview">
          <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
          {{ isLoading ? '刷新中...' : '立即刷新' }}
        </button>
      </template>
    </PageHeader>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <section class="dashboard-section dashboard-core-metrics-section">
      <article class="card dashboard-core-metrics-panel">
        <div class="section-header section-header-rich dashboard-core-metrics-header">
          <div>
            <h3>核心运行指标</h3>
            <p class="section-subtitle">优先展示最关键、最常看的首页指标。</p>
          </div>
          <span class="section-tag">Core Metrics</span>
        </div>

        <div class="dashboard-core-metrics-panel-body">
          <div class="dashboard-hero-grid page-tier-stats">
            <MetricCard
              label="已开机"
              :value="formatUptime(hardware?.uptimeSeconds)"
              :hint="`启动时间 ${formatDateTime(bootTimeText)}`"
              badge="运行"
              tone="info"
              icon-name="uptime"
              small-value
            />
            <MetricCard
              label="CPU 当前频率"
              :value="formatNullable(hardware?.cpuFrequencyMhz, 'MHz')"
              :hint="formatCpuNameHint(hardware?.cpuName)"
              badge="频率"
              tone="info"
              icon-name="cpu"
            />
            <MetricCard
              label="CPU 占用"
              :value="formatPercent(hardware?.cpuUsagePercent)"
              :hint="formatCpuNameHint(hardware?.cpuName)"
              :badge="usageBadge(hardware?.cpuUsagePercent)"
              :tone="usageTone(hardware?.cpuUsagePercent)"
              icon-name="dashboard"
              :meter-percent="hardware?.cpuUsagePercent"
            />
            <MetricCard
              label="CPU 当前温度"
              :value="formatNullable(hardware?.cpuTemperatureC, '°C')"
              :hint="formatTemperatureHint(hardware?.cpuTemperatureC)"
              :badge="temperatureBadge(hardware?.cpuTemperatureC)"
              :tone="temperatureTone(hardware?.cpuTemperatureC)"
              icon-name="temperature"
              :meter-percent="temperaturePercent(hardware?.cpuTemperatureC)"
            />
            <MetricCard
              label="CPU 当前功耗"
              :value="formatNullable(hardware?.cpuPowerWatts, 'W')"
              :hint="formatPowerHint(hardware?.cpuPowerWatts)"
              badge="功耗"
              tone="info"
              icon-name="power"
              :meter-percent="powerPercent(hardware?.cpuPowerWatts)"
            />
            <MetricCard
              label="内存已使用"
              :value="formatMemoryUsage(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
              :hint="formatMemoryHint(hardware?.memoryTotalMb)"
              :badge="memoryUsageBadge(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
              :tone="memoryUsageTone(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
              icon-name="memory"
              :meter-percent="getMemoryUsagePercent(hardware?.memoryUsedMb, hardware?.memoryTotalMb)"
            />
            <MetricCard
              label="最热磁盘温度"
              :value="formatNullable(hardware?.diskTemperatureC, '°C')"
              :hint="`已识别 ${hardware?.disks?.length ?? 0} 块磁盘`"
              :badge="temperatureBadge(hardware?.diskTemperatureC)"
              :tone="temperatureTone(hardware?.diskTemperatureC)"
              icon-name="disk"
              :meter-percent="temperaturePercent(hardware?.diskTemperatureC)"
            />
          </div>
        </div>
      </article>
    </section>

    <section class="panel-grid dashboard-storage-grid">
      <article class="card dashboard-panel-card page-tier-panel">
        <div class="panel-header">
          <div class="panel-title">
            <span class="panel-icon"><AppIcon name="temperature" :size="16" /></span>
            <div>
              <h3>磁盘温度</h3>
              <p class="panel-subtitle">快速定位当前最热磁盘，便于散热与健康排查。</p>
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
            <span class="panel-icon"><AppIcon name="disk" :size="16" /></span>
            <div>
              <h3>磁盘空间</h3>
              <p class="panel-subtitle">按卷展示容量占用，优先发现空间紧张的盘符。</p>
            </div>
          </div>
          <span class="section-tag">按卷汇总</span>
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

const hardware = ref<RealtimeOverviewDto['hardware'] | null>(null);
const isLoading = ref(false);
const errorMessage = ref('');
const diskSortField = ref<'temperatureC'>('temperatureC');
const diskSortDescending = ref(true);
let unsubscribeHardware: (() => void) | null = null;

const sortedDisks = computed(() => {
  const disks = [...(hardware.value?.disks ?? [])];
  return disks.sort(compareDisks);
});

const sortedDiskSpaces = computed(() => {
  const diskSpaces = [...(hardware.value?.diskSpaces ?? [])];
  return diskSpaces.sort((left, right) => left.name.localeCompare(right.name, 'zh-CN'));
});

const bootTimeText = computed(() => {
  const uptimeSeconds = hardware.value?.uptimeSeconds;
  if (uptimeSeconds == null) {
    return null;
  }

  return new Date(Date.now() - uptimeSeconds * 1000).toISOString();
});

onMounted(() => {
  // 进入首页时先调用后端 /api/overview，保证首屏有完整概览数据
  void loadOverview();
  // 再连接后端 /hubs/monitor，后续通过 SignalR 增量刷新硬件实时数据
  void startRealtimeConnection().catch(() => {
    errorMessage.value = 'SignalR 实时通道连接失败，将继续保留当前页面数据。';
  });

  // 订阅后端 hardwareRealtime 推送：首页卡片收到新数据后直接更新本地状态
  unsubscribeHardware = subscribeHardwareRealtime((hardware) => {
    errorMessage.value = '';
    updateHardwareState(mergeHardwarePayload(hardware, hardwareState()));
  });
});

onUnmounted(() => {
  unsubscribeHardware?.();
});

async function loadOverview() {
  isLoading.value = true;
  errorMessage.value = '';

  try {
    // 调用后端 /api/overview：获取首页概览所需的硬件与网络快照
    const overview = await getOverview();
    updateHardwareState(mergeHardwarePayload(overview.hardware, hardwareState()));
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

function formatMemoryHint(total?: number | null) {
  if (total == null) {
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
    return `${days}天 ${hours}小时 ${minutes}分`;
  }

  return `${hours}小时 ${minutes}分`;
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

function temperaturePercent(value?: number | null) {
  if (value == null) return null;
  return Math.max(0, Math.min(100, value));
}

function powerPercent(value?: number | null) {
  if (value == null) return null;
  return Math.max(0, Math.min(100, (value / 65) * 100));
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
  Object.assign(target, next, {
    disks: target.disks,
    diskSpaces: target.diskSpaces
  });

  syncNamedItems(target.disks, next.disks);
  syncNamedItems(target.diskSpaces, next.diskSpaces);
}

function syncNamedItems<T extends { name: string }>(target: T[], next: T[]) {
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
