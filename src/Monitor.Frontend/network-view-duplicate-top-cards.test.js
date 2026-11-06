import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const networkStylesPath = new URL('./src/styles/network.css', import.meta.url);

test('network view removes the top overview strip and keeps the ranking panel in its own analytics column', async () => {
  const source = await readFile(viewPath, 'utf8');
  const styles = await readFile(networkStylesPath, 'utf8');

  assert.doesNotMatch(source, /network-section-overview|network-overview-strip|概览速览/, 'top overview strip should be removed entirely');

  assert.match(
    source,
    /<div class="network-section network-section-column network-section-column-ranking">[\s\S]*?<article class="card dashboard-panel-card network-ranking-panel network-section network-section-ranking">/,
    'expected the ranking panel to live inside its own analytics column wrapper',
  );

  const uploadDownloadIndex = source.indexOf('<h3>累计上传 / 下载</h3>');
  const wanLanIndex = source.indexOf('<h3>网络流量分布</h3>');
  const rankingIndex = source.indexOf('<h3>应用流量排行</h3>');

  assert.ok(uploadDownloadIndex >= 0, 'expected upload/download panel to remain');
  assert.ok(wanLanIndex > uploadDownloadIndex, 'expected WAN / LAN panel to render after upload/download in the left column markup');
  assert.ok(rankingIndex > wanLanIndex, 'expected ranking panel to render after the left column stack in the markup');

  assert.match(
    styles,
    /\.network-page \.ranking-table-head,\s*\.network-page \.ranking-list \.ranking-item\s*\{[^}]*display:\s*grid;[^}]*grid-template-columns:/,
    'expected desktop ranking rows and the table header to share one column grid',
  );
  assert.match(
    styles,
    /\.network-page \.ranking-list \.ranking-item\s*\{[^}]*grid-template-rows:\s*auto 4px;/,
    'expected ranking rows to reserve a separate progress row',
  );
  assert.match(
    styles,
    /\.network-page \.ranking-progress\s*\{[^}]*grid-column:\s*4\s*\/\s*6;[^}]*grid-row:\s*2;/,
    'expected ranking progress beneath the traffic columns',
  );
  assert.match(styles, /\.network-page \.network-section-analytics\s*\{[^}]*display:\s*grid;/);
  assert.match(styles, /\.network-page \.network-section-column-analytics\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*3fr\)\s+minmax\(0,\s*4fr\)/);
  assert.match(styles, /\.network-page \.ranking-item:hover,[\s\S]*?\{[^}]*background:\s*var\(--surface-0\)/);
  const tablet = styles.slice(styles.indexOf('@media (max-width: 960px)'));
  assert.match(tablet, /\.network-page \.ranking-main strong\s*\{[^}]*grid-row:\s*1;/);
  assert.match(tablet, /\.network-page \.ranking-main small\s*\{[^}]*grid-row:\s*2;/);
});
