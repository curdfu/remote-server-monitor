import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles/network.css', import.meta.url);
const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('compact ranking resets desktop subgrid rows before stacking identity and traffic', async () => {
  const [source, view] = await Promise.all([readFile(stylesPath, 'utf8'), readFile(viewPath, 'utf8')]);
  const compactStart = source.indexOf('@media (max-width: 960px)');
  const phoneStart = source.indexOf('@media (max-width: 720px)');
  assert.ok(compactStart >= 0 && phoneStart > compactStart);
  const compact = source.slice(compactStart, phoneStart);
  const phone = source.slice(phoneStart);
  assert.match(compact, /\.network-page \.ranking-open-button\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\);[^}]*grid-template-rows:\s*auto auto;/);
  assert.match(compact, /\.network-page \.ranking-main\s*\{[^}]*grid-column:\s*1;[^}]*grid-row:\s*1;/);
  assert.match(compact, /\.network-page \.ranking-side\s*\{[^}]*grid-column:\s*1;[^}]*grid-row:\s*2;/);
  assert.match(phone, /\.network-page \.ranking-side\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\);/);
  assert.match(phone, /\.network-page \.ranking-value\s*\{[^}]*grid-column:\s*1;[^}]*grid-row:\s*1;/);
  assert.match(phone, /\.network-page \.ranking-breakdown\s*\{[^}]*grid-row:\s*2;/);
  assert.match(phone, /\.network-page \.ranking-progress\s*\{[^}]*grid-row:\s*3;/);
  assert.match(phone, /\.network-page \.ranking-skeleton-value\s*\{[^}]*grid-column:\s*1;/);
  assert.match(view, /class="ranking-value-label">当前统计<\/span>/);
});

test('network ranking and app details wrap long process paths inside panels', async () => {
  const source = await readFile(stylesPath, 'utf8');

  assert.match(
    source,
    /\.network-page \.ranking-main strong,\s*\.network-page \.ranking-main small\s*\{[^}]*min-width:\s*0;[^}]*overflow-wrap:\s*anywhere;/,
    'expected long process names in the ranking panel to wrap before they can overflow',
  );

  assert.match(
    source,
    /\.network-page \.ranking-main strong,\s*\.network-page \.ranking-main small\s*\{[^}]*min-width:\s*0;[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;/,
    'expected long executable paths in the ranking panel to wrap before they can overflow',
  );

  assert.match(
    source,
    /\.app-segments-title\s*\{[^}]*min-width:\s*0;/,
    'expected the app details title text column to be allowed to shrink inside the dialog',
  );

  assert.match(
    source,
    /\.app-segments-title \.panel-subtitle\s*\{[^}]*overflow-wrap:\s*anywhere;/,
    'expected long executable paths in the details panel to wrap before they can overflow',
  );
});
