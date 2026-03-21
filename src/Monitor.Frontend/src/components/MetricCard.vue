<template>
  <article class="card metric-card" :class="toneClass">
    <div class="metric-top">
      <span class="metric-label">{{ label }}</span>
      <span v-if="badge" class="metric-badge">{{ badge }}</span>
    </div>
    <strong class="metric-value">{{ value }}</strong>
    <span v-if="hint" class="metric-hint">{{ hint }}</span>
  </article>
</template>

<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{
  label: string;
  value: string;
  hint?: string;
  badge?: string;
  tone?: 'default' | 'info' | 'success' | 'warning' | 'danger';
}>();

const toneClass = computed(() => `metric-tone-${props.tone ?? 'default'}`);
</script>
