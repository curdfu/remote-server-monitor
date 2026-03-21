<template>
  <header class="page-header">
    <div class="page-header-main">
      <div class="page-title-row">
        <div v-if="icon || kicker" class="page-title-side">
          <span v-if="icon" class="page-title-icon">{{ icon }}</span>
          <span v-if="kicker" class="page-kicker">{{ kicker }}</span>
        </div>
        <div class="page-title-stack">
          <div class="page-title-line">
            <h2>{{ title }}</h2>
          </div>
          <p class="page-description">{{ description }}</p>
        </div>
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
  description: string;
  kicker?: string;
  icon?: string;
}>();
</script>
