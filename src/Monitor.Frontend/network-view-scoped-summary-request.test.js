import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('network view does not issue an unused scoped summary request', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.doesNotMatch(
    source,
    /getNetworkSummary\(\{[\s\S]*?scope: filters\.scope,[\s\S]*?direction: filters\.direction/,
    'expected loadApps to avoid requesting a scoped summary that is not consumed by the UI',
  );
});
