import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles.css', import.meta.url);

test('sidebar brand description remains fully visible on desktop', async () => {
  const source = await readFile(stylesPath, 'utf8');
  const descriptionStyles = source.match(/\.sidebar-description \{([\s\S]*?)\}/)?.[1] ?? '';

  assert.match(descriptionStyles, /white-space: normal;/, 'expected the brand description to wrap naturally');
  assert.match(descriptionStyles, /overflow-wrap: anywhere;/, 'expected long text to stay inside the sidebar');
  assert.doesNotMatch(descriptionStyles, /text-overflow: ellipsis;/, 'expected the full description without ellipsis');
});
