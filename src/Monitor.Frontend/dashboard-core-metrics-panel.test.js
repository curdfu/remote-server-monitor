import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/DashboardView.vue', import.meta.url);
const metricCardPath = new URL('./src/components/MetricCard.vue', import.meta.url);
const stylesPath = new URL('./src/styles.css', import.meta.url);
const observatoryStylesPath = new URL('./src/styles/observatory.css', import.meta.url);

test('dashboard core metrics are wrapped in a single outer panel shell', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /<section class="dashboard-section dashboard-core-metrics-section">[\s\S]*?<article class="card dashboard-core-metrics-panel">[\s\S]*?<div class="section-header section-header-rich dashboard-core-metrics-header">[\s\S]*?<div class="dashboard-hero-grid dashboard-primary-metrics">/,
    'expected the core metrics section to use a single outer panel shell around the existing header and hero grid',
  );

  assert.match(
    source,
    /<div class="section-header section-header-rich dashboard-core-metrics-header">\s*<div>\s*<h3>核心运行指标<\/h3>/,
    'expected the new outer core metrics header to stay text-first and iconless',
  );
});

test('dashboard core metrics use the shared panel surface and retain a responsive hero grid', async () => {
  const [sharedStyles, dashboardStyles] = await Promise.all([
    readFile(stylesPath, 'utf8'),
    readFile(observatoryStylesPath, 'utf8'),
  ]);

  assert.match(sharedStyles, /\.card\s*\{[^}]*border:\s*1px solid var\(--line\);[^}]*background:\s*var\(--surface-1\)/);
  assert.match(dashboardStyles, /\.dashboard-core-metrics-panel-body\s*\{[^}]*min-width:\s*0/);
  assert.match(dashboardStyles, /\.dashboard-primary-metrics\s*\{[^}]*grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
  assert.match(dashboardStyles, /\.dashboard-secondary-metrics\s*\{[^}]*grid-template-columns:\s*repeat\(4,\s*minmax\(0,\s*1fr\)\)/);
  assert.doesNotMatch(
    dashboardStyles,
    /\.dashboard-core-metrics-panel(?:-body)? [^{]*\{[^}]*grid-template-columns:/,
    'expected the outer panel styles not to redefine the hero grid columns',
  );
});

test('core metrics include all six readings without a separate sensor panel', async () => {
  const [view, styles] = await Promise.all([
    readFile(viewPath, 'utf8'), readFile(observatoryStylesPath, 'utf8')
  ]);
  const core = view.slice(view.indexOf('dashboard-core-metrics-panel-body'), view.indexOf('<details class="metric-model-details">'));
  const cards = [...core.matchAll(/<MetricCard\b[\s\S]*?\/>/g)].map(([card]) => card);
  assert.deepEqual(cards.map((card) => card.match(/label="([^"]+)"/)[1]), [
    'CPU 占用', '内存已使用', 'CPU 当前温度', 'CPU 当前频率', 'CPU 当前功耗', '最高磁盘温度'
  ]);
  assert.match(cards[0], /:percent="hardware\?\.cpuUsagePercent"/);
  assert.match(cards[1], /:percent="memoryUsagePercent"/);
  assert.ok(cards.slice(2).every((card) => card.includes('small-value')));
  assert.match(core, /:value="formatFrequency\(hardware\?\.cpuFrequencyMhz\)"/);
  assert.match(core, /:value="formatNullable\(hardware\?\.cpuTemperatureC, '°C'\)"/);
  assert.match(core, /:value="formatNullable\(hardware\?\.cpuPowerWatts, 'W'\)"/);
  assert.match(core, /:badge="temperatureBadge\(hardware\?\.cpuTemperatureC\)"/);
  assert.doesNotMatch(view, /dashboard-sensor-panel|dashboard-sensor-grid|辅助传感器/);
  assert.doesNotMatch(styles, /dashboard-sensor-panel|dashboard-sensor-grid/);
  const mobile = styles.slice(styles.indexOf('@media (max-width: 720px)'));
  assert.match(mobile, /\.dashboard-secondary-metrics\s*\{[^}]*grid-template-columns:\s*repeat\(2,\s*minmax\(0,\s*1fr\)\)/);
  assert.match(mobile, /\.dashboard-secondary-metrics \.metric-card:nth-child\(3\)\s*\{[^}]*border-left:\s*0/);
});

test('metric grid uses continuous dividers without doubling row spacing', async () => {
  const styles = await readFile(observatoryStylesPath, 'utf8');
  assert.match(styles, /\.dashboard-hero-grid\s*\{[^}]*gap:\s*0;/);
  assert.match(styles, /\.dashboard-hero-grid \.metric-card \+ \.metric-card\s*\{[^}]*border-left:\s*1px solid var\(--line\)/);
  assert.match(styles, /\.dashboard-secondary-metrics\s*\{[^}]*border-top:\s*1px solid var\(--line\)/);
  const mobile = styles.slice(styles.indexOf('@media (max-width: 720px)'));
  assert.match(mobile, /\.dashboard-secondary-metrics \.metric-card:nth-child\(n \+ 3\)\s*\{[^}]*border-top:\s*1px solid var\(--line\)/);
});

test('dashboard core metrics highlights the hottest disk with its temperature and name', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(source, /label="最高磁盘温度"/);
  assert.match(source, /:value="formatNullable\(hottestDisk\?\.temperatureC, '°C'\)"/);
  assert.match(source, /:hint="hottestDiskName"/);
  assert.match(source, /:badge="temperatureBadge\(hottestDisk\?\.temperatureC\)"/);
  assert.match(
    source,
    /const hottestDisk = computed\(\(\) => findHottestDisk\(hardware\.value\?\.disks \?\? \[\]\)\)/,
    'expected the card to select the hottest disk independently of table sorting',
  );
});

test('mobile core metrics keep memory context and allow complete model names to be opened', async () => {
  const [view, card, styles] = await Promise.all([
    readFile(viewPath, 'utf8'),
    readFile(metricCardPath, 'utf8'),
    readFile(observatoryStylesPath, 'utf8'),
  ]);
  const mobile = styles.slice(styles.indexOf('@media (max-width: 720px)'));
  assert.match(view, /label="内存已使用"[\s\S]*?:hint="memoryCapacityHint"/);
  assert.match(view, /const memoryCapacityHint = computed\([\s\S]*?formatMemoryHint\(hardware\.value\?\.memoryTotalMb\)/);
  assert.match(card, /<span v-if="hint" class="metric-hint" :title="hint">\{\{ hint \}\}<\/span>/);
  assert.match(card, /v-if="fill !== null" class="metric-track"/);
  assert.match(mobile, /\.dashboard-secondary-metrics \.metric-hint\s*\{[^}]*overflow:\s*hidden;[^}]*text-overflow:\s*ellipsis;/);
  assert.doesNotMatch(mobile, /\.dashboard-secondary-metrics \.metric-hint\s*\{[^}]*display:\s*none/);
  assert.match(view, /<details class="metric-model-details">[\s\S]*?\{\{ coreMetricDetails \}\}[\s\S]*?<\/details>/);
  assert.match(view, /const coreMetricDetails = computed\([\s\S]*?formatCpuNameHint\(hardware\.value\?\.cpuName\)[\s\S]*?hottestDiskName\.value/);
  assert.doesNotMatch(styles, /\.metric-model-details\s*\{[^}]*display:\s*none/);
});

test('dashboard starts with content and keeps refresh and freshness feedback without a banner', async () => {
  const [view, styles] = await Promise.all([
    readFile(viewPath, 'utf8'), readFile(observatoryStylesPath, 'utf8')
  ]);
  assert.doesNotMatch(view, /PageHeader|dashboard-overview-facts|bootTimeText|<dt>开机时间|<dt>运行时间/);
  assert.match(view, /dashboard-core-metrics-header[\s\S]*?<h3>核心运行指标<\/h3>[\s\S]*?@click="refreshDashboard"/);
  assert.doesNotMatch(styles, /\.page-header|dashboard-overview-facts|dashboard-banner/);
  assert.doesNotMatch(view, /system-status-card|latestSampleText|storageStatusText|系统运行平稳|<dt>最新样本|<dt>存储状态|label="已开机"/);
  assert.doesNotMatch(styles, /system-status-/);
  assert.match(view, /const errorMessage = computed\([\s\S]*?hardwareFreshnessNotice\.value/);
  assert.match(view, /hardwareFreshness\.value\.state === 'stale'/);
  assert.match(view, /hardwareFreshness\.value\.state === 'invalid-time'/);
});
