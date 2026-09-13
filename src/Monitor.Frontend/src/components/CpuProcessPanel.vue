<template>
  <section class="card page-tier-panel dashboard-process-panel" aria-labelledby="process-cpu-title">
    <div class="panel-header">
      <div class="panel-title">
        <span class="panel-icon"><AppIcon name="apps" :size="16" /></span>
        <div>
          <h3 id="process-cpu-title">CPU 占用最高的进程</h3>
          <p class="panel-subtitle">按最近采样区间计算，占整机 CPU 的比例。</p>
        </div>
      </div>
      <span class="section-tag">Top 5 · {{ statusText }}</span>
    </div>

    <p v-if="stateMessage" class="process-cpu-state" :class="`process-cpu-state-${state}`">
      {{ stateMessage }}
    </p>

    <div v-if="processCpu?.processes?.length" class="table-shell process-cpu-table-shell">
      <table class="data-table process-cpu-table">
        <caption class="sr-only">CPU 占用最高的进程列表</caption>
        <thead>
          <tr>
            <th scope="col">排名</th>
            <th scope="col">进程</th>
            <th scope="col" class="align-right">CPU 占用</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="(process, index) in processCpu.processes" :key="`${process.processId}-${process.processName}`">
            <td class="process-cpu-rank">{{ index + 1 }}</td>
            <td>
              <div class="process-cpu-identity">
                <strong class="process-cpu-name">{{ process.processName || '未知进程' }}</strong>
                <span class="process-cpu-pid">PID {{ process.processId }}</span>
              </div>
            </td>
            <td class="align-right process-cpu-metric">
              <strong>{{ formatPercent(process.cpuUsagePercent) }}</strong>
              <div class="process-cpu-progress" aria-hidden="true">
                <span class="process-cpu-progress-bar" :style="{ width: `${clampPercent(process.cpuUsagePercent)}%` }"></span>
              </div>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <p class="process-cpu-sample">{{ sampleText }}</p>
  </section>
</template>

<script setup lang="ts">
import AppIcon from './AppIcon.vue';
import type { ProcessCpuRealtimeDto } from '../types/monitor';

withDefaults(defineProps<{
  processCpu: ProcessCpuRealtimeDto | null;
  state: 'waiting' | 'baseline' | 'empty' | 'fresh' | 'stale' | 'error';
  statusText: string;
  stateMessage: string;
  sampleText: string;
}>(), {
  processCpu: null,
  state: 'waiting',
  statusText: '等待采样',
  stateMessage: '',
  sampleText: '样本时间：--'
});

function clampPercent(value: number | null | undefined) {
  if (value == null || !Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, value));
}

function formatPercent(value: number | null | undefined) {
  return value == null || !Number.isFinite(value) ? '--' : `${value.toFixed(1)}%`;
}
</script>
