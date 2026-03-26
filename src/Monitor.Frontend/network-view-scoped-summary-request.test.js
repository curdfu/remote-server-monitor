import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('network view keeps the scoped summary request alongside current layout requests', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /getNetworkSummary\(\{[\s\S]*?scope: filters\.scope,[\s\S]*?direction: filters\.direction/,
    'expected loadApps to keep a scoped summary request using the current scope and direction',
  );
});
