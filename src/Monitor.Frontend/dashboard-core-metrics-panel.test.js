import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/DashboardView.vue', import.meta.url);
const stylesPath = new URL('./src/styles.css', import.meta.url);

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
