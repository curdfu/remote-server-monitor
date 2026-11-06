import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('upload/download summary uses the scoped totals from the dashboard response', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /totalsSummary\.value = dashboard\.totals/,
    'expected upload/download cards to use the combined response scoped totals',
  );

  assert.match(
    source,
    /getNetworkDashboard\(\{[\s\S]*?scope: filters\.scope,[\s\S]*?direction: filters\.direction/,
    'expected the combined request to carry the active scope and direction',
  );
  assert.match(source, /const totalUploadBytes = computed\(\(\) =>\s*totalsSummary\.value\?\.totalUploadBytes/);
  assert.match(source, /const totalDownloadBytes = computed\(\(\) =>\s*totalsSummary\.value\?\.totalDownloadBytes/);
  assert.match(source, /v-if="totalsSummary" class="ratio-overview traffic-summary-values"/);
  assert.match(source, /v-if="!totalsSummary" class="summary-unavailable"/);
  for (const label of ['累计上传', '累计下载', '累计总流量']) {
    assert.equal((source.match(new RegExp(`<span class="ratio-summary-label">${label}</span>`, 'g')) ?? []).length, 1);
  }
  assert.doesNotMatch(source, /class="ratio-group"|主导方向|主导网络/);
});
