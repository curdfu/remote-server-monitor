<template>
  <article class="card metric-card metric-card-hero" :class="toneClass" :style="meterStyle">
    <div class="metric-card-glow"></div>

    <div class="metric-top">
      <span class="metric-label">{{ label }}</span>
      <span v-if="badge" class="metric-badge">{{ badge }}</span>
    </div>

    <div class="metric-main">
      <div class="metric-copy">
        <strong class="metric-value">{{ value }}</strong>
        <span v-if="hint" class="metric-hint">{{ hint }}</span>
      </div>

      <div v-if="hasMetricVisual" class="metric-visual">
        <div v-if="hasMeter" class="metric-ring">
          <div class="metric-ring-core">
            <AppIcon v-if="iconName" :name="iconName" :size="18" />
            <span v-else class="metric-ring-fallback">{{ meterText }}</span>
          </div>
        </div>
        <span v-else class="metric-icon-shell">
          <AppIcon v-if="iconName" :name="iconName" :size="18" />
        </span>
      </div>
    </div>
  </article>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import AppIcon from './AppIcon.vue';

type MetricTone = 'default' | 'info' | 'success' | 'warning' | 'danger';
type MetricIconName =
  | 'cpu'
  | 'memory'
  | 'temperature'
  | 'power'
  | 'uptime'
  | 'disk'
  | 'upload'
  | 'download'
  | 'traffic'
  | 'apps'
  | 'status'
  | 'refresh';

const props = defineProps<{
  label: string;
  value: string;
  hint?: string;
  badge?: string;
  tone?: MetricTone;
  iconName?: MetricIconName;
  meterPercent?: number | null;
}>();

const toneClass = computed(() => `metric-tone-${props.tone ?? 'default'}`);
const normalizedMeterPercent = computed(() => {
  if (props.meterPercent == null || Number.isNaN(props.meterPercent)) {
    return null;
  }

  return Math.max(0, Math.min(100, props.meterPercent));
});

const hasMeter = computed(() => normalizedMeterPercent.value != null);
const hasMetricVisual = computed(() => !!props.iconName || hasMeter.value);

const meterStyle = computed(() => {
  if (normalizedMeterPercent.value == null) {
    return undefined;
  }

  return {
    '--metric-meter-angle': `${normalizedMeterPercent.value * 3.6}deg`
  };
});

const meterText = computed(() => {
  if (normalizedMeterPercent.value == null) {
    return '';
  }

  return `${Math.round(normalizedMeterPercent.value)}%`;
});
</script>
