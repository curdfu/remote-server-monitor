<template>
  <div class="layout">
    <aside class="sidebar">
      <div>
        <p class="sidebar-kicker">Win11</p>
        <h1>System Monitor</h1>
      </div>
      <nav>
        <RouterLink to="/">总览</RouterLink>
        <RouterLink to="/network">网络</RouterLink>
        <RouterLink to="/settings">设置</RouterLink>
      </nav>
      <div class="sidebar-footer">
        <span>V1 MVP</span>
        <small>实时通道：{{ connectionStatusText }}</small>
      </div>
    </aside>
    <main class="content">
      <RouterView />
    </main>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import {
  type RealtimeConnectionState,
  startRealtimeConnection,
  subscribeRealtimeConnectionState
} from './services/realtime';

const connectionState = ref<RealtimeConnectionState>('disconnected');
let unsubscribeConnectionState: (() => void) | null = null;

onMounted(() => {
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

const connectionStatusText = computed(() => {
  switch (connectionState.value) {
    case 'connected':
      return 'SignalR 已连接';
    case 'connecting':
      return 'SignalR 连接中';
    case 'reconnecting':
      return 'SignalR 重连中';
    default:
      return 'SignalR 未连接';
  }
});
</script>
