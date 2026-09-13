import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

async function loadGeometry() {
  const source = await readFile(new URL('./src/utils/trendGeometry.ts', import.meta.url), 'utf8');
  const transpiled = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(transpiled).toString('base64')}`);
}

test('trend geometry uses actual time spacing and keeps gaps separate', async () => {
  const { getTrendDomain, projectTrendPoint, splitTrendSegments } = await loadGeometry();
  const points = [
    { time: '2026-01-01T00:00:00Z', value: 20 },
    { time: '2026-01-01T00:00:20Z', value: null },
    { time: '2026-01-01T00:01:00Z', value: 40 }
  ];
  const segments = splitTrendSegments(points, 30_000);
  assert.equal(segments.length, 2);
  assert.equal(segments[0].points.length, 1);
  assert.equal(segments[1].points.length, 1);

  const domain = getTrendDomain('percent', points);
  const window = { startMs: Date.parse('2026-01-01T00:00:00Z'), endMs: Date.parse('2026-01-01T00:01:00Z') };
  const start = projectTrendPoint(segments[0].points[0], window, domain, 360, 120);
  const end = projectTrendPoint(segments[1].points[0], window, domain, 360, 120);
  assert.equal(start.x, 0);
  assert.equal(end.x, 360);
});

test('temperature domain includes negative values and downsampling preserves spikes', async () => {
  const { downsampleTrendSegments, getTrendDomain } = await loadGeometry();
  const domain = getTrendDomain('temperature', [
    { time: '2026-01-01T00:00:00Z', value: -12 },
    { time: '2026-01-01T00:00:01Z', value: 120 }
  ]);
  assert.ok(domain.min <= -20);
  assert.ok(domain.max >= 120);

  const points = Array.from({ length: 100 }, (_, index) => ({
    timestamp: index,
    time: new Date(index).toISOString(),
    value: index === 50 ? 99 : 1
  }));
  const [segment] = downsampleTrendSegments([{ points }], 10);
  assert.ok(segment.points.some((point) => point.value === 99));
  assert.equal(segment.points[0].timestamp, 0);
  assert.equal(segment.points.at(-1)?.timestamp, 99);
});
