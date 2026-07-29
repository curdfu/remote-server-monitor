<template>
  <svg
    class="app-icon"
    :style="iconStyle"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
  >
    <template v-if="name === 'home'">
      <path d="M3 11.5 12 4l9 7.5" />
      <path d="M5.5 10.5V20h13V10.5" />
      <path d="M10 20v-5.5h4V20" />
    </template>

    <template v-else-if="name === 'dashboard'">
      <rect x="3.5" y="3.5" width="7" height="7" rx="2" />
      <rect x="13.5" y="3.5" width="7" height="5" rx="2" />
      <rect x="3.5" y="13.5" width="7" height="7" rx="2" />
      <rect x="13.5" y="11.5" width="7" height="9" rx="2" />
    </template>

    <template v-else-if="name === 'network'">
      <path d="M5 17c2.5-4.5 5.5-6.5 9.5-9" />
      <path d="M9 19c1.5-2.7 3.7-4.8 7-7" />
      <path d="M4 8.5h4" />
      <path d="M16 15.5h4" />
      <circle cx="4" cy="8.5" r="1.5" />
      <circle cx="20" cy="15.5" r="1.5" />
      <circle cx="9.5" cy="19" r="1.5" />
      <circle cx="15" cy="8" r="1.5" />
    </template>

    <template v-else-if="name === 'settings'">
      <circle cx="12" cy="12" r="3.2" />
      <path d="M19.4 15a1 1 0 0 0 .2 1.1l.1.1a2 2 0 0 1-2.8 2.8l-.1-.1a1 1 0 0 0-1.1-.2 1 1 0 0 0-.6.9V20a2 2 0 0 1-4 0v-.2a1 1 0 0 0-.6-.9 1 1 0 0 0-1.1.2l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1 1 0 0 0 .2-1.1 1 1 0 0 0-.9-.6H4a2 2 0 0 1 0-4h.2a1 1 0 0 0 .9-.6 1 1 0 0 0-.2-1.1l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1 1 0 0 0 1.1.2 1 1 0 0 0 .6-.9V4a2 2 0 0 1 4 0v.2a1 1 0 0 0 .6.9 1 1 0 0 0 1.1-.2l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1 1 0 0 0-.2 1.1 1 1 0 0 0 .9.6H20a2 2 0 0 1 0 4h-.2a1 1 0 0 0-.9.6Z" />
    </template>

    <template v-else-if="name === 'cpu'">
      <rect x="7" y="7" width="10" height="10" rx="2.5" />
      <rect x="10" y="10" width="4" height="4" rx="1" />
      <path d="M9 3.5v2.2M15 3.5v2.2M9 18.3v2.2M15 18.3v2.2M20.5 9h-2.2M20.5 15h-2.2M5.7 9H3.5M5.7 15H3.5" />
    </template>

    <template v-else-if="name === 'memory'">
      <path d="M5 8.5h14v7H5z" />
      <path d="M8 12h.01M12 12h.01M16 12h.01" />
      <path d="M7 6V4.5M11 6V4.5M15 6V4.5M17 18v1.5M13 18v1.5M9 18v1.5" />
    </template>

    <template v-else-if="name === 'temperature'">
      <path d="M10 5a2 2 0 1 1 4 0v7.2a4.2 4.2 0 1 1-4 0Z" />
      <path d="M12 9v6" />
      <path d="M12 17.8a1.2 1.2 0 1 0 0-.01" />
    </template>

    <template v-else-if="name === 'power'">
      <path d="M12 3v7" />
      <path d="M7.7 5.8a7 7 0 1 0 8.6 0" />
    </template>

    <template v-else-if="name === 'uptime'">
      <circle cx="12" cy="12" r="8" />
      <path d="M12 7.5V12l3 2" />
      <path d="M12 4v1.3M20 12h-1.3M12 20v-1.3M4 12h1.3" />
    </template>

    <template v-else-if="name === 'disk'">
      <ellipse cx="12" cy="6.5" rx="6.5" ry="2.8" />
      <path d="M5.5 6.5v7c0 1.5 2.9 2.8 6.5 2.8s6.5-1.3 6.5-2.8v-7" />
      <path d="M5.5 10c0 1.5 2.9 2.8 6.5 2.8s6.5-1.3 6.5-2.8" />
      <path d="M16.5 18.5c0 1-1.8 2-4.5 2s-4.5-1-4.5-2" />
    </template>

    <template v-else-if="name === 'upload'">
      <path d="M12 20V6" />
      <path d="m7 11 5-5 5 5" />
      <path d="M5 20h14" />
    </template>

    <template v-else-if="name === 'download'">
      <path d="M12 4v14" />
      <path d="m7 13 5 5 5-5" />
      <path d="M5 20h14" />
    </template>

    <template v-else-if="name === 'traffic'">
      <path d="M4 16h4l2-5 4 8 2-4h4" />
      <path d="M4 8h3l2 3 3-6 3 9 2-4h3" opacity=".55" />
    </template>

    <template v-else-if="name === 'apps'">
      <rect x="4" y="4" width="7" height="7" rx="2" />
      <rect x="13" y="4" width="7" height="7" rx="2" />
      <rect x="4" y="13" width="7" height="7" rx="2" />
      <path d="M13 16.5h7" />
      <path d="M13 20h7" />
    </template>

    <template v-else-if="name === 'palette'">
      <path d="M12 4a8 8 0 0 0 0 16h1.2a2 2 0 0 0 0-4H12a2 2 0 1 1 0-4h1a4 4 0 0 0 0-8Z" />
      <circle cx="7.5" cy="10" r="1" />
      <circle cx="9.5" cy="7.5" r="1" />
      <circle cx="13" cy="7" r="1" />
    </template>

    <template v-else-if="name === 'status'">
      <circle cx="12" cy="12" r="7" />
      <path d="m9.2 12.1 1.7 1.7 4-4" />
    </template>

    <template v-else-if="name === 'refresh'">
      <path d="M20 11a8 8 0 0 0-13.7-5.4L4 8" />
      <path d="M4 4v4h4" />
      <path d="M4 13a8 8 0 0 0 13.7 5.4L20 16" />
      <path d="M16 16h4v4" />
    </template>

    <template v-else-if="name === 'eye-off'">
      <path d="M3 3l18 18" />
      <path d="M10.6 10.6a2 2 0 0 0 2.8 2.8" />
      <path d="M9.9 4.4A10.8 10.8 0 0 1 12 4.2c4.8 0 8.1 4.3 9 7.8a11.8 11.8 0 0 1-2.1 4.2" />
      <path d="M6.6 6.6A12 12 0 0 0 3 12c.9 3.5 4.2 7.8 9 7.8 1.4 0 2.7-.4 3.8-1" />
    </template>

    <template v-else-if="name === 'restore'">
      <path d="M4 10a8 8 0 1 1 2.3 7.7" />
      <path d="M4 5v5h5" />
      <path d="M12 8v4l2.8 1.8" />
    </template>

    <template v-else-if="name === 'close'">
      <path d="M6.5 6.5 17.5 17.5" />
      <path d="M17.5 6.5 6.5 17.5" />
    </template>
  </svg>
</template>

<script setup lang="ts">
import { computed } from 'vue';

const props = defineProps<{
  name:
    | 'home'
    | 'dashboard'
    | 'network'
    | 'settings'
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
    | 'palette'
    | 'status'
    | 'refresh'
    | 'eye-off'
    | 'restore'
    | 'close';
  size?: number | string;
}>();

const iconStyle = computed(() => {
  const size = typeof props.size === 'number' ? `${props.size}px` : props.size;
  return size ? { width: size, height: size } : undefined;
});
</script>
