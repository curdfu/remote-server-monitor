<template>
  <div class="layout">
    <a class="skip-link" href="#main-content">跳转到主要内容</a>
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
          <div class="sidebar-mini-card sidebar-realtime-card" :class="`is-${connectionState}`">
            <div class="sidebar-realtime-main">
              <span class="sidebar-realtime-icon" aria-hidden="true">
                <AppIcon name="status" :size="17" />
                <span class="sidebar-realtime-indicator" />
              </span>
              <span class="sidebar-realtime-copy">
                <span class="sidebar-realtime-label">实时通道</span>
                <strong aria-live="polite">{{ connectionStatusText }}</strong>
              </span>
            </div>
            <span class="sidebar-realtime-description">
              {{ connectionStatusCompactDescription }}
            </span>
          </div>

          <div class="sidebar-mini-card theme-field">
            <span class="sidebar-meta-label">
              <AppIcon name="palette" :size="12" />
              <span>界面主题</span>
            </span>
            <div class="sidebar-theme-control">
              <button
                ref="desktopThemeButtonRef"
                type="button"
                class="sidebar-theme-trigger"
                :aria-expanded="showDesktopThemeMenu"
                aria-haspopup="menu"
                :aria-label="`界面主题，当前${themePreferenceText}`"
                @click="toggleDesktopThemeMenu"
              >
                <span>{{ themePreferenceText }}</span>
              </button>

              <div
                v-if="showDesktopThemeMenu"
                ref="desktopThemeMenuRef"
                class="sidebar-theme-menu"
                role="menu"
                aria-label="界面主题"
              >
                <button
                  v-for="option in themeOptions"
                  :key="option.value"
                  type="button"
                  class="sidebar-theme-option"
                  :class="{ 'is-active': themePreference === option.value }"
                  role="menuitemradio"
                  :aria-checked="themePreference === option.value"
                  @click="setThemePreference(option.value)"
                >
                  <span class="sidebar-theme-option-swatch" :data-theme-option="option.value" aria-hidden="true"></span>
                  <span class="sidebar-theme-option-copy">
                    <strong>{{ option.label }}</strong>
                    <small>{{ option.description }}</small>
                  </span>
                  <span v-if="themePreference === option.value" class="sidebar-theme-option-check" aria-hidden="true">✓</span>
                </button>
              </div>
            </div>
          </div>
        </div>
      </nav>
    </aside>
    <main id="main-content" class="content" tabindex="-1">
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

const themeOptions: ReadonlyArray<{
  value: ThemePreference;
  label: string;
  description: string;
}> = [
  { value: 'system', label: '跟随系统', description: '自动匹配设备设置' },
  { value: 'light', label: '亮色', description: '明亮清晰，适合白天' },
  { value: 'dark', label: '暗色', description: '降低亮度，适合夜间' }
];

const themeStorageKey = 'monitor.frontend.theme';
const connectionState = ref<RealtimeConnectionState>('disconnected');
const themePreference = ref<ThemePreference>('system');
const showRealtimePanel = ref(false);
const showThemeMenu = ref(false);
const showDesktopThemeMenu = ref(false);
const realtimeButtonRef = ref<HTMLElement | null>(null);
const realtimePanelRef = ref<HTMLElement | null>(null);
const themeButtonRef = ref<HTMLElement | null>(null);
const themeMenuRef = ref<HTMLElement | null>(null);
const desktopThemeButtonRef = ref<HTMLElement | null>(null);
const desktopThemeMenuRef = ref<HTMLElement | null>(null);
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

const connectionStatusCompactDescription = computed(() => {
  switch (connectionState.value) {
    case 'connected':
      return '实时数据正在持续推送';
    case 'connecting':
      return '正在建立实时连接';
    case 'reconnecting':
      return '连接中断，正在自动重试';
    default:
      return '实时推送暂不可用';
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
    showDesktopThemeMenu.value = false;
  }
}

function toggleThemeMenu() {
  showThemeMenu.value = !showThemeMenu.value;
  if (showThemeMenu.value) {
    showRealtimePanel.value = false;
    showDesktopThemeMenu.value = false;
  }
}

function toggleDesktopThemeMenu() {
  showDesktopThemeMenu.value = !showDesktopThemeMenu.value;
  if (showDesktopThemeMenu.value) {
    showRealtimePanel.value = false;
    showThemeMenu.value = false;
  }
}

function closeUtilityOverlays() {
  showRealtimePanel.value = false;
  showThemeMenu.value = false;
  showDesktopThemeMenu.value = false;
}

function setThemePreference(value: ThemePreference) {
  themePreference.value = value;
  showThemeMenu.value = false;
  showDesktopThemeMenu.value = false;
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
    containsTarget(themeMenuRef.value, target) ||
    containsTarget(desktopThemeButtonRef.value, target) ||
    containsTarget(desktopThemeMenuRef.value, target)
  ) {
    return;
  }

  closeUtilityOverlays();
}

function handleDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    closeUtilityOverlays();
  }
}

function containsTarget(element: HTMLElement | null, target: Node) {
  return Boolean(element?.contains(target));
}
</script>
