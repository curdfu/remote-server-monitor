import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

async function loadRuntime() {
  const source = await readFile(new URL('./src/utils/systemRuntime.ts', import.meta.url), 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}

test('boot time is based on the server sample and handles invalid values', async () => {
  const { formatBootTime } = await loadRuntime();
  const time = '2026-11-06T08:00:00Z';
  assert.equal(formatBootTime(time, 3600), new Date('2026-11-06T07:00:00Z').toLocaleString('zh-CN', { hour12: false }));
  for (const value of [undefined, null, -1, NaN, Infinity, Number.MAX_VALUE]) {
    assert.equal(formatBootTime(time, value), '--');
  }
  assert.equal(formatBootTime('invalid', 0), '--');
  assert.equal(formatBootTime(undefined, 0), '--');
});

test('uptime uses complete days hours and minutes without assuming fresh samples', async () => {
  const { formatUptime } = await loadRuntime();
  assert.equal(formatUptime(0), '0小时 0分');
  assert.equal(formatUptime(59), '0小时 0分');
  assert.equal(formatUptime(3661), '1小时 1分');
  assert.equal(formatUptime(8 * 86400 + 19 * 3600 + 23 * 60), '8天 19小时 23分');
  for (const value of [undefined, null, -1, NaN, Infinity]) assert.equal(formatUptime(value), '--');
});

test('runtime snapshots reject stale or invalid data and accept a server reboot', async () => {
  const { isRuntimeSnapshotNewer } = await loadRuntime();
  const current = { sampleTime: '2026-11-06T08:00:00Z', uptimeSeconds: 86400 };
  assert.equal(isRuntimeSnapshotNewer(null, current), true);
  assert.equal(isRuntimeSnapshotNewer(current, current), false);
  assert.equal(isRuntimeSnapshotNewer(current, { ...current, sampleTime: '2026-11-06T07:59:00Z' }), false);
  assert.equal(isRuntimeSnapshotNewer(current, { sampleTime: '2026-11-06T08:01:00Z', uptimeSeconds: 10 }), true);
  assert.equal(isRuntimeSnapshotNewer(current, { sampleTime: 'invalid', uptimeSeconds: 10 }), false);
  assert.equal(isRuntimeSnapshotNewer(current, { sampleTime: '2026-11-06T08:01:00Z', uptimeSeconds: -1 }), false);
});

test('all pages remove their banner while preserving refresh and save actions', async () => {
  const [dashboard, network, settings] = await Promise.all(['Dashboard', 'Network', 'Settings'].map((name) =>
    readFile(new URL(`./src/views/${name}View.vue`, import.meta.url), 'utf8')));
  for (const source of [dashboard, network, settings]) assert.doesNotMatch(source, /PageHeader|page-header/);
  assert.match(dashboard, /dashboard-core-metrics-header[\s\S]*?@click="refreshDashboard"/);
  assert.match(network, /network-filter-heading[\s\S]*?@click="refreshApps"/);
  assert.match(settings, /settings-profile-tools[\s\S]*?@click="refreshSettings\(\)"/);
  assert.equal((settings.match(/type="submit"/g) ?? []).length, 1);
  assert.match(settings, /type="submit" form="settings-form"/);
});

test('global runtime shares the existing polling lifecycle without a hardware subscription or extra clock', async () => {
  const app = await readFile(new URL('./src/App.vue', import.meta.url), 'utf8');
  assert.match(app, /getHardwareRealtime\(controller\.signal\)/);
  assert.match(app, /isRuntimeSnapshotNewer\(runtimeSnapshot\.value, snapshot\)/);
  assert.match(app, /healthTimer = window\.setTimeout\(pollHealth, 30_000\)/);
  assert.match(app, /document\.hidden/);
  assert.match(app, /healthRequest\?\.abort\(\)/);
  assert.doesNotMatch(app, /subscribeHardwareRealtime|subscribeProcessCpuRealtime|useTelemetryClock|setInterval/);
});
