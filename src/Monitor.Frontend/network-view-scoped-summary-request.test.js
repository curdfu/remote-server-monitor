import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('network view loads rankings and summaries through one dashboard request', async () => {
  const source = await readFile(viewPath, 'utf8');
  const dashboardCalls = source.match(/getNetworkDashboard\(\{[\s\S]*?\n\s*\}\)/g) ?? [];

  assert.equal(
    dashboardCalls.length,
    1,
    'expected NetworkView to issue a single combined dashboard request',
  );

  assert.match(
    dashboardCalls[0],
    /scope: filters\.scope,[\s\S]*?direction: filters\.direction/,
    'expected the dashboard request to preserve the active scope and direction',
  );

  assert.doesNotMatch(source, /getNetworkApps|getNetworkSummary/, 'expected legacy duplicate requests to be removed');
});
