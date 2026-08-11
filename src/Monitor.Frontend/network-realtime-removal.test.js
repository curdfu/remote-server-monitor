import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const sourceFiles = [
  new URL('../Monitor.Network/Abstractions/INetworkAggregator.cs', import.meta.url),
  new URL('../Monitor.Network/Services/TrafficAggregator.cs', import.meta.url),
  new URL('../Monitor.WebApi/Endpoints/NetworkEndpoints.cs', import.meta.url),
  new URL('../Monitor.WebApi/Hubs/MonitorHub.cs', import.meta.url),
  new URL('./src/services/realtime.ts', import.meta.url),
  new URL('./src/views/NetworkView.vue', import.meta.url)
];

test('network realtime speed feature is absent while historical aggregation remains', async () => {
  const sources = await Promise.all(sourceFiles.map((file) => readFile(file, 'utf8')));
  const combinedSource = sources.join('\n');

  assert.doesNotMatch(combinedSource, /NetworkRealtime|networkRealtime|network\/realtime|RealtimeSlot|实时网络速率/);
  assert.match(combinedSource, /AddToBucketCore/);
  assert.match(combinedSource, /api\/network\/dashboard/);
});
