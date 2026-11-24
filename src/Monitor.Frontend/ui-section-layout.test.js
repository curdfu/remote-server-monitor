import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const sharedStylesPath = new URL('./src/styles.css', import.meta.url);
const dashboardStylesPath = new URL('./src/styles/observatory.css', import.meta.url);
const networkStylesPath = new URL('./src/styles/network.css', import.meta.url);
const settingsStylesPath = new URL('./src/styles/settings.css', import.meta.url);
const dashboardPath = new URL('./src/views/DashboardView.vue', import.meta.url);
const networkPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const settingsPath = new URL('./src/views/SettingsView.vue', import.meta.url);
const processPath = new URL('./src/components/CpuProcessPanel.vue', import.meta.url);
const trendPath = new URL('./src/components/MetricTrend.vue', import.meta.url);
const appPath = new URL('./src/App.vue', import.meta.url);
const mainPath = new URL('./src/main.ts', import.meta.url);

function declarationsFor(source, selector) {
  const withoutComments = source.replace(/\/\*[\s\S]*?\*\//g, '');
  return [...withoutComments.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .filter(([, names]) => names.split(',').some((name) => name.trim() === selector))
    .map(([, , body]) => body);
}

function tokens(block) {
  return Object.fromEntries([...block.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)]
    .map(([, name, value]) => [name, value.trim()]));
}

test('main panels use the shared bordered surface in each view', async () => {
  const [styles, dashboard, network, settings, process] = await Promise.all([
    readFile(sharedStylesPath, 'utf8'), readFile(dashboardPath, 'utf8'),
    readFile(networkPath, 'utf8'), readFile(settingsPath, 'utf8'), readFile(processPath, 'utf8')
  ]);
  const card = declarationsFor(styles, '.card').join('\n');
  assert.match(card, /border:\s*1px solid var\(--line\)/);
  assert.match(card, /border-radius:\s*var\(--radius-md\)/);
  assert.match(card, /background:\s*var\(--surface-1\)/);
  for (const [source, names] of [
    [dashboard, ['dashboard-core-metrics-panel', 'dashboard-trends-panel']],
    [network, ['filters-card', 'network-section-upload-download', 'network-section-wan-lan', 'network-section-ranking']],
    [settings, ['settings-profile-panel', 'settings-layout']],
    [process, ['dashboard-process-panel']]
  ]) {
    for (const name of names) {
      assert.match(source, new RegExp(`class="(?=[^"]*\\bcard\\b)(?=[^"]*\\b${name}\\b)[^"]*"`), `${name} should use the shared card`);
    }
  }
});

test('dashboard keeps its reading order and switches among the three measured trend series', async () => {
  const [view, trend] = await Promise.all([readFile(dashboardPath, 'utf8'), readFile(trendPath, 'utf8')]);
  const names = [
    'dashboard-core-metrics-section', 'dashboard-trends-panel', '<CpuProcessPanel',
    'dashboard-disk-space-panel', 'dashboard-disk-temperature-panel'
  ];
  let previous = -1;
  for (const name of names) {
    const index = view.indexOf(name);
    assert.ok(index > previous, `${name} should follow the preceding dashboard section`);
    previous = index;
  }
  assert.match(view, /:aria-pressed="activeTrendId === option.id"\s+@click="activeTrendId = option.id"/);
  assert.match(view, /<MetricTrend[\s\S]*?:points="activeTrend.points.value"/);
  assert.match(view, /id: 'cpu', label: 'CPU 占用', points: cpuTrend/);
  assert.match(view, /id: 'memory', label: '内存占用', points: memoryTrend/);
  assert.match(view, /id: 'temperature', label: 'CPU 温度', points: temperatureTrend, unit: '°C', scaleKind: 'temperature'/);
  assert.match(trend, /role="img"\s+:aria-label="summary"/);
  assert.match(trend, /splitTrendSegments\(props\.points, gapMs\.value\)/);
  assert.match(trend, /v-else class="trend-empty"/);
});

test('tablet layout uses shared column tracks and mobile volume rows keep all labels', async () => {
  const [styles, view] = await Promise.all([readFile(dashboardStylesPath, 'utf8'), readFile(dashboardPath, 'utf8')]);
  assert.match(styles, /@media \(min-width: 700px\)\s*\{\s*\.dashboard-page \{[^}]*grid-template-columns:\s*minmax\(0, 3fr\) minmax\(0, 2fr\)/);
  assert.match(styles, /\.dashboard-trends-panel, \.dashboard-disk-space-panel \{ grid-column: 1; \}/);
  assert.match(styles, /\.dashboard-process-panel, \.dashboard-disk-temperature-panel \{ grid-column: 2; \}/);
  assert.match(styles, /\.trend-plot-body\s*\{[^}]*min-height:\s*156px/);
  const mobile = styles.slice(styles.indexOf('@media (max-width: 560px)'));
  assert.match(mobile, /\.disk-space-table tbody tr \{[^}]*grid-template-columns:\s*repeat\(3, minmax\(0, 1fr\)\)/);
  for (const label of ['总容量', '已用空间', '剩余空间']) {
    assert.ok(view.includes(`data-label="${label}"`));
  }
  assert.match(view, /@click="toggleDiskSort\('temperatureC'\)"/);
});

test('process CPU keeps the pie and name together and exposes long names on touch or keyboard', async () => {
  const [view, styles] = await Promise.all([readFile(processPath, 'utf8'), readFile(dashboardStylesPath, 'utf8')]);
  assert.match(view, /class="process-cpu-identity"[\s\S]*?class="process-cpu-pie"[\s\S]*?pathLength="100"[\s\S]*?class="process-cpu-name"/);
  assert.match(view, /:aria-expanded="expandedProcessKey === processKey\(process\)"[\s\S]*?@click="toggleProcessName\(process\)"/);
  assert.match(view, /v-show="expandedProcessKey === processKey\(process\)"[\s\S]*?class="process-cpu-full-name"/);
  assert.match(styles, /\.process-cpu-identity \{[^}]*grid-template-columns:\s*14px minmax\(0, 1fr\) auto/);
  assert.match(styles, /\.process-cpu-name \{[^}]*text-overflow:\s*ellipsis/);
  assert.match(styles, /\.process-cpu-full-name \{[^}]*overflow-wrap:\s*anywhere/);
});

test('network retains its filter heading and secondary table surfaces', async () => {
  const [view, styles] = await Promise.all([readFile(networkPath, 'utf8'), readFile(networkStylesPath, 'utf8')]);
  assert.match(view, /network-filter-heading[\s\S]*?<h3 id="network-filters-title">筛选条件<\/h3>/);
  assert.match(declarationsFor(styles, '.network-page .ranking-table-head').join('\n'), /background:\s*var\(--surface-0\)/);
  const ignored = declarationsFor(styles, '.network-page .ranking-ignored-panel').join('\n');
  assert.match(ignored, /border:\s*1px solid var\(--line\)/);
  assert.match(ignored, /background:\s*var\(--surface-0\)/);
});

test('light, explicit dark and system theme modes share their visual token contract', async () => {
  const [styles, app, main] = await Promise.all([
    readFile(sharedStylesPath, 'utf8'), readFile(appPath, 'utf8'), readFile(mainPath, 'utf8')
  ]);
  const light = styles.match(/:root\s*\{([^{}]+)\}/)?.[1] ?? '';
  const systemDark = styles.match(/@media\s*\(prefers-color-scheme:\s*dark\)\s*\{\s*:root:not\(\[data-theme=['"]light['"]\]\)\s*\{([^{}]+)\}/)?.[1] ?? '';
  const explicitDark = styles.match(/:root\[data-theme=['"]dark['"]\]\s*\{([^{}]+)\}/)?.[1] ?? '';
  const lightTokens = tokens(light);
  const automaticTokens = tokens(systemDark);
  const explicitTokens = tokens(explicitDark);
  for (const name of ['--canvas', '--surface-0', '--surface-1', '--surface-2', '--ink', '--ink-secondary', '--ink-tertiary', '--line', '--signal', '--warning', '--danger', '--button-fill', '--button-ink']) {
    assert.ok(lightTokens[name], `light theme must define ${name}`);
    assert.ok(automaticTokens[name], `system dark theme must define ${name}`);
  }
  assert.deepEqual(automaticTokens, explicitTokens, 'system dark and explicit dark should use identical tokens');
  assert.match(light, /color-scheme:\s*light/);
  assert.match(systemDark, /color-scheme:\s*dark/);
  assert.match(explicitDark, /color-scheme:\s*dark/);
  for (const theme of ['system', 'light', 'dark']) {
    assert.match(app, new RegExp(`setThemePreference\\('${theme}'\\)`));
  }
  assert.match(app, /if \(value === 'system'\)\s*\{\s*delete root\.dataset\.theme/);
  assert.match(app, /root\.dataset\.theme = value/);
  assert.match(app, /window\.localStorage\.setItem\(themeStorageKey, value\)/);
  assert.match(main, /import '\.\/styles\.css';\s*import '\.\/styles\/observatory\.css';\s*import '\.\/styles\/network\.css';\s*import '\.\/styles\/settings\.css';/);
});

test('stylesheet source avoids gradients, explicit sub-12px text and animated layout dimensions', async () => {
  for (const path of [sharedStylesPath, dashboardStylesPath, networkStylesPath, settingsStylesPath]) {
    const source = (await readFile(path, 'utf8')).replace(/\/\*[\s\S]*?\*\//g, '');
    const name = path.pathname.split('/').at(-1);
    assert.doesNotMatch(source, /\b(?:repeating-)?(?:linear|radial|conic)-gradient\s*\(/i, `${name} should not contain gradients`);
    for (const [, property, value] of source.matchAll(/(?:^|[;{])\s*(font-size|font)\s*:\s*([^;{}]+)/gi)) {
      const sizes = [...value.matchAll(/(?:^|[^\w.-])(\d*\.?\d+)px\b/gi)].map((match) => Number(match[1]));
      for (const size of property.toLowerCase() === 'font' ? sizes.slice(0, 1) : sizes) {
        assert.ok(size >= 12, `${name}: ${property}: ${value.trim()} contains text smaller than 12px`);
      }
    }
    for (const [, value] of source.matchAll(/(?:^|[;{])\s*transition(?:-property)?\s*:\s*([^;{}]+)/gi)) {
      assert.doesNotMatch(value, /(?:^|[,\s])(?:all|width|height|min-width|max-width|min-height|max-height)(?:[,\s]|$)/i, `${name} should not transition dimensions`);
    }
    for (const [, animation, body] of source.matchAll(/@(?:-webkit-)?keyframes\s+([\w-]+)\s*\{((?:[^{}]*\{[^{}]*\}\s*)+)\}/gi)) {
      assert.doesNotMatch(body, /(?:^|[;{])\s*(?:width|height|min-width|max-width|min-height|max-height)\s*:/i, `${name}: ${animation} should not animate dimensions`);
    }
  }
});
