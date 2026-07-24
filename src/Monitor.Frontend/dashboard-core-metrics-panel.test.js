import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/DashboardView.vue', import.meta.url);
const stylesPath = new URL('./src/styles.css', import.meta.url);
const observatoryStylesPath = new URL('./src/styles/observatory.css', import.meta.url);

test('dashboard core metrics are wrapped in a single outer panel shell', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /<section class="dashboard-section dashboard-core-metrics-section">[\s\S]*?<article class="card dashboard-core-metrics-panel">[\s\S]*?<div class="section-header section-header-rich dashboard-core-metrics-header">[\s\S]*?<div class="dashboard-hero-grid page-tier-stats">/,
    'expected the core metrics section to use a single outer panel shell around the existing header and hero grid',
  );

  assert.match(
    source,
    /<div class="section-header section-header-rich dashboard-core-metrics-header">\s*<div>\s*<h3>核心运行指标<\/h3>/,
    'expected the new outer core metrics header to stay text-first and iconless',
  );
});

test('dashboard core metrics panel styles do not rewrite the hero grid columns', async () => {
  const source = await readFile(stylesPath, 'utf8');

  assert.match(source, /\.dashboard-core-metrics-panel \{[\s\S]*?\}/, 'expected a dedicated outer panel style');
  assert.match(source, /\.dashboard-core-metrics-panel-body \{[\s\S]*?\}/, 'expected a dedicated panel body style');
  assert.doesNotMatch(
    source,
    /\.dashboard-core-metrics-panel(?:-body)? [^{]*\{[^}]*grid-template-columns:/,
    'expected the outer panel styles to avoid redefining hero grid column counts',
  );
});

test('dashboard system status owns the uptime details without a duplicate metric card', async () => {
  const [viewSource, styleSource] = await Promise.all([
    readFile(viewPath, 'utf8'),
    readFile(observatoryStylesPath, 'utf8'),
  ]);

  assert.match(
    viewSource,
    /<dt>最新样本<\/dt>[\s\S]*?<dt>开机时间<\/dt>[\s\S]*?<dt>运行时间<\/dt>[\s\S]*?<dt>存储状态<\/dt>/,
    'expected boot time to sit alongside the existing system status facts',
  );
  assert.match(
    viewSource,
    /<dt>开机时间<\/dt>\s*<dd>{{ formatDateTime\(bootTimeText\) }}<\/dd>/,
    'expected boot time to use the existing formatter',
  );
  assert.doesNotMatch(viewSource, /label="已开机"/, 'expected the duplicate uptime metric card to be removed');
  assert.match(
    styleSource,
    /\.system-status-facts \{[\s\S]*?grid-template-columns: repeat\(4, minmax\(0, 1fr\)\);[\s\S]*?\}/,
    'expected four equal status fact columns',
  );
  assert.match(
    styleSource,
    /\.system-status-facts dd \{[\s\S]*?font-size: 14px;[\s\S]*?\}/,
    'expected the status values to use the larger readable type size',
  );
});
