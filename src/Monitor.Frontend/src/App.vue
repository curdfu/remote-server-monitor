<template>
  <div class="layout">
    <aside class="sidebar">
      <div class="sidebar-brand">
        <p class="sidebar-kicker">Windows 主机</p>
        <h1>Windows服务器远程监控</h1>
        <p class="sidebar-description">查看硬件状态、网络流量和基础配置。</p>
      </div>
      <nav class="sidebar-nav">
        <div class="sidebar-nav-links">
          <RouterLink to="/">
            <span class="nav-link-content">
              <span class="nav-icon">▦</span>
              <span>首页</span>
            </span>
          </RouterLink>
          <RouterLink to="/network">
            <span class="nav-link-content">
              <span class="nav-icon">⇆</span>
              <span>网络</span>
            </span>
          </RouterLink>
          <RouterLink to="/settings">
            <span class="nav-link-content">
              <span class="nav-icon">⛭</span>
              <span>设置</span>
            </span>
          </RouterLink>
        </div>

        <div class="sidebar-nav-meta">
          <div class="sidebar-mini-card">
            <span class="sidebar-meta-label">实时通道</span>
            <small>{{ connectionStatusText }}</small>
          </div>

          <label class="sidebar-mini-card theme-field">
            <span class="sidebar-meta-label">界面主题</span>
            <select v-model="themePreference" class="theme-select">
              <option value="system">跟随系统</option>
              <option value="light">亮色</option>
              <option value="dark">暗色</option>
            </select>
          </label>
        </div>
      </nav>
    </aside>
    <main class="content">
      <RouterView />
    </main>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import {
  type RealtimeConnectionState,
  startRealtimeConnection,
  subscribeRealtimeConnectionState
} from './services/realtime';

type ThemePreference = 'system' | 'light' | 'dark';

const themeStorageKey = 'monitor.frontend.theme';
const connectionState = ref<RealtimeConnectionState>('disconnected');
const themePreference = ref<ThemePreference>('system');
let unsubscribeConnectionState: (() => void) | null = null;

onMounted(() => {
  const savedTheme = readSavedThemePreference();
  themePreference.value = savedTheme;
  applyThemePreference(savedTheme);

  unsubscribeConnectionState = subscribeRealtimeConnectionState((state) => {
    connectionState.value = state;
  });

  void startRealtimeConnection().catch(() => {
    connectionState.value = 'disconnected';
  });
});

onUnmounted(() => {
  unsubscribeConnectionState?.();
});

watch(themePreference, (value) => {
  persistThemePreference(value);
  applyThemePreference(value);
});

const connectionStatusText = computed(() => {
  switch (connectionState.value) {
    case 'connected':
      return '已连接';
    case 'connecting':
      return '连接中';
    case 'reconnecting':
      return '重连中';
    default:
      return '未连接';
  }
});

function readSavedThemePreference(): ThemePreference {
  if (typeof window === 'undefined') return 'system';

  const saved = window.localStorage.getItem(themeStorageKey);
  if (saved === 'light' || saved === 'dark' || saved === 'system') {
    return saved;
  }

  return 'system';
}

function persistThemePreference(value: ThemePreference) {
  if (typeof window === 'undefined') return;
  window.localStorage.setItem(themeStorageKey, value);
}

function applyThemePreference(value: ThemePreference) {
  if (typeof document === 'undefined') return;

  const root = document.documentElement;
  if (value === 'system') {
    delete root.dataset.theme;
    return;
  }

  root.dataset.theme = value;
}
</script>
