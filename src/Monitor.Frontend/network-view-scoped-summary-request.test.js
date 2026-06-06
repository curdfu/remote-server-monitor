import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('network view does not issue an unused scoped summary request', async () => {
  const source = await readFile(viewPath, 'utf8');
  const summaryCalls = source.match(/getNetworkSummary\(\{[\s\S]*?\n\s*\}\)/g) ?? [];

  assert.ok(
    summaryCalls.length > 0,
    'expected NetworkView to issue network summary requests',
  );

  assert.ok(
    summaryCalls.every((call) => !/scope: filters\.scope,[\s\S]*?direction: filters\.direction/.test(call)),
    'expected loadApps to avoid requesting a scoped summary that is not consumed by the UI',
  );
});
