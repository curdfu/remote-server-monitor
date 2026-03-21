<template>
  <section class="page">
    <PageHeader
      kicker="Settings"
      title="设置页"
      description="当前把 HTTP 端口、采样间隔、默认时间粒度和历史保留天数整理成可保存、可重置、可校验的表单。"
    >
      <template #actions>
        <div class="actions-row">
          <button class="chip-button" :disabled="isLoading || isSaving" @click="loadSettings">
            重新加载
          </button>
          <button class="ghost-button" :disabled="!isDirty || isSaving" @click="save">
            {{ isSaving ? '保存中...' : '保存设置' }}
          </button>
        </div>
      </template>
    </PageHeader>

    <div class="grid">
      <div class="card metric-card">
        <span class="metric-label">当前 HTTP 端口</span>
        <strong class="metric-value">{{ form.httpPort }}</strong>
        <span class="metric-hint">修改后下次启动生效</span>
      </div>
      <div class="card metric-card">
        <span class="metric-label">硬件采样间隔</span>
        <strong class="metric-value">{{ form.hardwareSampleIntervalMs }} ms</strong>
        <span class="metric-hint">越低越实时，负载也更高</span>
      </div>
      <div class="card metric-card">
        <span class="metric-label">默认时间粒度</span>
        <strong class="metric-value">{{ form.aggregateIntervalSeconds }} s</strong>
        <span class="metric-hint">影响网络历史聚合桶</span>
      </div>
      <div class="card metric-card">
        <span class="metric-label">历史保留天数</span>
        <strong class="metric-value">{{ form.historyRetentionDays }} 天</strong>
        <span class="metric-hint">定期清理过期历史</span>
      </div>
    </div>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <div v-if="successMessage" class="card state-card success-state">
      {{ successMessage }}
    </div>

    <section class="panel-grid">
      <form class="card settings-layout" @submit.prevent="save">
        <div class="settings-group">
          <div class="section-header">
            <h3>服务访问</h3>
            <span class="muted">HTTP 端口配置</span>
          </div>

          <label class="field">
            <span>HTTP 端口</span>
            <input v-model.number="form.httpPort" type="number" min="1" max="65535" />
            <small class="field-help">本地页面访问端口，例如 `http://127.0.0.1:5188`。</small>
            <small v-if="validation.httpPort" class="field-error">{{ validation.httpPort }}</small>
          </label>
        </div>

        <div class="settings-group">
          <div class="section-header">
            <h3>采样与聚合</h3>
            <span class="muted">实时采样频率</span>
          </div>

          <label class="field">
            <span>硬件采样间隔 (ms)</span>
            <input v-model.number="form.hardwareSampleIntervalMs" type="number" min="500" max="60000" />
            <small class="field-help">建议 1000ms 左右，兼顾刷新速度与开销。</small>
            <small v-if="validation.hardwareSampleIntervalMs" class="field-error">{{ validation.hardwareSampleIntervalMs }}</small>
          </label>

          <label class="field">
            <span>网络刷新间隔 (ms)</span>
            <input v-model.number="form.networkSampleIntervalMs" type="number" min="500" max="60000" />
            <small class="field-help">影响网络实时速率和榜单刷新节奏。</small>
            <small v-if="validation.networkSampleIntervalMs" class="field-error">{{ validation.networkSampleIntervalMs }}</small>
          </label>

          <label class="field">
            <span>默认时间粒度 (秒)</span>
            <input v-model.number="form.aggregateIntervalSeconds" type="number" min="1" max="3600" />
            <small class="field-help">对应后端 `AggregateIntervalSeconds`，用于历史网络聚合。</small>
            <small v-if="validation.aggregateIntervalSeconds" class="field-error">{{ validation.aggregateIntervalSeconds }}</small>
          </label>
        </div>

        <div class="settings-group">
          <div class="section-header">
            <h3>历史保留</h3>
            <span class="muted">存储与展示默认项</span>
          </div>

          <label class="field">
            <span>历史保留天数</span>
            <input v-model.number="form.historyRetentionDays" type="number" min="1" max="3650" />
            <small class="field-help">超过该天数的历史数据会被清理。</small>
            <small v-if="validation.historyRetentionDays" class="field-error">{{ validation.historyRetentionDays }}</small>
          </label>

          <label class="field">
            <span>默认 Top N</span>
            <input v-model.number="form.topNDefault" type="number" min="1" max="100" />
            <small class="field-help">影响总览页与网络页默认排行数量。</small>
            <small v-if="validation.topNDefault" class="field-error">{{ validation.topNDefault }}</small>
          </label>
        </div>
      </form>

      <aside class="card settings-side">
        <div class="section-header">
          <h3>保存说明</h3>
          <span class="muted">{{ isDirty ? '有未保存修改' : '已同步' }}</span>
        </div>

        <ul class="simple-list compact">
          <li>端口修改会写入 SQLite，并在下次启动时生效。</li>
          <li>采样间隔和默认粒度当前也会持久化保存。</li>
          <li>如果填入非法范围，前端会先拦截，再提交到后端。</li>
        </ul>

        <div class="settings-side-actions">
          <button class="chip-button" :disabled="!isDirty || isSaving" @click="resetForm">
            撤销修改
          </button>
          <button class="ghost-button" :disabled="isSaving || !canSave" @click="save">
            提交保存
          </button>
        </div>
      </aside>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import PageHeader from '../components/PageHeader.vue';
import { getSettings, saveSettings } from '../services/api';
import type { AppSettingsDto } from '../types/monitor';

const defaultForm: AppSettingsDto = {
  httpPort: 5188,
  hardwareSampleIntervalMs: 1000,
  networkSampleIntervalMs: 1000,
  aggregateIntervalSeconds: 10,
  historyRetentionDays: 30,
  topNDefault: 10
};

const form = reactive<AppSettingsDto>({ ...defaultForm });
const original = ref<AppSettingsDto>({ ...defaultForm });
const isLoading = ref(false);
const isSaving = ref(false);
const errorMessage = ref('');
const successMessage = ref('');

const validation = computed<Record<string, string>>(() => {
  const errors: Record<string, string> = {};

  if (form.httpPort < 1 || form.httpPort > 65535) {
    errors.httpPort = 'HTTP 端口必须在 1 ~ 65535 之间。';
  }

  if (form.hardwareSampleIntervalMs < 500 || form.hardwareSampleIntervalMs > 60000) {
    errors.hardwareSampleIntervalMs = '硬件采样间隔必须在 500 ~ 60000 ms 之间。';
  }

  if (form.networkSampleIntervalMs < 500 || form.networkSampleIntervalMs > 60000) {
    errors.networkSampleIntervalMs = '网络刷新间隔必须在 500 ~ 60000 ms 之间。';
  }

  if (form.aggregateIntervalSeconds < 1 || form.aggregateIntervalSeconds > 3600) {
    errors.aggregateIntervalSeconds = '默认时间粒度必须在 1 ~ 3600 秒之间。';
  }

  if (form.historyRetentionDays < 1 || form.historyRetentionDays > 3650) {
    errors.historyRetentionDays = '历史保留天数必须在 1 ~ 3650 天之间。';
  }

  if (form.topNDefault < 1 || form.topNDefault > 100) {
    errors.topNDefault = '默认 Top N 必须在 1 ~ 100 之间。';
  }

  return errors;
});

const canSave = computed(() => Object.keys(validation.value).length === 0);

const isDirty = computed(() =>
  JSON.stringify(form) !== JSON.stringify(original.value)
);

onMounted(() => {
  void loadSettings();
});

async function loadSettings() {
  isLoading.value = true;
  errorMessage.value = '';
  successMessage.value = '';

  try {
    const loaded = await getSettings();
    Object.assign(form, loaded);
    original.value = { ...loaded };
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '加载设置失败。';
  } finally {
    isLoading.value = false;
  }
}

function resetForm() {
  Object.assign(form, original.value);
  errorMessage.value = '';
  successMessage.value = '';
}

async function save() {
  if (!canSave.value) {
    errorMessage.value = '请先修正表单中的非法配置。';
    successMessage.value = '';
    return;
  }

  isSaving.value = true;
  errorMessage.value = '';
  successMessage.value = '';

  try {
    const saved = await saveSettings({ ...form });
    Object.assign(form, saved);
    original.value = { ...saved };
    successMessage.value = '设置已保存。端口修改将在下次启动时生效。';
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存设置失败。';
  } finally {
    isSaving.value = false;
  }
}
</script>
