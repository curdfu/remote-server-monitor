<template>
  <article class="card trend-card" :class="`trend-tone-${tone}`">
    <div class="trend-card-header">
      <div>
        <span class="trend-eyebrow">{{ eyebrow }}</span>
        <h3>{{ title }}</h3>
      </div>
      <div class="trend-current">
        <strong>{{ formattedCurrent }}</strong>
        <small>{{ rangeLabel }}</small>
      </div>
    </div>

    <div v-if="segments.length" class="trend-plot">
      <div class="trend-plot-body">
        <div class="trend-scale" aria-hidden="true">
          <span v-for="tick in domain.ticks" :key="tick">{{ formatTick(tick) }}</span>
        </div>
        <svg viewBox="0 0 360 120" preserveAspectRatio="none" role="img" :aria-label="summary">
          <path class="trend-grid-line" d="M0 30H360M0 60H360M0 90H360" />
          <polyline v-for="(points, index) in polylinePoints" :key="`line-${index}`" class="trend-line" :points="points" />
          <circle v-for="point in singlePoints" :key="`point-${point.timestamp}`" class="trend-point" :cx="point.x" :cy="point.y" r="3" />
        </svg>
      </div>
      <div class="trend-axis">
        <span>{{ formatTime(window.startMs) }}</span>
        <span>{{ formatTime(window.endMs) }}</span>
      </div>
    </div>

    <div v-else class="trend-empty">
      <span class="trend-empty-line"></span>
      <p>等待足够的历史样本。</p>
    </div>
  </article>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { downsampleTrendSegments, getTrendDomain, getTrendGapMs, getTrendWindow, projectTrendPoint, splitTrendSegments } from '../utils/trendGeometry';

export interface MetricTrendPoint {
  time: string;
  value: number | null;
}

const props = withDefaults(defineProps<{
  title: string;
  eyebrow: string;
  points: MetricTrendPoint[];
  unit: string;
  scaleKind?: 'percent' | 'temperature';
  rangeLabel?: string;
  windowEndMs?: number;
  windowDurationMs?: number;
  maxPlotPoints?: number;
  sampleIntervalMs?: number | null;
  tone?: 'signal' | 'info' | 'warning';
}>(), {
  rangeLabel: '区间末样本',
  tone: 'signal',
  scaleKind: 'percent',
  windowDurationMs: 60 * 60 * 1000,
  maxPlotPoints: 240
});

const window = computed(() => getTrendWindow(props.points, props.windowDurationMs, props.windowEndMs));
const domain = computed(() => getTrendDomain(props.scaleKind, props.points));
const gapMs = computed(() => getTrendGapMs(props.sampleIntervalMs));
const segments = computed(() => downsampleTrendSegments(
  splitTrendSegments(props.points, gapMs.value),
  props.maxPlotPoints
));
const values = computed(() => segments.value.flatMap((segment) => segment.points.map((point) => point.value)));
const minimum = computed(() => values.value.length ? Math.min(...values.value) : null);
const maximum = computed(() => values.value.length ? Math.max(...values.value) : null);
const projectedSegments = computed(() => segments.value.map((segment) => segment.points.map((point) => ({
  ...point,
  ...projectTrendPoint(point, window.value, domain.value)
}))));
const polylinePoints = computed(() => projectedSegments.value
  .filter((points) => points.length > 1)
  .map((points) => points.map((point) => `${point.x.toFixed(2)},${point.y.toFixed(2)}`).join(' ')));
const singlePoints = computed(() => projectedSegments.value.filter((points) => points.length === 1).flat());
const lastPoint = computed(() => props.points
  .filter((point) => Number.isFinite(Date.parse(point.time)))
  .sort((left, right) => Date.parse(right.time) - Date.parse(left.time))[0] ?? null);
const currentValue = computed(() => lastPoint.value?.value ?? null);
const formattedCurrent = computed(() =>
  currentValue.value == null ? '--' : `${currentValue.value.toFixed(1)}${props.unit}`
);

const summary = computed(() => {
  if (!values.value.length) return `${props.title}暂无历史数据`;
  return `${props.title}区间末样本${formattedCurrent.value}，区间最低${minimum.value?.toFixed(1)}${props.unit}，最高${maximum.value?.toFixed(1)}${props.unit}，有效样本${values.value.length}个`;
});

function formatTime(value: string | number) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--:--';
  return date.toLocaleString([], {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit'
  });
}

function formatTick(value: number) {
  return `${value.toFixed(0)}${props.unit}`;
}
</script>
