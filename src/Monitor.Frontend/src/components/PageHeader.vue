<template>
  <header class="page-header">
    <div>
      <p class="page-kicker">{{ kicker }}</p>
      <h2>{{ title }}</h2>
      <p class="page-description">{{ description }}</p>
    </div>
    <div v-if="$slots.actions" class="page-actions">
      <slot name="actions" />
    </div>
  </header>
</template>

<script setup lang="ts">
defineProps<{
  title: string;
  description: string;
  kicker?: string;
}>();
</script>
