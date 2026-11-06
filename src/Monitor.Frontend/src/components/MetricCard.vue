<template>
  <article class="card metric-card metric-card-hero" :class="toneClass">
    <div class="metric-top">
      <span class="metric-label">{{ label }}</span>
      <span v-if="badge" class="metric-badge">{{ badge }}</span>
    </div>
    <strong class="metric-value" :class="{ 'metric-value-small': smallValue }">{{ value }}</strong>
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
}>();

const toneClass = computed(() => `metric-tone-${props.tone ?? 'default'}`);
</script>
