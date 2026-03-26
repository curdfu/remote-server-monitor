import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('upload/download summary keeps current scope while ignoring direction', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /getNetworkSummary\(\{[\s\S]*?scope: filters\.scope,[\s\S]*?direction: 'total'/,
    'expected upload/download summary request to use the current scope and total direction',
  );
});
