import { createApp } from 'vue';
import { createRouter, createWebHistory } from 'vue-router';
import App from './App.vue';
import { routes } from './router';
import './styles.css';
import './styles/observatory.css';
import './styles/network.css';
import './styles/settings.css';

const router = createRouter({
  history: createWebHistory(),
  routes,
  scrollBehavior(to, _from, savedPosition) {
    if (savedPosition) return savedPosition;
    if (to.hash) return { el: to.hash };
    return { top: 0, left: 0 };
  }
});

router.afterEach(() => {
  window.requestAnimationFrame(() => {
    document.querySelector<HTMLElement>('#main-content')?.focus({ preventScroll: true });
  });
});

createApp(App).use(router).mount('#app');
