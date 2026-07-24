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

    <div v-if="chartPoints.length > 1" class="trend-plot">
      <svg
        viewBox="0 0 360 120"
        preserveAspectRatio="none"
        role="img"
        :aria-label="summary"
      >
        <defs>
          <linearGradient :id="gradientId" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="currentColor" stop-opacity=".28" />
            <stop offset="100%" stop-color="currentColor" stop-opacity="0" />
          </linearGradient>
        </defs>
        <path class="trend-grid-line" d="M0 30H360M0 60H360M0 90H360" />
        <path class="trend-area" :d="areaPath" :fill="`url(#${gradientId})`" />
        <polyline class="trend-line" :points="polylinePoints" />
      </svg>
      <div class="trend-axis">
        <span>{{ startLabel }}</span>
        <span>{{ endLabel }}</span>
      </div>
    </div>

    <div v-else class="trend-empty">
      <span class="trend-empty-line"></span>
      <p>等待足够的历史样本。</p>
    </div>
  </article>
</template>

<script setup lang="ts">
import { computed, useId } from 'vue';

export interface MetricTrendPoint {
  time: string;
  value: number | null;
}

const props = withDefaults(defineProps<{
  title: string;
  eyebrow: string;
  points: MetricTrendPoint[];
  unit: string;
  rangeLabel?: string;
  tone?: 'signal' | 'info' | 'warning';
}>(), {
  rangeLabel: '最近 1 小时',
  tone: 'signal'
});

const gradientId = `trend-${useId().replaceAll(':', '')}`;
const chartPoints = computed(() =>
  props.points.filter((point): point is MetricTrendPoint & { value: number } =>
    point.value != null && Number.isFinite(point.value)
  )
);

const values = computed(() => chartPoints.value.map((point) => point.value));
const minimum = computed(() => Math.min(...values.value));
const maximum = computed(() => Math.max(...values.value));
const valueRange = computed(() => Math.max(maximum.value - minimum.value, 1));

const normalizedPoints = computed(() =>
  chartPoints.value.map((point, index) => {
    const x = chartPoints.value.length <= 1
      ? 0
      : (index / (chartPoints.value.length - 1)) * 360;
    const y = 108 - ((point.value - minimum.value) / valueRange.value) * 92;
    return { x, y };
  })
);

const polylinePoints = computed(() =>
  normalizedPoints.value.map((point) => `${point.x.toFixed(2)},${point.y.toFixed(2)}`).join(' ')
);

const areaPath = computed(() => {
  if (!normalizedPoints.value.length) return '';
  const line = normalizedPoints.value
    .map((point) => `L${point.x.toFixed(2)} ${point.y.toFixed(2)}`)
    .join(' ');
  return `M0 112 ${line} L360 112 Z`;
});

const currentValue = computed(() => chartPoints.value.at(-1)?.value ?? null);
const formattedCurrent = computed(() =>
  currentValue.value == null ? '--' : `${currentValue.value.toFixed(1)}${props.unit}`
);

const startLabel = computed(() => formatTime(chartPoints.value.at(0)?.time));
const endLabel = computed(() => formatTime(chartPoints.value.at(-1)?.time));
const summary = computed(() => {
  if (!chartPoints.value.length) return `${props.title}暂无历史数据`;
  return `${props.title}当前${formattedCurrent.value}，区间最低${minimum.value.toFixed(1)}${props.unit}，最高${maximum.value.toFixed(1)}${props.unit}`;
});

function formatTime(value?: string) {
  if (!value) return '--:--';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '--:--';
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}
</script>
