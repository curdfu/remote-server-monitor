import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const observatoryStylesPath = new URL('./src/styles/observatory.css', import.meta.url);

test('network view removes the top overview strip and keeps the ranking panel in its own analytics column', async () => {
  const source = await readFile(viewPath, 'utf8');
  const styles = await readFile(observatoryStylesPath, 'utf8');

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

  assert.match(
    styles,
    /\.network-page \.ranking-list \.ranking-item \{[\s\S]*?display: grid;[\s\S]*?grid-template-columns:[\s\S]*?grid-template-rows: auto 4px;/,
    'expected desktop ranking rows to use the same five-column grid as the table header',
  );
  assert.match(
    styles,
    /\.network-page \.ranking-progress \{[\s\S]*?grid-column: 4 \/ 6;[\s\S]*?grid-row: 2;/,
    'expected ranking progress to occupy its own row beneath the traffic columns',
  );
  assert.match(
    styles,
    /\.network-page \.network-section-column-ranking \{[\s\S]*?grid-column: 1 \/ -1;[\s\S]*?grid-row: 1;/,
    'expected the ranking panel to occupy the first analytics row below realtime traffic',
  );
  assert.match(
    styles,
    /\.network-page \.network-section-upload-download,[\s\S]*?\.network-page \.network-section-wan-lan \{[\s\S]*?grid-row: 2;/,
    'expected the traffic summary panels to render after the ranking panel',
  );
  assert.match(
    styles,
    /\.network-page \.ranking-list \.ranking-item \{[\s\S]*?transition:\s*[\s\S]*?background-color 160ms ease,[\s\S]*?border-color 160ms ease;/,
    'expected ranking rows to transition only non-geometric visual properties',
  );
  assert.match(
    styles,
    /\.network-page \.ranking-list \.ranking-item:hover \{[\s\S]*?transform: none;[\s\S]*?background: color-mix\([\s\S]*?box-shadow: none;/,
    'expected ranking hover state to stay still and use only a gentle background tint',
  );
  assert.match(
    styles,
    /\.network-page \.ranking-main strong \{\s*grid-row: 1;\s*\}\s*\.network-page \.ranking-main small \{\s*grid-row: 2;/,
    'expected mobile ranking titles and paths to occupy separate grid rows',
  );
});
