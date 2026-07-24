import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/SettingsView.vue', import.meta.url);

test('runtime profiles only change hardware and network realtime sampling intervals', async () => {
  const source = await readFile(viewPath, 'utf8');
  const profileType = source.match(/interface PerformanceProfile \{[\s\S]*?\n\}/)?.[0] ?? '';
  const profileValues = source.match(
    /const performanceProfiles:[\s\S]*?=\s*\[([\s\S]*?)\]\s*as const;/,
  )?.[1] ?? '';

  assert.match(profileType, /'hardwareSampleIntervalMs'\s*\|\s*'networkRealtimeIntervalMs'/);
  assert.doesNotMatch(profileType, /aggregateIntervalSeconds|historyRetentionDays|topNDefault/);
  assert.doesNotMatch(profileValues, /aggregateIntervalSeconds|historyRetentionDays|topNDefault/);
});
