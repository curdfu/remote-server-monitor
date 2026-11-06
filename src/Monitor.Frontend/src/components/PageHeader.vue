<template>
  <header class="page-header">
    <div class="page-header-main">
      <div class="page-title-stack">
        <h2>{{ title }}</h2>
        <p v-if="description" class="page-description">{{ description }}</p>
        <slot name="meta" />
      </div>
      <div v-if="$slots.actions" class="page-actions">
        <slot name="actions" />
      </div>
    </div>
  </header>
</template>

<script setup lang="ts">
defineProps<{
  title: string;
  description?: string;
  kicker?: string;
  icon?: string;
  iconName?: 'dashboard' | 'network' | 'settings';
}>();
</script>
