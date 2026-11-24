<template>
  <section class="card page-tier-panel dashboard-process-panel" aria-labelledby="process-cpu-title">
    <div class="panel-header">
      <div class="panel-title">
        <div>
          <h3 id="process-cpu-title">CPU 占用最高的进程</h3>
          <p class="panel-subtitle">最近采样区间 · 占整机 CPU 的比例</p>
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
          <tr v-for="(process, index) in processCpu.processes" :key="processKey(process)">
            <td class="process-cpu-rank">{{ index + 1 }}</td>
            <td>
              <div class="process-cpu-identity">
                <svg v-if="Number.isFinite(process.cpuUsagePercent)" class="process-cpu-pie" viewBox="0 0 16 16" aria-hidden="true">
                  <circle class="process-cpu-pie-track" cx="8" cy="8" r="8" />
                  <circle class="process-cpu-pie-fill" cx="8" cy="8" r="4" pathLength="100" :stroke-dasharray="`${clampPercent(process.cpuUsagePercent)} 100`" transform="rotate(-90 8 8)" />
                </svg>
                <button
                  type="button"
                  class="process-cpu-name"
                  :aria-expanded="expandedProcessKey === processKey(process)"
                  :aria-controls="`process-cpu-name-${process.processId}`"
                  @click="toggleProcessName(process)"
                >{{ process.processName || '未知进程' }}</button>
                <span class="process-cpu-pid">PID {{ process.processId }}</span>
                <span
                  v-show="expandedProcessKey === processKey(process)"
                  :id="`process-cpu-name-${process.processId}`"
                  class="process-cpu-full-name"
                >{{ process.processName || '未知进程' }}</span>
              </div>
            </td>
            <td class="align-right process-cpu-metric">
              <strong>{{ formatPercent(process.cpuUsagePercent) }}</strong>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <p class="process-cpu-sample">{{ sampleText }}</p>
  </section>
</template>

<script setup lang="ts">
import { ref } from 'vue';
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

type ProcessCpu = ProcessCpuRealtimeDto['processes'][number];
const expandedProcessKey = ref('');

function processKey(process: ProcessCpu) {
  return `${process.processId}-${process.processName}`;
}

function toggleProcessName(process: ProcessCpu) {
  const key = processKey(process);
  expandedProcessKey.value = expandedProcessKey.value === key ? '' : key;
}

function clampPercent(value: number | null | undefined) {
  if (value == null || !Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, value));
}

function formatPercent(value: number | null | undefined) {
  return value == null || !Number.isFinite(value) ? '--' : `${value.toFixed(1)}%`;
}
</script>
