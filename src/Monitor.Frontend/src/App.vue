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
        <small>先搭框架，再细化功能</small>
      </div>
    </aside>
    <main class="content">
      <RouterView />
    </main>
  </div>
</template>

<script setup lang="ts">
</script>
