<template>
  <section class="page settings-page">
    <PageHeader
      iconName="settings"
      kicker="设置"
      title="系统设置"
      description="这里调整访问端口、采样间隔、默认统计粒度和历史保留时间。"
    >
      <template #actions>
        <div class="actions-row settings-actions">
          <button class="chip-button settings-action-button" :disabled="isLoading || isSaving" @click="loadSettings">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
            重新加载
          </button>
          <button class="ghost-button settings-action-button" :disabled="!isDirty || isSaving" @click="save">
            <span class="button-inline-icon"><AppIcon name="settings" :size="14" /></span>
            {{ isSaving ? '保存中...' : '保存设置' }}
          </button>
        </div>
      </template>
    </PageHeader>

    <div class="grid">
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="settings" :size="14" />当前访问端口</span>
        </div>
        <strong class="metric-value">{{ form.httpPort }}</strong>
        <span class="metric-hint">修改后下次启动生效</span>
      </div>
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="cpu" :size="14" />硬件采样间隔</span>
        </div>
        <strong class="metric-value">{{ form.hardwareSampleIntervalMs }} ms</strong>
        <span class="metric-hint">保存后会持久化，并在服务重启后生效</span>
      </div>
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="network" :size="14" />网络统计粒度</span>
        </div>
        <strong class="metric-value">{{ form.aggregateIntervalSeconds }} s</strong>
        <span class="metric-hint">保存后会持久化，并在服务重启后生效</span>
      </div>
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="disk" :size="14" />历史保留天数</span>
        </div>
        <strong class="metric-value">{{ form.historyRetentionDays }} 天</strong>
        <span class="metric-hint">到期后会自动清理旧数据</span>
      </div>
    </div>

    <div v-if="errorMessage" class="card state-card error-state">
      {{ errorMessage }}
    </div>

    <div v-if="successMessage" class="card state-card success-state">
      {{ successMessage }}
    </div>

    <section class="panel-grid">
      <form class="card settings-layout settings-layout-elevated" @submit.prevent="save">
        <div class="settings-group">
          <div class="section-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="settings" :size="16" /></span>
              <div>
                <h3>访问端口</h3>
                <p class="panel-subtitle">仅调整展示样式，不改配置行为。</p>
              </div>
            </div>
            <span class="section-tag">HTTP 端口配置</span>
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
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="network" :size="16" /></span>
              <div>
                <h3>采样与统计</h3>
                <p class="panel-subtitle">保持原有字段与保存逻辑，只做视觉升级。</p>
              </div>
            </div>
            <span class="section-tag">实时采样频率</span>
          </div>

          <label class="field">
            <span>硬件采样间隔 (ms)</span>
            <input v-model.number="form.hardwareSampleIntervalMs" type="number" min="500" max="60000" />
            <small class="field-help">建议 1000ms 左右，兼顾刷新速度和资源占用。</small>
            <small v-if="validation.hardwareSampleIntervalMs" class="field-error">{{ validation.hardwareSampleIntervalMs }}</small>
          </label>

          <label class="field">
            <span>网络采样间隔 (ms)</span>
            <input v-model.number="form.networkSampleIntervalMs" type="number" min="500" max="60000" />
            <small class="field-help">影响网络实时速率和相关列表的刷新节奏。</small>
            <small v-if="validation.networkSampleIntervalMs" class="field-error">{{ validation.networkSampleIntervalMs }}</small>
          </label>

          <label class="field">
            <span>默认统计粒度 (秒)</span>
            <input v-model.number="form.aggregateIntervalSeconds" type="number" min="1" max="3600" />
            <small class="field-help">对应后端 `AggregateIntervalSeconds`，用于网络历史数据聚合。</small>
            <small v-if="validation.aggregateIntervalSeconds" class="field-error">{{ validation.aggregateIntervalSeconds }}</small>
          </label>
        </div>

        <div class="settings-group">
          <div class="section-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="disk" :size="16" /></span>
              <div>
                <h3>历史数据</h3>
                <p class="panel-subtitle">清晰区分存储周期与默认展示配置。</p>
              </div>
            </div>
            <span class="section-tag">存储与默认展示</span>
          </div>

          <label class="field">
            <span>历史保留天数</span>
            <input v-model.number="form.historyRetentionDays" type="number" min="1" max="3650" />
            <small class="field-help">超过这个天数的历史数据会被清理。</small>
            <small v-if="validation.historyRetentionDays" class="field-error">{{ validation.historyRetentionDays }}</small>
          </label>

          <label class="field">
            <span>默认排行数量</span>
            <input v-model.number="form.topNDefault" type="number" min="1" max="100" />
            <small class="field-help">影响网络页面默认显示的排行数量。</small>
            <small v-if="validation.topNDefault" class="field-error">{{ validation.topNDefault }}</small>
          </label>
        </div>
      </form>

      <aside class="card settings-side settings-side-elevated">
        <div class="section-header">
          <div class="panel-title">
            <span class="panel-icon"><AppIcon name="status" :size="16" /></span>
            <div>
              <h3>保存说明</h3>
              <p class="panel-subtitle">保存逻辑保持不变，仅强化信息层级。</p>
            </div>
          </div>
          <span class="section-tag">{{ isDirty ? '有未保存修改' : '已同步' }}</span>
        </div>

        <ul class="simple-list compact">
          <li>所有设置都会先写入 SQLite 持久化保存。</li>
          <li>运行中的服务不会热切换，新配置会在服务重启后统一生效。</li>
          <li>如果输入超出合法范围，前端会先拦截，再阻止提交。</li>
        </ul>

        <div class="settings-side-actions">
          <button class="chip-button" :disabled="!isDirty || isSaving" @click="resetForm">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
            撤销修改
          </button>
          <button class="ghost-button" :disabled="isSaving || !canSave || !isDirty" @click="save">
            <span class="button-inline-icon"><AppIcon name="settings" :size="14" /></span>
            提交保存
          </button>
        </div>
      </aside>
    </section>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import AppIcon from '../components/AppIcon.vue';
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
    errors.networkSampleIntervalMs = '网络采样间隔必须在 500 ~ 60000 ms 之间。';
  }

  if (form.aggregateIntervalSeconds < 1 || form.aggregateIntervalSeconds > 3600) {
    errors.aggregateIntervalSeconds = '默认统计粒度必须在 1 ~ 3600 秒之间。';
  }

  if (form.historyRetentionDays < 1 || form.historyRetentionDays > 3650) {
    errors.historyRetentionDays = '历史保留天数必须在 1 ~ 3650 天之间。';
  }

  if (form.topNDefault < 1 || form.topNDefault > 100) {
    errors.topNDefault = '默认排行数量必须在 1 ~ 100 之间。';
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
    successMessage.value = '设置已保存。新的配置会在服务重启后生效。';
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存设置失败。';
  } finally {
    isSaving.value = false;
  }
}
</script>
