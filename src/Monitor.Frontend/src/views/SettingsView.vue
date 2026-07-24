<template>
  <section class="page settings-page">
    <PageHeader
      iconName="settings"
      kicker="设置"
      title="设置"
      description="调整端口、硬件与网络实时刷新间隔、统计粒度和历史保留时间。"
    >
      <template #actions>
        <div class="actions-row settings-actions page-tier-toolbar-inline">
          <button class="chip-button settings-action-button" :disabled="isLoading || isSaving" @click="loadSettings">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
            刷新
          </button>
          <button class="ghost-button settings-action-button" :disabled="!isDirty || isSaving" @click="save">
            <span class="button-inline-icon"><AppIcon name="settings" :size="14" /></span>
            {{ isSaving ? '保存中...' : '保存' }}
          </button>
        </div>
      </template>
    </PageHeader>

    <section class="card settings-profile-panel">
      <div class="section-header section-header-rich">
        <div>
          <span class="section-overline">RUNTIME PROFILE</span>
          <h3>选择运行策略</h3>
          <p class="section-subtitle">预设仅调整硬件与网络实时刷新频率；统计粒度、历史保留时间和排行数量保持不变。</p>
        </div>
        <span class="section-tag">{{ activeProfileLabel }}</span>
      </div>

      <div class="settings-profile-grid">
        <button
          v-for="profile in performanceProfiles"
          :key="profile.id"
          type="button"
          class="settings-profile-button"
          :class="{ 'settings-profile-button-active': activeProfileId === profile.id }"
          :aria-pressed="activeProfileId === profile.id"
          @click="applyPerformanceProfile(profile)"
        >
          <span class="settings-profile-kicker">{{ profile.kicker }}</span>
          <strong>{{ profile.label }}</strong>
          <small>{{ profile.description }}</small>
        </button>
      </div>

      <div class="settings-impact-grid">
        <div class="settings-impact-item">
          <span>硬件样本</span>
          <strong>{{ hardwareSamplesPerMinute }}/分钟</strong>
        </div>
        <div class="settings-impact-item">
          <span>理论样本量</span>
          <strong>{{ hardwareSamplesPerDay }}/天</strong>
        </div>
        <div class="settings-impact-item">
          <span>网络实时刷新</span>
          <strong>{{ networkRefreshesPerMinute }}/分钟</strong>
        </div>
        <div class="settings-impact-item">
          <span>资源倾向</span>
          <strong>{{ resourceImpactLabel }}</strong>
        </div>
      </div>
    </section>

    <div class="grid page-tier-stats">
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="settings" :size="14" />当前访问端口</span>
        </div>
        <strong class="metric-value">{{ form.httpPort }}</strong>
        <span class="metric-hint">保存后会持久化，端口变更仍需重启</span>
      </div>
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="cpu" :size="14" />硬件采样间隔</span>
        </div>
        <strong class="metric-value">{{ form.hardwareSampleIntervalMs }} ms</strong>
        <span class="metric-hint">保存后会实时生效</span>
      </div>
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="network" :size="14" />网络实时刷新间隔</span>
        </div>
        <strong class="metric-value">{{ form.networkRealtimeIntervalMs }} ms</strong>
        <span class="metric-hint">保存后会实时生效</span>
      </div>
      <div class="card metric-card metric-card-compact settings-stat-card">
        <div class="metric-top">
          <span class="metric-label metric-label-inline"><AppIcon name="disk" :size="14" />历史保留天数</span>
        </div>
        <strong class="metric-value">{{ form.historyRetentionDays }} 天</strong>
        <span class="metric-hint">到期后会自动清理旧数据</span>
      </div>
    </div>

    <div v-if="errorMessage" class="card state-card error-state" role="alert">
      {{ errorMessage }}
    </div>

    <section class="panel-grid">
      <form class="card settings-layout settings-layout-elevated page-tier-panel" @submit.prevent="save">
        <div class="settings-group">
          <div class="section-header">
            <div class="panel-title">
              <span class="panel-icon"><AppIcon name="settings" :size="16" /></span>
              <div>
                <h3>访问端口</h3>
                <p class="panel-subtitle">配置 Web 服务的 HTTP 监听端口。</p>
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
                <p class="panel-subtitle">配置硬件数据的采集频率和网络历史数据的聚合粒度。</p>
              </div>
            </div>
            <span class="section-tag">采样与聚合</span>
          </div>

          <label class="field">
            <span>硬件采样间隔 (ms)</span>
            <input v-model.number="form.hardwareSampleIntervalMs" type="number" min="500" max="60000" />
            <small class="field-help">建议 1000ms 左右，兼顾刷新速度和资源占用。</small>
            <small v-if="validation.hardwareSampleIntervalMs" class="field-error">{{ validation.hardwareSampleIntervalMs }}</small>
          </label>

          <label class="field">
            <span>网络实时刷新间隔 (ms)</span>
            <input v-model.number="form.networkRealtimeIntervalMs" type="number" min="500" max="60000" />
            <small class="field-help">控制实时速率聚合、持久化检查和推送节奏；ETW 原始事件仍会持续采集。</small>
            <small v-if="validation.networkRealtimeIntervalMs" class="field-error">{{ validation.networkRealtimeIntervalMs }}</small>
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
                <p class="panel-subtitle">配置历史数据的保留时间和网络排行默认显示数量。</p>
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

      <aside class="card settings-side settings-side-elevated page-tier-panel">
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
          <li>除访问端口外，其余采样、聚合和展示配置会在运行中实时生效。</li>
          <li>如果输入超出合法范围，前端会先拦截，再阻止提交。</li>
        </ul>

        <div class="settings-side-actions">
          <button class="chip-button" :disabled="!isDirty || isSaving" @click="resetForm">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
            重置
          </button>
          <button class="ghost-button" :disabled="isSaving || !canSave || !isDirty" @click="save">
            <span class="button-inline-icon"><AppIcon name="settings" :size="14" /></span>
            {{ isSaving ? '保存中...' : '保存' }}
          </button>
        </div>
      </aside>
    </section>

    <Transition name="settings-toast">
      <aside v-if="successMessage" class="settings-save-toast" role="status" aria-live="polite">
        <span class="settings-save-toast-icon" aria-hidden="true">
          <AppIcon name="status" :size="18" />
        </span>
        <div class="settings-save-toast-copy">
          <strong>设置已保存</strong>
          <p>{{ successMessage }}</p>
        </div>
        <button class="settings-save-toast-close" type="button" aria-label="关闭保存提示" @click="clearSuccessMessage">
          <span aria-hidden="true">×</span>
        </button>
      </aside>
    </Transition>
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue';
import { onBeforeRouteLeave } from 'vue-router';
import AppIcon from '../components/AppIcon.vue';
import PageHeader from '../components/PageHeader.vue';
import { getSettings, saveSettings } from '../services/api';
import type { AppSettingsDto } from '../types/monitor';

const defaultForm: AppSettingsDto = {
  httpPort: 5188,
  hardwareSampleIntervalMs: 1000,
  networkRealtimeIntervalMs: 1000,
  aggregateIntervalSeconds: 10,
  historyRetentionDays: 30,
  topNDefault: 10
};

type PerformanceProfileId = 'economy' | 'balanced' | 'realtime';

interface PerformanceProfile {
  id: PerformanceProfileId;
  kicker: string;
  label: string;
  description: string;
  values: Pick<
    AppSettingsDto,
    'hardwareSampleIntervalMs' | 'networkRealtimeIntervalMs'
  >;
}

const performanceProfiles: readonly PerformanceProfile[] = [
  {
    id: 'economy',
    kicker: 'LOW OVERHEAD',
    label: '节能',
    description: '适合长期后台运行，优先降低采样与聚合开销。',
    values: {
      hardwareSampleIntervalMs: 5000,
      networkRealtimeIntervalMs: 5000
    }
  },
  {
    id: 'balanced',
    kicker: 'RECOMMENDED',
    label: '均衡',
    description: '兼顾监控响应、图表细节和日常服务器开销。',
    values: {
      hardwareSampleIntervalMs: 2000,
      networkRealtimeIntervalMs: 2000
    }
  },
  {
    id: 'realtime',
    kicker: 'HIGH FIDELITY',
    label: '实时',
    description: '更快响应与更细网络粒度，适合短期排障观察。',
    values: {
      hardwareSampleIntervalMs: 1000,
      networkRealtimeIntervalMs: 1000
    }
  }
] as const;

const form = reactive<AppSettingsDto>({ ...defaultForm });
const original = ref<AppSettingsDto>({ ...defaultForm });
const isLoading = ref(false);
const isSaving = ref(false);
const errorMessage = ref('');
const successMessage = ref('');
let successMessageTimer: number | null = null;

const validation = computed<Record<string, string>>(() => {
  const errors: Record<string, string> = {};

  if (!isIntegerInRange(form.httpPort, 1, 65535)) {
    errors.httpPort = 'HTTP 端口必须在 1 ~ 65535 之间。';
  }

  if (!isIntegerInRange(form.hardwareSampleIntervalMs, 500, 60000)) {
    errors.hardwareSampleIntervalMs = '硬件采样间隔必须在 500 ~ 60000 ms 之间。';
  }

  if (!isIntegerInRange(form.networkRealtimeIntervalMs, 500, 60000)) {
    errors.networkRealtimeIntervalMs = '网络实时刷新间隔必须在 500 ~ 60000 ms 之间。';
  }

  if (!isIntegerInRange(form.aggregateIntervalSeconds, 1, 3600)) {
    errors.aggregateIntervalSeconds = '默认统计粒度必须在 1 ~ 3600 秒之间。';
  }

  if (!isIntegerInRange(form.historyRetentionDays, 1, 3650)) {
    errors.historyRetentionDays = '历史保留天数必须在 1 ~ 3650 天之间。';
  }

  if (!isIntegerInRange(form.topNDefault, 1, 100)) {
    errors.topNDefault = '默认排行数量必须在 1 ~ 100 之间。';
  }

  return errors;
});

const canSave = computed(() => Object.keys(validation.value).length === 0);

const isDirty = computed(() =>
  JSON.stringify(form) !== JSON.stringify(original.value)
);

const activeProfileId = computed<PerformanceProfileId | null>(() =>
  performanceProfiles.find((profile) =>
    Object.entries(profile.values).every(
      ([key, value]) => form[key as keyof AppSettingsDto] === value
    )
  )?.id ?? null
);

const activeProfileLabel = computed(() =>
  performanceProfiles.find((profile) => profile.id === activeProfileId.value)?.label ?? '自定义'
);

const hardwareSamplesPerMinute = computed(() =>
  formatCompactNumber(safeDivide(60_000, form.hardwareSampleIntervalMs))
);

const hardwareSamplesPerDay = computed(() =>
  formatCompactNumber(safeDivide(86_400_000, form.hardwareSampleIntervalMs))
);

const networkRefreshesPerMinute = computed(() =>
  formatCompactNumber(safeDivide(60_000, form.networkRealtimeIntervalMs))
);

const resourceImpactLabel = computed(() => {
  if (form.hardwareSampleIntervalMs >= 5000 && form.networkRealtimeIntervalMs >= 5000) {
    return '低占用';
  }

  if (form.hardwareSampleIntervalMs <= 1000 || form.networkRealtimeIntervalMs <= 1000) {
    return '高实时';
  }

  return '均衡';
});

onMounted(() => {
  window.addEventListener('beforeunload', handleBeforeUnload);
  void loadSettings(true);
});

onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', handleBeforeUnload);
  clearSuccessMessageTimer();
});

onBeforeRouteLeave(() => confirmDiscardChanges());

async function loadSettings(skipDiscardConfirmation = false) {
  if (!skipDiscardConfirmation && !confirmDiscardChanges()) {
    return;
  }

  isLoading.value = true;
  errorMessage.value = '';
  successMessage.value = '';

  try {
    // 调用后端 /api/settings：加载设置页初始配置
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

function applyPerformanceProfile(profile: PerformanceProfile) {
  Object.assign(form, profile.values);
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
    // 调用后端 /api/settings：把当前表单保存到服务端，并用返回值回填页面
    const saved = await saveSettings({ ...form });
    Object.assign(form, saved);
    original.value = { ...saved };
    showSuccessMessage('除访问端口外，其余配置已实时生效；端口变更仍需重启服务。');
  } catch (error) {
    errorMessage.value = error instanceof Error ? error.message : '保存设置失败。';
  } finally {
    isSaving.value = false;
  }
}

function showSuccessMessage(message: string) {
  clearSuccessMessageTimer();
  successMessage.value = message;
  successMessageTimer = window.setTimeout(clearSuccessMessage, 6000);
}

function clearSuccessMessage() {
  successMessage.value = '';
  clearSuccessMessageTimer();
}

function clearSuccessMessageTimer() {
  if (successMessageTimer == null) return;
  window.clearTimeout(successMessageTimer);
  successMessageTimer = null;
}

function confirmDiscardChanges() {
  return !isDirty.value ||
    isSaving.value ||
    window.confirm('当前设置尚未保存，确定要放弃这些修改吗？');
}

function handleBeforeUnload(event: BeforeUnloadEvent) {
  if (!isDirty.value || isSaving.value) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

function isIntegerInRange(value: number, minimum: number, maximum: number) {
  return Number.isInteger(value) && value >= minimum && value <= maximum;
}

function safeDivide(dividend: number, divisor: number) {
  return Number.isFinite(divisor) && divisor > 0 ? dividend / divisor : 0;
}

function formatCompactNumber(value: number) {
  return Math.round(value).toLocaleString('zh-CN');
}
</script>
