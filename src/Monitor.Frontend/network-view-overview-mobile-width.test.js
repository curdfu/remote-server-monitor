import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles/network.css', import.meta.url);
const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const baseStylesPath = new URL('./src/styles.css', import.meta.url);

test('network summary panels stack before ranking and stretch on mobile and tablet', async () => {
  const [styles, view, base] = await Promise.all([readFile(stylesPath, 'utf8'), readFile(viewPath, 'utf8'), readFile(baseStylesPath, 'utf8')]);
  assert.match(styles, /\.network-page \.network-section-analytics\s*\{[^}]*display:\s*grid;[^}]*min-width:\s*0;/);
  assert.match(styles, /\.network-page \.network-section-column-analytics\s*\{[^}]*display:\s*grid;[^}]*grid-template-columns:\s*minmax\(0,\s*3fr\)\s+minmax\(0,\s*4fr\);[^}]*min-width:\s*0;/);
  assert.match(styles, /\.network-page \.network-section-column-ranking\s*\{[^}]*min-width:\s*0;/);
  assert.match(styles, /\.network-page \.panel-header\s*\{[^}]*display:\s*flex;[^}]*flex-wrap:\s*wrap;[^}]*min-width:\s*0;/, 'summary headers must be able to wrap on narrow screens');
  assert.match(base, /\.card\s*\{[^}]*min-width:\s*0;/, 'shared cards must shrink below their content width');
  assert.match(base, /body\s*\{[^}]*min-width:\s*0;/, 'body must fit the viewport after the vertical scrollbar takes space');
  for (const section of ['filters', 'upload-download', 'wan-lan', 'ranking']) {
    assert.match(view, new RegExp(`<article[^>]*class="(?=[^"]*\\bcard\\b)[^"]*network-section-${section}\\b`), `${section} must retain the shared card width constraint`);
  }
  const tabletStart = styles.indexOf('@media (max-width: 1120px)');
  assert.ok(tabletStart >= 0);
  const tablet = styles.slice(tabletStart);
  assert.match(tablet, /\.network-page \.network-section-column-analytics\s*\{[^}]*grid-template-columns:\s*1fr;/);
  const summary = view.indexOf('network-section-column-analytics');
  const ranking = view.indexOf('network-section-column-ranking');
  assert.ok(summary >= 0 && ranking > summary, 'summary markup should precede the ranking at narrow widths');
  const mobileStart = styles.indexOf('@media (max-width: 720px)');
  assert.ok(mobileStart >= 0);
  const mobile = styles.slice(mobileStart);
  assert.match(mobile, /\.network-page \.network-section-filters,[\s\S]*?\.network-page \.network-section-ranking\s*\{[^}]*padding:\s*16px;/);
  assert.match(mobile, /\.network-page \.preset-row \.chip-button,[\s\S]*?\{[^}]*min-height:\s*44px;/);
  assert.match(mobile, /\.network-page \.ranking-ignore-button,[\s\S]*?\{[^}]*width:\s*44px;[^}]*height:\s*44px;/);
});
