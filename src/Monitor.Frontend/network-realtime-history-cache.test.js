import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const aggregatorInterfacePath = new URL('../Monitor.Network/Abstractions/INetworkAggregator.cs', import.meta.url);
const aggregatorPath = new URL('../Monitor.Network/Services/TrafficAggregator.cs', import.meta.url);
const endpointPath = new URL('../Monitor.WebApi/Endpoints/NetworkEndpoints.cs', import.meta.url);
const apiPath = new URL('./src/services/api.ts', import.meta.url);
const appPath = new URL('./src/App.vue', import.meta.url);
const networkViewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const observatoryStylesPath = new URL('./src/styles/observatory.css', import.meta.url);

test('network realtime history reuses the existing aggregation cadence without a global subscription', async () => {
  const [interfaceSource, aggregatorSource, endpointSource, apiSource, appSource, viewSource] = await Promise.all([
    readFile(aggregatorInterfacePath, 'utf8'),
    readFile(aggregatorPath, 'utf8'),
    readFile(endpointPath, 'utf8'),
    readFile(apiPath, 'utf8'),
    readFile(appPath, 'utf8'),
    readFile(networkViewPath, 'utf8'),
  ]);

  assert.match(
    interfaceSource,
    /IReadOnlyList<NetworkRealtimeSnapshot> GetRecentRealtimeSnapshots\(\);/,
    'expected the existing network aggregator to expose its in-memory history',
  );
  assert.match(
    aggregatorSource,
    /RealtimeHistoryRetention = TimeSpan\.FromMinutes\(2\)/,
    'expected a fixed two-minute in-memory retention window',
  );
  assert.match(
    aggregatorSource,
    /_realtimeHistory\.Enqueue\(snapshot\);/,
    'expected existing realtime snapshots to populate the history without another collector',
  );
  assert.match(
    endpointSource,
    /MapGet\("\/api\/network\/realtime\/history"/,
    'expected an on-demand history endpoint',
  );
  assert.match(
    apiSource,
    /getNetworkRealtimeHistory\(\)[\s\S]*?request<NetworkRealtimeDto\[]>\('\/api\/network\/realtime\/history'\)/,
    'expected a typed frontend history request',
  );
  assert.match(
    viewSource,
    /void loadRealtimeHistory\(\);/,
    'expected the network page to hydrate its chart on entry',
  );
  assert.doesNotMatch(
    appSource,
    /subscribeNetworkRealtime/,
    'expected the app shell not to keep network pushes active while the page is closed',
  );
});

test('dashboard core metric cards use equal-width responsive columns', async () => {
  const source = await readFile(observatoryStylesPath, 'utf8');

  assert.match(
    source,
    /\.dashboard-core-metrics-panel \.dashboard-hero-grid \{[\s\S]*?grid-template-columns: repeat\(3, minmax\(0, 1fr\)\);/,
    'expected desktop metric cards to use three equal-width columns',
  );
  assert.match(
    source,
    /@media \(min-width: 2200px\) \{[\s\S]*?\.dashboard-core-metrics-panel \.dashboard-hero-grid \{[\s\S]*?grid-template-columns: repeat\(6, minmax\(0, 1fr\)\);/,
    'expected ultra-wide metric cards to use six equal-width columns',
  );
  assert.doesNotMatch(
    source,
    /grid-template-columns: minmax\(300px, 1\.12fr\) repeat\(3,/,
    'expected the removed uptime card special width to be gone',
  );
});
