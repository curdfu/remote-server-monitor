import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const aggregationServicePath = new URL('../Monitor.Service/HostedServices/AggregationHostedService.cs', import.meta.url);
const collectorServicePath = new URL('../Monitor.Service/HostedServices/CollectorHostedService.cs', import.meta.url);

test('network aggregation isolates persistence failures from the hosted-service loop', async () => {
  const source = await readFile(aggregationServicePath, 'utf8');

  assert.match(
    source,
    /RunAggregationCycleSafeAsync/,
    'expected the aggregation loop to execute each cycle behind an exception boundary',
  );

  assert.match(
    source,
    /catch \(Exception exception\) when \(!cancellationToken\.IsCancellationRequested\)/,
    'expected non-cancellation persistence failures to be retained instead of escaping the hosted service',
  );

  assert.match(
    source,
    /Retaining \{Count\} network traffic buckets for the next cycle/,
    'expected failed network batches to remain available for a later retry',
  );
});

test('hardware persistence also retains batches when SQLite fails unexpectedly', async () => {
  const source = await readFile(collectorServicePath, 'utf8');

  assert.match(
    source,
    /snapshotBuffer\.RequeuePendingBatch\(batch\);[\s\S]*?SQLite persistence failed unexpectedly/,
    'expected hardware batches to be requeued for every recoverable SQLite failure',
  );
});
