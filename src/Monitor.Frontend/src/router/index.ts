import type { RouteRecordRaw } from 'vue-router';
import DashboardView from '../views/DashboardView.vue';
import NetworkView from '../views/NetworkView.vue';
import SettingsView from '../views/SettingsView.vue';

export const routes: RouteRecordRaw[] = [
  {
    path: '/',
    name: 'dashboard',
    component: DashboardView
  },
  {
    path: '/network',
    name: 'network',
    component: NetworkView
  },
  {
    path: '/settings',
    name: 'settings',
    component: SettingsView
  }
];
