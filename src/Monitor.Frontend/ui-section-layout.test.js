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

test('dashboard keeps its meaningful reading order and CPU-led trend data', async () => {
  const [view, trend] = await Promise.all([readFile(dashboardPath, 'utf8'), readFile(trendPath, 'utf8')]);
  const names = [
    'dashboard-core-metrics-section', 'dashboard-trends-panel',
    '<CpuProcessPanel', 'dashboard-storage-grid'
  ];
  let previous = -1;
  for (const name of names) {
    const index = view.indexOf(name);
    assert.ok(index > previous, `${name} should follow the preceding dashboard section`);
    previous = index;
  }
  assert.match(view, /<MetricTrend\s+class="trend-card-primary"\s+title="CPU 使用率"[\s\S]*?:points="cpuTrend"/);
  assert.match(view, /<MetricTrend\s+title="内存使用率"[\s\S]*?:points="memoryTrend"/);
  assert.match(view, /<MetricTrend\s+title="CPU 温度"[\s\S]*?:points="temperatureTrend"[\s\S]*?scale-kind="temperature"/);
  assert.match(trend, /role="img"\s+:aria-label="summary"/);
  assert.match(trend, /splitTrendSegments\(props\.points, gapMs\.value\)/);
  assert.match(trend, /v-else class="trend-empty"/);
});

test('desktop CPU plot spans two rows and mobile trends stack with usable plot heights', async () => {
  const styles = await readFile(dashboardStylesPath, 'utf8');
  const grid = declarationsFor(styles, '.trend-grid');
  const primary = declarationsFor(styles, '.trend-card-primary');
  assert.ok(grid.some((rule) => /grid-template-columns:\s*minmax\(0,\s*1\.8fr\)\s+minmax\(0,\s*1fr\)/.test(rule) && /grid-template-rows:\s*repeat\(2,/.test(rule)));
  assert.ok(primary.some((rule) => /grid-row:\s*1\s*\/\s*span\s*2/.test(rule)));
  assert.match(styles, /\.trend-plot-body\s*\{[^}]*min-height:\s*64px/);
  assert.match(styles, /\.trend-card-primary \.trend-plot-body\s*\{[^}]*min-height:\s*200px/);
  assert.match(styles, /\.trend-plot svg\s*\{[^}]*width:\s*calc\(100%\s*-\s*40px\);[^}]*height:\s*100%/);
  const desktopStart = styles.indexOf('@media (min-width: 1200px)');
  assert.ok(desktopStart >= 0, 'expected a desktop breakpoint');
  const desktop = styles.slice(desktopStart, styles.indexOf('@media (max-width: 1199px)', desktopStart));
  assert.match(desktop, /\.dashboard-trends-panel\s*\{[^}]*align-self:\s*stretch/);
  assert.match(desktop, /\.dashboard-process-panel\s*\{[^}]*align-self:\s*stretch/);
  assert.match(styles, /\.trend-grid\s*\{[^}]*flex:\s*1/);
  const mobileStart = styles.indexOf('@media (max-width: 720px)');
  assert.ok(mobileStart >= 0, 'expected a phone breakpoint');
  const mobile = styles.slice(mobileStart);
  assert.match(mobile, /\.trend-grid\s*\{[^}]*grid-template-columns:\s*1fr;[^}]*grid-template-rows:\s*auto/);
  assert.match(mobile, /\.trend-card-primary\s*\{[^}]*grid-row:\s*auto/);
  assert.match(mobile, /\.trend-card-primary \.trend-plot-body\s*\{[^}]*min-height:\s*180px/);
  assert.match(mobile, /\.trend-plot-body\s*\{[^}]*min-height:\s*88px/);
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
