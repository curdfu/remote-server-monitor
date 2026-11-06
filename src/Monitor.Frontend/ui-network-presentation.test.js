import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const modalPath = new URL('./src/components/AccessibleModal.vue', import.meta.url);
const stylesPath = new URL('./src/styles/network.css', import.meta.url);
const sharedStylesPath = new URL('./src/styles.css', import.meta.url);

test('network presentation includes Other traffic and applied-filter lifecycle', async () => {
  const source = await readFile(viewPath, 'utf8');
  assert.match(source, /otherTotalBytes/);
  assert.match(source, /otherPercent/);
  assert.match(source, /const appliedFilters = ref<NetworkFilters \| null>\(null\)/);
  assert.match(source, /requestVersion !== dashboardRequestVersion/);
  assert.match(source, /结束时间必须晚于开始时间/);
  assert.match(source, /formatRatioPercent\(overviewTotalBytes/);
  assert.match(source, /overviewSummary\.value = dashboard\.overview/);
  assert.match(source, /v-if="!overviewSummary" class="summary-unavailable"/);
  const distribution = source.match(/<div v-if="overviewSummary" class="ratio-overview network-distribution-values">([\s\S]*?)\n          <\/div>/)?.[1] ?? '';
  for (const [label, value, percent] of [
    ['WAN', 'wanTotalBytes', 'wanPercent'],
    ['LAN', 'lanTotalBytes', 'lanPercent'],
    ['Loopback', 'loopbackTotalBytes', 'loopbackPercent'],
    ['其他', 'otherTotalBytes', 'otherPercent']
  ]) {
    assert.match(distribution, new RegExp(`<span class="ratio-summary-label">${label}</span>[\\s\\S]*?formatBytes\\(${value}\\)[\\s\\S]*?formatRatioPercent\\(overviewTotalBytes, ${percent}\\)`));
  }
});

test('app details use a native modal component with focus return metadata', async () => {
  const [source, modal] = await Promise.all([
    readFile(viewPath, 'utf8'),
    readFile(modalPath, 'utf8')
  ]);
  assert.match(source, /<AccessibleModal/);
  assert.match(source, /:return-focus="dialogTrigger"/);
  assert.match(modal, /dialog\.showModal\(\)/);
  assert.match(modal, /@cancel="handleCancel"/);
  assert.match(modal, /target\?\.isConnected/);
  assert.doesNotMatch(source, /restoreDialogFocus/);
  assert.match(source, /getSegmentUploadBytes[\s\S]*?isDirectionIncluded\(activeFilters\.direction, 'upload'\)/);
  assert.match(source, /getSegmentDownloadBytes[\s\S]*?isDirectionIncluded\(activeFilters\.direction, 'download'\)/);
  assert.doesNotMatch(source, /function getScoped(?:Upload|Download)Bytes[\s\S]*?isDirectionIncluded(?=[\s\S]*?function getRankingPercent)/);
});

test('segment table confines horizontal scrolling and presents both complete timestamps', async () => {
  const [source, styles, shared] = await Promise.all([
    readFile(viewPath, 'utf8'), readFile(stylesPath, 'utf8'), readFile(sharedStylesPath, 'utf8')
  ]);
  assert.match(source, /class="table-shell app-segments-table-scroll"[^>]*tabindex="0"/);
  assert.match(source, /class="app-segment-time-cell"[\s\S]*?formatSegmentTime\(segment\.from\)[\s\S]*?formatSegmentTime\(segment\.to\)/);
  const panel = styles.match(/\.app-segments-panel\s*\{([^{}]*)\}/)?.[1] ?? '';
  assert.match(panel, /grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(panel, /min-width:\s*0/);
  assert.match(panel, /overflow-x:\s*hidden/);
  assert.match(panel, /overflow-y:\s*auto/);
  const tableShell = shared.match(/\.table-shell\s*\{([^{}]*)\}/)?.[1] ?? '';
  assert.match(tableShell, /min-width:\s*0/);
  assert.match(tableShell, /max-width:\s*100%/);
  assert.match(tableShell, /overflow-x:\s*auto/);
  assert.match(styles, /\.app-segments-table-scroll\s*\{[^}]*max-width:\s*100%;[^}]*overflow:\s*auto/);
  assert.match(styles, /\.app-segments-table \.app-segment-time-cell\s*\{[^}]*white-space:\s*normal/);
});
