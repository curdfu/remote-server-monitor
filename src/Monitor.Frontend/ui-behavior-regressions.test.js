import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

async function loadModule(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8');
  const output = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(output).toString('base64')}`);
}

function fakeScheduler() {
  let next = 0;
  const tasks = new Map();
  return {
    schedule(callback, delay) { const id = ++next; tasks.set(id, { callback, delay }); return id; },
    cancel(id) { tasks.delete(id); },
    get delays() { return [...tasks.values()].map((task) => task.delay); },
    runNext() { const [id, task] = tasks.entries().next().value ?? []; if (task) { tasks.delete(id); task.callback(); } }
  };
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test('hottest disk is independent of input order and ignores unknown temperatures', async () => {
  const { findHottestDisk } = await loadModule('./src/utils/hardwareHistory.ts');
  const disks = [{ name: 'unknown', temperatureC: null }, { name: 'hot', temperatureC: 51 }, { name: 'cool', temperatureC: 24 }];
  assert.equal(findHottestDisk(disks)?.name, 'hot');
  assert.equal(findHottestDisk([...disks].reverse())?.name, 'hot');
  assert.equal(findHottestDisk([{ name: 'missing', temperatureC: null }]), null);
});

test('history backfill retains realtime samples arriving during a request and de-duplicates timestamps', async () => {
  const { mergeHistorySamples } = await loadModule('./src/utils/hardwareHistory.ts');
  const now = Date.parse('2026-01-01T01:00:30Z');
  const sample = (time, value) => ({ sampleTime: time, value });
  const live = sample('2026-01-01T01:00:20Z', 'live');
  const merged = mergeHistorySamples([live], [
    sample('2025-12-31T23:59:00Z', 'old'),
    sample('2026-01-01T00:40:00Z', 'history'),
    sample('2026-01-01T01:00:20.000Z', 'stale duplicate')
  ], now);
  assert.deepEqual(merged.map((item) => item.value), ['history', 'live']);
});

test('sparse trend gaps use the configured sampling bound, not their observed median', async () => {
  const { getTrendGapMs, splitTrendSegments } = await loadModule('./src/utils/trendGeometry.ts');
  const points = [0, 120, 240].map((seconds) => ({ time: new Date(seconds * 1000).toISOString(), value: 1 }));
  assert.equal(splitTrendSegments(points, getTrendGapMs(1000)).length, 3);
  assert.equal(getTrendGapMs(null), 180_000);
  assert.equal(getTrendGapMs(60_000), 180_000);
});

test('segment axis is duration-based and a filtered-out direction is not counted', async () => {
  const { formatSegmentAxisTime, isDirectionIncluded } = await loadModule('./src/utils/networkPresentation.ts');
  const start = '2026-01-01T12:00:00Z';
  assert.match(formatSegmentAxisTime(start, start, '2026-01-01T13:00:00Z'), /^\d{2}:\d{2}$/);
  assert.match(formatSegmentAxisTime(start, start, '2026-01-04T12:00:00Z'), /^\d{2}-\d{2}$/);
  assert.equal(isDirectionIncluded('upload', 'download'), false);
  assert.equal(isDirectionIncluded('download', 'upload'), false);
  assert.equal(isDirectionIncluded('total', 'download'), true);
});

test('manual preset refresh makes exactly one request whether values change or not', async () => {
  const { createNetworkReloadController } = await loadModule('./src/utils/networkReload.ts');
  const scheduler = fakeScheduler();
  const calls = [];
  let changed = false;
  let controller;
  controller = createNetworkReloadController({
    ...scheduler,
    getPreset: () => 24,
    advancePreset: () => { if (changed) controller.filtersChanged(); },
    load: (source) => calls.push(source)
  });
  controller.filtersChanged();
  controller.refresh();
  assert.deepEqual(calls, ['manual']);
  assert.deepEqual(scheduler.delays, []);
  changed = true;
  controller.refresh();
  assert.deepEqual(calls, ['manual', 'manual']);
  assert.deepEqual(scheduler.delays, []);
  controller.filtersChanged();
  assert.deepEqual(scheduler.delays, [250]);
  scheduler.runNext();
  assert.deepEqual(calls, ['manual', 'manual', 'filter']);
  controller.filtersChanged();
  controller.dispose();
  assert.deepEqual(scheduler.delays, []);
});

test('topic subscription retries failed invokes and cancels retries when listeners leave', async () => {
  const { createTopicSubscription } = await loadModule('./src/utils/topicSubscription.ts');
  const scheduler = fakeScheduler();
  const methods = [];
  let failures = 2;
  let ready = 0;
  const topic = createTopicSubscription({
    ...scheduler,
    isConnected: () => true,
    invoke: async (method) => { methods.push(method); if (failures-- > 0) throw new Error('temporary'); },
    onSubscribed: () => ready++
  });
  topic.setDesired(true);
  await flush();
  assert.deepEqual(scheduler.delays, [1000]);
  assert.equal(ready, 0);
  scheduler.runNext();
  await flush();
  assert.deepEqual(scheduler.delays, [2000]);
  scheduler.runNext();
  await flush();
  assert.equal(ready, 1);
  assert.deepEqual(methods, ['SubscribeHardware', 'SubscribeHardware', 'SubscribeHardware']);
  topic.setDesired(false);
  await flush();
  assert.equal(methods.at(-1), 'UnsubscribeHardware');

  failures = 1;
  topic.setDesired(true);
  await flush();
  assert.deepEqual(scheduler.delays, [1000]);
  topic.setDesired(false);
  assert.deepEqual(scheduler.delays, []);
});

test('old connection invocation cannot mark a new generation subscribed', async () => {
  const { createTopicSubscription } = await loadModule('./src/utils/topicSubscription.ts');
  const scheduler = fakeScheduler();
  const pending = [];
  let ready = 0;
  const topic = createTopicSubscription({
    ...scheduler,
    isConnected: () => true,
    invoke: (method) => new Promise((resolve, reject) => pending.push({ method, resolve, reject })),
    onSubscribed: () => ready++
  });
  topic.setDesired(true);
  topic.reset();
  topic.sync();
  assert.equal(pending.length, 2);
  pending[0].resolve();
  await flush();
  assert.equal(ready, 0);
  topic.setDesired(false);
  pending[1].resolve();
  await flush();
  assert.equal(ready, 0);
  assert.equal(pending[2].method, 'UnsubscribeHardware');
  pending[2].resolve();
  await flush();
  assert.deepEqual(scheduler.delays, []);
});
