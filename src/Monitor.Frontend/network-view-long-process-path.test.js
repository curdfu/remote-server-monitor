import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles.css', import.meta.url);

test('network ranking and app details wrap long process paths inside panels', async () => {
  const source = await readFile(stylesPath, 'utf8');

  assert.match(
    source,
    /\.ranking-main strong\s*\{[^}]*min-width:\s*0;[^}]*overflow-wrap:\s*anywhere;/,
    'expected long process names in the ranking panel to wrap before they can overflow',
  );

  assert.match(
    source,
    /\.ranking-main small\s*\{[^}]*min-width:\s*0;[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;/,
    'expected long executable paths in the ranking panel to wrap before they can overflow',
  );

  assert.match(
    source,
    /\.app-segments-title > div\s*\{[^}]*min-width:\s*0;/,
    'expected the app details title text column to be allowed to shrink inside the dialog',
  );

  assert.match(
    source,
    /\.app-segments-title \.panel-subtitle\s*\{[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;/,
    'expected long executable paths in the details panel to wrap before they can overflow',
  );
});
