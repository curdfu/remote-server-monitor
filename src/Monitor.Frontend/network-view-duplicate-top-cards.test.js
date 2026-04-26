import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('network view removes the top overview strip and keeps the ranking panel in its own analytics column', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.doesNotMatch(source, /network-section-overview|network-overview-strip|概览速览/, 'top overview strip should be removed entirely');

  assert.match(
    source,
    /<div class="network-section network-section-column network-section-column-ranking">[\s\S]*?<article class="card dashboard-panel-card network-ranking-panel network-section network-section-ranking">/,
    'expected the ranking panel to live inside its own analytics column wrapper',
  );

  const uploadDownloadIndex = source.indexOf('<h3>累计上传 / 下载</h3>');
  const wanLanIndex = source.indexOf('<h3>WAN / LAN 占比</h3>');
  const rankingIndex = source.indexOf('<h3>应用流量排行</h3>');

  assert.ok(uploadDownloadIndex >= 0, 'expected upload/download panel to remain');
  assert.ok(wanLanIndex > uploadDownloadIndex, 'expected WAN / LAN panel to render after upload/download in the left column markup');
  assert.ok(rankingIndex > wanLanIndex, 'expected ranking panel to render after the left column stack in the markup');
});
