<template>
  <section class="page settings-page">
    <PageHeader
      iconName="settings"
      kicker="设置"
      title="设置"
      description="调整端口、硬件采样与网络处理间隔、统计粒度和历史保留时间。"
    >
      <template #actions>
        <div class="actions-row settings-actions page-tier-toolbar-inline">
          <button class="chip-button settings-action-button" :disabled="isBusy" @click="refreshSettings()">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
            刷新
          </button>
          <button class="ghost-button settings-action-button" :disabled="!canSave || !isDirty" @click="save">
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
          <p class="section-subtitle">预设仅调整硬件采样与网络处理频率；统计粒度、历史保留时间和排行数量保持不变。</p>
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
          :disabled="isBusy"
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
          <span>网络处理检查</span>
          <strong>{{ networkProcessingChecksPerMinute }}/分钟</strong>
        </div>
        <div class="settings-impact-item">
          <span>资源倾向</span>
          <strong>{{ resourceImpactLabel }}</strong>
        </div>
      </div>
    </section>

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

          <label class="field" for="settings-http-port">
            <span>HTTP 端口</span>
            <input id="settings-http-port" v-model.number="form.httpPort" type="number" min="1" max="65535" :disabled="isBusy" :aria-invalid="Boolean(validation.httpPort)" :aria-describedby="validation.httpPort ? 'settings-http-port-help settings-http-port-error' : 'settings-http-port-help'" />
            <small id="settings-http-port-help" class="field-help">本地页面访问端口，例如 http://127.0.0.1:5188。保存后需要重启服务。</small>
            <small v-if="validation.httpPort" id="settings-http-port-error" class="field-error">{{ validation.httpPort }}</small>
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

          <label class="field" for="settings-hardware-interval">
            <span>硬件采样间隔 (ms)</span>
            <input id="settings-hardware-interval" v-model.number="form.hardwareSampleIntervalMs" type="number" min="500" max="60000" :disabled="isBusy" :aria-invalid="Boolean(validation.hardwareSampleIntervalMs)" :aria-describedby="validation.hardwareSampleIntervalMs ? 'settings-hardware-interval-help settings-hardware-interval-error' : 'settings-hardware-interval-help'" />
            <small id="settings-hardware-interval-help" class="field-help">建议 1000ms 左右，兼顾刷新速度和资源占用。</small>
            <small v-if="validation.hardwareSampleIntervalMs" id="settings-hardware-interval-error" class="field-error">{{ validation.hardwareSampleIntervalMs }}</small>
          </label>

          <label class="field" for="settings-network-interval">
            <span>网络处理间隔 (ms)</span>
            <input id="settings-network-interval" v-model.number="form.networkProcessingIntervalMs" type="number" min="500" max="60000" :disabled="isBusy" :aria-invalid="Boolean(validation.networkProcessingIntervalMs)" :aria-describedby="validation.networkProcessingIntervalMs ? 'settings-network-interval-help settings-network-interval-error' : 'settings-network-interval-help'" />
            <small id="settings-network-interval-help" class="field-help">控制网络事件追平、完成时间桶轮转和持久化检查；ETW 原始事件仍会持续采集。</small>
            <small v-if="validation.networkProcessingIntervalMs" id="settings-network-interval-error" class="field-error">{{ validation.networkProcessingIntervalMs }}</small>
          </label>

          <label class="field" for="settings-aggregate-interval">
            <span>默认统计粒度 (秒)</span>
            <input id="settings-aggregate-interval" v-model.number="form.aggregateIntervalSeconds" type="number" min="1" max="3600" :disabled="isBusy" :aria-invalid="Boolean(validation.aggregateIntervalSeconds)" :aria-describedby="validation.aggregateIntervalSeconds ? 'settings-aggregate-interval-help settings-aggregate-interval-error' : 'settings-aggregate-interval-help'" />
            <small id="settings-aggregate-interval-help" class="field-help">用于网络历史数据聚合，较小数值会带来更细的趋势。</small>
            <small v-if="validation.aggregateIntervalSeconds" id="settings-aggregate-interval-error" class="field-error">{{ validation.aggregateIntervalSeconds }}</small>
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

          <label class="field" for="settings-retention-days">
            <span>历史保留天数</span>
            <input id="settings-retention-days" v-model.number="form.historyRetentionDays" type="number" min="1" max="3650" :disabled="isBusy" :aria-invalid="Boolean(validation.historyRetentionDays)" :aria-describedby="validation.historyRetentionDays ? 'settings-retention-days-help settings-retention-days-error' : 'settings-retention-days-help'" />
            <small id="settings-retention-days-help" class="field-help">超过这个天数的历史数据会被清理。</small>
            <small v-if="validation.historyRetentionDays" id="settings-retention-days-error" class="field-error">{{ validation.historyRetentionDays }}</small>
          </label>

          <label class="field" for="settings-top-n">
            <span>默认排行数量</span>
            <input id="settings-top-n" v-model.number="form.topNDefault" type="number" min="1" max="100" :disabled="isBusy" :aria-invalid="Boolean(validation.topNDefault)" :aria-describedby="validation.topNDefault ? 'settings-top-n-help settings-top-n-error' : 'settings-top-n-help'" />
            <small id="settings-top-n-help" class="field-help">影响网络页面默认显示的排行数量。</small>
            <small v-if="validation.topNDefault" id="settings-top-n-error" class="field-error">{{ validation.topNDefault }}</small>
          </label>
        </div>
      </form>

      <aside class="card settings-side settings-side-elevated page-tier-panel">
        <div class="section-header">
          <div class="panel-title">
            <span class="panel-icon"><AppIcon name="status" :size="16" /></span>
            <div>
              <h3>保存说明</h3>
              <p class="panel-subtitle">先修改草稿，再一次性保存；保存成功后会明确提示生效范围。</p>
            </div>
          </div>
          <span class="section-tag">{{ isDirty ? '有未保存修改' : '已同步' }}</span>
        </div>

        <ul class="simple-list compact">
          <li>端口变更保存后需要重启服务，其余配置会在运行中逐步生效。</li>
          <li>刷新会重新读取服务端已保存的配置，并可能放弃当前草稿。</li>
          <li>输入超出合法范围时，保存按钮会保持禁用。</li>
        </ul>

        <div class="settings-side-actions">
          <button class="chip-button" :disabled="!isDirty || isBusy" @click="resetForm">
            <span class="button-inline-icon"><AppIcon name="refresh" :size="14" /></span>
            放弃修改
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
import { validateSettings } from '../utils/settingsValidation';

const defaultForm: AppSettingsDto = {
  httpPort: 5188,
  hardwareSampleIntervalMs: 1000,
  networkProcessingIntervalMs: 1000,
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
    'hardwareSampleIntervalMs' | 'networkProcessingIntervalMs'
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
      networkProcessingIntervalMs: 5000
    }
  },
  {
    id: 'balanced',
    kicker: 'RECOMMENDED',
    label: '均衡',
    description: '兼顾监控响应、图表细节和日常服务器开销。',
    values: {
      hardwareSampleIntervalMs: 2000,
      networkProcessingIntervalMs: 2000
    }
  },
  {
    id: 'realtime',
    kicker: 'HIGH FIDELITY',
    label: '实时',
    description: '更快响应与更细网络粒度，适合短期排障观察。',
    values: {
      hardwareSampleIntervalMs: 1000,
      networkProcessingIntervalMs: 1000
    }
  }
] as const;

const form = reactive<AppSettingsDto>({ ...defaultForm });
const original = ref<AppSettingsDto>({ ...defaultForm });
const isLoading = ref(false);
const isSaving = ref(false);
const hasLoaded = ref(false);
const errorMessage = ref('');
const successMessage = ref('');
let successMessageTimer: number | null = null;
let settingsRequestVersion = 0;
let isPageActive = false;

const validation = computed(() => validateSettings(form));

const isBusy = computed(() => isLoading.value || isSaving.value);

const canSave = computed(() =>
  hasLoaded.value && !isBusy.value && Object.keys(validation.value).length === 0
);

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

const networkProcessingChecksPerMinute = computed(() =>
  formatCompactNumber(safeDivide(60_000, form.networkProcessingIntervalMs))
);

const resourceImpactLabel = computed(() => {
  if (form.hardwareSampleIntervalMs >= 5000 && form.networkProcessingIntervalMs >= 5000) {
    return '低占用';
  }

  if (form.hardwareSampleIntervalMs <= 1000 || form.networkProcessingIntervalMs <= 1000) {
    return '高频';
  }

  return '均衡';
});

onMounted(() => {
  isPageActive = true;
  window.addEventListener('beforeunload', handleBeforeUnload);
  void loadInitialSettings();
});

onBeforeUnmount(() => {
  isPageActive = false;
  settingsRequestVersion++;
  window.removeEventListener('beforeunload', handleBeforeUnload);
  clearSuccessMessageTimer();
});

onBeforeRouteLeave(() => confirmDiscardChanges());

async function loadSettings(options: { confirmDiscard: boolean }) {
  if (options.confirmDiscard && !confirmDiscardChanges()) {
    return;
  }

  const requestVersion = ++settingsRequestVersion;
  isLoading.value = true;
  errorMessage.value = '';
  successMessage.value = '';

  try {
    // 调用后端 /api/settings：加载设置页初始配置
    const loaded = await getSettings();
    if (!isPageActive || requestVersion !== settingsRequestVersion) return;
    Object.assign(form, loaded);
    original.value = { ...loaded };
    hasLoaded.value = true;
  } catch (error) {
    if (isPageActive && requestVersion === settingsRequestVersion) {
      errorMessage.value = error instanceof Error ? error.message : '加载设置失败。';
    }
  } finally {
    if (requestVersion === settingsRequestVersion) isLoading.value = false;
  }
}

function loadInitialSettings() {
  void loadSettings({ confirmDiscard: false });
}

function refreshSettings() {
  void loadSettings({ confirmDiscard: true });
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
  if (!hasLoaded.value || isBusy.value) {
    return;
  }

  if (!isDirty.value) {
    errorMessage.value = '当前没有待保存的修改。';
    successMessage.value = '';
    return;
  }

  if (Object.keys(validation.value).length > 0) {
    errorMessage.value = '请先修正表单中的非法配置。';
    successMessage.value = '';
    return;
  }

  isSaving.value = true;
  errorMessage.value = '';
  successMessage.value = '';
  const requestVersion = ++settingsRequestVersion;
  const payload = { ...form };

  try {
    // 调用后端 /api/settings：把当前表单保存到服务端，并用返回值回填页面
    const saved = await saveSettings(payload);
    if (!isPageActive || requestVersion !== settingsRequestVersion) return;
    const portChanged = saved.httpPort !== original.value.httpPort;
    Object.assign(form, saved);
    original.value = { ...saved };
    showSuccessMessage(
      portChanged
        ? '采样、聚合和展示配置已实时生效；HTTP 端口变更需要重启服务。'
        : '采样、聚合和展示配置已实时生效。'
    );
  } catch (error) {
    if (isPageActive && requestVersion === settingsRequestVersion) {
      errorMessage.value = error instanceof Error ? error.message : '保存设置失败。';
    }
  } finally {
    if (requestVersion === settingsRequestVersion) isSaving.value = false;
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
  if (isSaving.value) {
    errorMessage.value = '正在保存，请稍候再离开此页面。';
    return false;
  }

  return !isDirty.value || window.confirm('当前设置尚未保存，确定要放弃这些修改吗？');
}

function handleBeforeUnload(event: BeforeUnloadEvent) {
  if (!isDirty.value && !isSaving.value) {
    return;
  }

  event.preventDefault();
  event.returnValue = '';
}

function safeDivide(dividend: number, divisor: number) {
  return Number.isFinite(divisor) && divisor > 0 ? dividend / divisor : 0;
}

function formatCompactNumber(value: number) {
  return Math.round(value).toLocaleString('zh-CN');
}
</script>
