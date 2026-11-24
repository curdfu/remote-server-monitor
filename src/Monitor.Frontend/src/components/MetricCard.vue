<template>
  <article class="card metric-card metric-card-hero" :class="toneClass">
    <div class="metric-top">
      <span class="metric-label">{{ label }}</span>
      <span v-if="badge" class="metric-badge">{{ badge }}</span>
    </div>
    <strong class="metric-value" :class="{ 'metric-value-small': smallValue }">{{ value }}</strong>
    <div v-if="fill !== null" class="metric-track" aria-hidden="true">
      <span :style="{ width: `${fill}%` }"></span>
    </div>
    <span v-if="hint" class="metric-hint" :title="hint">{{ hint }}</span>
  </article>
</template>

<script setup lang="ts">
import { computed } from 'vue';

type MetricTone = 'default' | 'info' | 'success' | 'warning' | 'danger';

const props = defineProps<{
  label: string;
  value: string;
  hint?: string;
  badge?: string;
  tone?: MetricTone;
  smallValue?: boolean;
  percent?: number | null;
}>();

const toneClass = computed(() => `metric-tone-${props.tone ?? 'default'}`);
const fill = computed(() => {
  const value = props.percent;
  return value == null || !Number.isFinite(value) ? null : Math.max(0, Math.min(100, value));
});
</script>
