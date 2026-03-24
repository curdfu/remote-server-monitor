<template>
  <div class="layout">
    <aside class="sidebar">
      <div class="sidebar-brand">
        <div class="sidebar-brand-copy">
          <p class="sidebar-kicker">系统监控</p>
          <h1>服务器远程监控</h1>
          <p class="sidebar-description">查看硬件状态、网络流量和基础配置。</p>
        </div>

        <div class="sidebar-mobile-tools">
          <div class="nav-utility">
            <button
              ref="realtimeButtonRef"
              type="button"
              class="nav-utility-button"
              :class="`is-${connectionState}`"
              :aria-expanded="showRealtimePanel"
              aria-haspopup="dialog"
              :aria-label="`实时通道，当前${connectionStatusText}`"
              @click="toggleRealtimePanel"
            >
              <span class="nav-utility-indicator" :data-state="connectionState" />
              <AppIcon name="status" :size="16" />
            </button>

            <div
              v-if="showRealtimePanel"
              ref="realtimePanelRef"
              class="nav-utility-popover"
              role="dialog"
              aria-label="实时通道状态"
            >
              <div class="nav-utility-popover-header">
                <span class="sidebar-meta-label">
                  <AppIcon name="status" :size="12" />
                  <span>实时通道</span>
                </span>
                <strong>{{ connectionStatusText }}</strong>
              </div>
              <p class="nav-utility-popover-text">{{ connectionStatusDescription }}</p>
              <button
                type="button"
                class="nav-utility-action"
                :disabled="isRealtimeBusy"
                @click="retryRealtimeConnection"
              >
                {{ isRealtimeBusy ? '处理中…' : '重新连接' }}
              </button>
            </div>
          </div>

          <div class="nav-utility">
            <button
              ref="themeButtonRef"
              type="button"
              class="nav-utility-button"
              :aria-expanded="showThemeMenu"
              aria-haspopup="menu"
              :aria-label="`界面主题，当前${themePreferenceText}`"
              @click="toggleThemeMenu"
            >
              <AppIcon name="palette" :size="16" />
            </button>

            <div
              v-if="showThemeMenu"
              ref="themeMenuRef"
              class="nav-utility-popover nav-utility-menu"
              role="menu"
              aria-label="界面主题"
            >
              <button
                type="button"
                class="nav-utility-menu-item"
                :class="{ 'is-active': themePreference === 'system' }"
                role="menuitemradio"
                :aria-checked="themePreference === 'system'"
                @click="setThemePreference('system')"
              >
                跟随系统
              </button>
              <button
                type="button"
                class="nav-utility-menu-item"
                :class="{ 'is-active': themePreference === 'light' }"
                role="menuitemradio"
                :aria-checked="themePreference === 'light'"
                @click="setThemePreference('light')"
              >
                亮色
              </button>
              <button
                type="button"
                class="nav-utility-menu-item"
                :class="{ 'is-active': themePreference === 'dark' }"
                role="menuitemradio"
                :aria-checked="themePreference === 'dark'"
                @click="setThemePreference('dark')"
              >
                暗色
              </button>
            </div>
          </div>
        </div>
      </div>

      <nav class="sidebar-nav">
        <div class="sidebar-nav-links">
          <RouterLink to="/">
            <span class="nav-link-content">
              <span class="nav-icon"><AppIcon name="home" :size="16" /></span>
              <span>首页</span>
            </span>
          </RouterLink>
          <RouterLink to="/network">
            <span class="nav-link-content">
              <span class="nav-icon"><AppIcon name="network" :size="16" /></span>
              <span>网络</span>
            </span>
          </RouterLink>
          <RouterLink to="/settings">
            <span class="nav-link-content">
              <span class="nav-icon"><AppIcon name="settings" :size="16" /></span>
              <span>设置</span>
            </span>
          </RouterLink>
        </div>

        <div class="sidebar-nav-meta">
          <div class="sidebar-mini-card">
            <span class="sidebar-meta-label">
              <AppIcon name="status" :size="12" />
              <span>实时通道</span>
            </span>
            <small :style="{ marginLeft: '18px' }">{{ connectionStatusText }}</small>
          </div>

          <label class="sidebar-mini-card theme-field">
            <span class="sidebar-meta-label">
              <AppIcon name="palette" :size="12" />
              <span>界面主题</span>
            </span>
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
import AppIcon from './components/AppIcon.vue';
import {
  type RealtimeConnectionState,
  startRealtimeConnection,
  subscribeRealtimeConnectionState
} from './services/realtime';

type ThemePreference = 'system' | 'light' | 'dark';

const themeStorageKey = 'monitor.frontend.theme';
const connectionState = ref<RealtimeConnectionState>('disconnected');
const themePreference = ref<ThemePreference>('system');
const showRealtimePanel = ref(false);
const showThemeMenu = ref(false);
const realtimeButtonRef = ref<HTMLElement | null>(null);
const realtimePanelRef = ref<HTMLElement | null>(null);
const themeButtonRef = ref<HTMLElement | null>(null);
const themeMenuRef = ref<HTMLElement | null>(null);
let unsubscribeConnectionState: (() => void) | null = null;

onMounted(() => {
  const savedTheme = readSavedThemePreference();
  themePreference.value = savedTheme;
  applyThemePreference(savedTheme);

  document.addEventListener('pointerdown', handleDocumentPointerDown);
  document.addEventListener('keydown', handleDocumentKeydown);

  unsubscribeConnectionState = subscribeRealtimeConnectionState((state) => {
    connectionState.value = state;
  });

  void startRealtimeConnection().catch(() => {
    connectionState.value = 'disconnected';
  });
});

onUnmounted(() => {
  unsubscribeConnectionState?.();
  document.removeEventListener('pointerdown', handleDocumentPointerDown);
  document.removeEventListener('keydown', handleDocumentKeydown);
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

const connectionStatusDescription = computed(() => {
  switch (connectionState.value) {
    case 'connected':
      return '实时推送工作正常，首页会持续接收最新状态。';
    case 'connecting':
      return '正在建立连接，请稍候。';
    case 'reconnecting':
      return '连接中断后正在自动重试。';
    default:
      return '当前未连上后端实时通道，可手动重试。';
  }
});

const themePreferenceText = computed(() => {
  switch (themePreference.value) {
    case 'light':
      return '亮色';
    case 'dark':
      return '暗色';
    default:
      return '跟随系统';
  }
});

const isRealtimeBusy = computed(
  () => connectionState.value === 'connecting' || connectionState.value === 'reconnecting'
);

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

function toggleRealtimePanel() {
  showRealtimePanel.value = !showRealtimePanel.value;
  if (showRealtimePanel.value) {
    showThemeMenu.value = false;
  }
}

function toggleThemeMenu() {
  showThemeMenu.value = !showThemeMenu.value;
  if (showThemeMenu.value) {
    showRealtimePanel.value = false;
  }
}

function closeMobileOverlays() {
  showRealtimePanel.value = false;
  showThemeMenu.value = false;
}

function setThemePreference(value: ThemePreference) {
  themePreference.value = value;
  showThemeMenu.value = false;
}

function retryRealtimeConnection() {
  void startRealtimeConnection().catch(() => {
    connectionState.value = 'disconnected';
  });
}

function handleDocumentPointerDown(event: PointerEvent) {
  const target = event.target;
  if (!(target instanceof Node)) {
    return;
  }

  if (
    containsTarget(realtimeButtonRef.value, target) ||
    containsTarget(realtimePanelRef.value, target) ||
    containsTarget(themeButtonRef.value, target) ||
    containsTarget(themeMenuRef.value, target)
  ) {
    return;
  }

  closeMobileOverlays();
}

function handleDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    closeMobileOverlays();
  }
}

function containsTarget(element: HTMLElement | null, target: Node) {
  return Boolean(element?.contains(target));
}
</script>
