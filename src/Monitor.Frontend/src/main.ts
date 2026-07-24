import { createApp } from 'vue';
import { createRouter, createWebHistory } from 'vue-router';
import App from './App.vue';
import { routes } from './router';
import './styles.css';
import './styles/observatory.css';

const router = createRouter({
  history: createWebHistory(),
  routes
});

router.afterEach(() => {
  window.requestAnimationFrame(() => {
    document.querySelector<HTMLElement>('#main-content')?.focus({ preventScroll: true });
  });
});

createApp(App).use(router).mount('#app');
