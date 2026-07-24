import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('upload/download summary uses the scoped totals from the dashboard response', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /totalsSummary\.value = dashboard\.totals/,
    'expected upload/download cards to use the combined response scoped totals',
  );

  assert.match(
    source,
    /getNetworkDashboard\(\{[\s\S]*?scope: filters\.scope,[\s\S]*?direction: filters\.direction/,
    'expected the combined request to carry the active scope and direction',
  );
});
