import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

async function loadTypeScriptModule(relativePath) {
  const source = await readFile(new URL(relativePath, import.meta.url), 'utf8');
  const transpiled = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(transpiled).toString('base64')}`);
}

test('telemetry freshness uses a 15 second floor and a 180 second fallback', async () => {
  const { getStaleThresholdMs, inspectSampleTime, shouldAcceptSnapshot } = await loadTypeScriptModule('./src/utils/telemetryState.ts');

  assert.equal(getStaleThresholdMs(500), 15_000);
  assert.equal(getStaleThresholdMs(1000), 15_000);
  assert.equal(getStaleThresholdMs(60_000), 180_000);
  assert.equal(getStaleThresholdMs(null), 180_000);
  assert.equal(inspectSampleTime(new Date(1_000).toISOString(), 15_999, 15_000).state, 'fresh');
  assert.equal(inspectSampleTime(new Date(1_000).toISOString(), 16_000, 15_000).state, 'stale');
  assert.equal(inspectSampleTime(null, 16_000, 15_000).state, 'waiting');
  assert.equal(inspectSampleTime('invalid', 16_000, 15_000).state, 'invalid-time');
  assert.equal(shouldAcceptSnapshot('2026-01-01T00:00:20Z', '2026-01-01T00:00:10Z'), false);
  assert.equal(shouldAcceptSnapshot('2026-01-01T00:00:10Z', '2026-01-01T00:00:20Z'), true);
});

test('resource pressure keeps null, NaN and Infinity unknown', async () => {
  const { getResourcePressure } = await loadTypeScriptModule('./src/utils/telemetryState.ts');
  assert.equal(getResourcePressure(null, 70, 90), 'unknown');
  assert.equal(getResourcePressure(Number.NaN, 70, 90), 'unknown');
  assert.equal(getResourcePressure(Number.POSITIVE_INFINITY, 70, 90), 'unknown');
  assert.equal(getResourcePressure(70, 70, 90), 'warning');
  assert.equal(getResourcePressure(90, 70, 90), 'danger');
});
