import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const apiPath = new URL('./src/services/api.ts', import.meta.url);
const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const stylesPath = new URL('./src/styles.css', import.meta.url);
const observatoryStylesPath = new URL('./src/styles/observatory.css', import.meta.url);
const networkEndpointsPath = new URL('../Monitor.WebApi/Endpoints/NetworkEndpoints.cs', import.meta.url);
const databaseInitializerPath = new URL('../Monitor.Storage/Services/DatabaseInitializer.cs', import.meta.url);
const ignoredAppsRepositoryPath = new URL('../Monitor.Storage/Repositories/IgnoredNetworkAppRepository.cs', import.meta.url);

test('network app segments API encodes app keys in the route', async () => {
  const source = await readFile(apiPath, 'utf8');

  assert.match(
    source,
    /export function getNetworkAppSegments\(appKey: string/,
    'expected API client to expose getNetworkAppSegments',
  );

  assert.match(
    source,
    /\/api\/network\/apps\/\$\{encodeURIComponent\(appKey\)\}\/segments/,
    'expected appKey to be encoded before being placed in the URL path',
  );
});

test('network view renders clickable app traffic segment details', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /import \{[^}]*getNetworkAppSegments[^}]*getNetworkDashboard[^}]*\}/,
    'expected NetworkView to import the app segment and dashboard API clients',
  );

  assert.match(
    source,
    /const selectedApp = ref<AppTrafficSummaryDto \| null>\(null\);/,
    'expected selected app state for the detail panel',
  );

  assert.match(
    source,
    /let segmentsRequestVersion = 0;/,
    'expected request version guard state for segment loading',
  );

  assert.match(
    source,
    /class="ranking-open-button"[\s\S]*?@click="selectApp\(item\)"/,
    'expected ranking rows to expose a dedicated button for selecting an app',
  );

  assert.doesNotMatch(
    source,
    /role="button"[\s\S]*?class="ranking-ignore-button"/,
    'expected the ignore action not to be nested inside another button-like control',
  );

  assert.match(
    source,
    /requestVersion !== segmentsRequestVersion/,
    'expected stale segment responses to be ignored',
  );

  assert.match(
    source,
    /class="app-segments-panel"/,
    'expected app segment detail panel markup',
  );

  assert.match(
    source,
    /class="app-segments-overlay"/,
    'expected app segment details to render in a floating overlay',
  );

  assert.match(
    source,
    /role="dialog"/,
    'expected floating app segment panel to use dialog semantics',
  );

  assert.match(
    source,
    /class="app-segments-chart"/,
    'expected app segment details to render as a bar chart',
  );

  assert.match(
    source,
    /class="app-segment-bar"/,
    'expected each segment to render as a chart bar',
  );

  assert.match(
    source,
    /class="app-segment-tooltip"/,
    'expected segment bars to expose hover details',
  );

  assert.doesNotMatch(
    source,
    /:title="getSegmentTooltip\(segment\)"/,
    'expected segment bars not to trigger a second native browser tooltip',
  );

  assert.match(
    source,
    /getSegmentTooltipPlacementClass/,
    'expected edge-aware tooltip placement for chart bars',
  );

  assert.match(
    source,
    /formatSegmentAxisLabel/,
    'expected simplified axis labels for segment dates',
  );

  assert.match(
    source,
    /closeAppSegmentsPanel/,
    'expected the floating panel to be closable',
  );

  assert.match(
    source,
    /<AppIcon name="close" :size="16" \/>/,
    'expected the floating panel close button to use the shared close icon',
  );

  assert.doesNotMatch(
    source,
    />\s*×\s*</,
    'expected the floating panel close button not to render a raw multiplication sign',
  );

  assert.match(
    source,
    /segmentsErrorMessage/,
    'expected segment error state',
  );

  assert.match(
    source,
    /isSegmentsLoading/,
    'expected segment loading state',
  );

  assert.match(
    source,
    /当前筛选范围内没有该应用的分段流量。/,
    'expected segment empty state',
  );
});

test('network app segment tooltip rises above sibling bars while active', async () => {
  const source = await readFile(stylesPath, 'utf8');

  assert.match(
    source,
    /\.app-segment-bar-item:hover,\s*\.app-segment-bar-item:focus-within\s*\{\s*z-index:\s*4;/,
    'expected hovered or focused segment items to rise above later sibling bars',
  );
});

test('network ranking supports globally persisted ignore, undo, and restore actions', async () => {
  const [source, apiSource] = await Promise.all([
    readFile(viewPath, 'utf8'),
    readFile(apiPath, 'utf8'),
  ]);

  assert.match(
    source,
    /const ignoredAppsStorageKey = 'monitor\.network\.ignored-apps\.v1';/,
    'expected the old browser-local key to remain available only for one-time migration',
  );

  assert.match(
    source,
    /items\.value\s*\.filter\(\(item\) => !ignoredAppKeys\.value\.has\(item\.appKey\)\)\s*\.slice\(0, filters\.topN\)/,
    'expected ignored apps to be removed before applying the visible TopN limit',
  );

  assert.match(
    source,
    /topN: filters\.topN,/,
    'expected the server to own ranking over-fetch based on the global ignore list',
  );

  assert.match(
    source,
    /ignoredApps\.value = dashboard\.ignoredApps \?\? \[\];/,
    'expected every dashboard refresh to synchronize the global ignore snapshot',
  );

  assert.match(
    source,
    /@click\.stop="ignoreApp\(item\)"/,
    'expected ignore controls not to open the app detail panel',
  );

  assert.match(
    source,
    /class="ranking-ignored-panel"/,
    'expected ignored apps to be shown in a separate panel',
  );

  assert.match(
    source,
    /@click="restoreIgnoredApp\(entry\.record\.appKey\)"/,
    'expected every ignored app to be restorable',
  );

  assert.match(
    source,
    /async function ignoreApp\(item: AppTrafficSummaryDto\)[\s\S]*?await ignoreNetworkApp\(record\)/,
    'expected ignore changes to be confirmed by the server before updating the page',
  );

  assert.match(
    source,
    /async function restoreIgnoredApp\(appKey: string\)[\s\S]*?await restoreNetworkApp\(appKey\)/,
    'expected restore changes to be confirmed by the server before updating the page',
  );

  assert.match(
    source,
    /function undoLastIgnore\(\)/,
    'expected the latest ignore action to support undo',
  );

  assert.match(
    source,
    /window\.localStorage\.removeItem\(ignoredAppsStorageKey\);/,
    'expected successful legacy migration to clear browser-local ignored apps',
  );

  assert.match(
    apiSource,
    /request<IgnoredNetworkAppDto\[\]>\('\/api\/network\/ignored-apps',\s*\{\s*method: 'PUT'/,
    'expected the API client to persist ignore changes globally',
  );

  assert.match(
    apiSource,
    /\/api\/network\/ignored-apps\/\$\{encodeURIComponent\(appKey\)\}[^]*method: 'DELETE'/,
    'expected the API client to restore globally ignored apps',
  );
});

test('global network ignore storage is cached and isolated from traffic history', async () => {
  const [endpointSource, databaseSource, repositorySource] = await Promise.all([
    readFile(networkEndpointsPath, 'utf8'),
    readFile(databaseInitializerPath, 'utf8'),
    readFile(ignoredAppsRepositoryPath, 'utf8'),
  ]);

  assert.match(
    databaseSource,
    /CREATE TABLE IF NOT EXISTS ignored_network_apps/,
    'expected a dedicated table that does not alter traffic history tables',
  );

  assert.match(
    repositorySource,
    /private IgnoredNetworkAppDto\[\]\? _snapshot;/,
    'expected the global ignore list to use an in-memory snapshot',
  );

  assert.match(
    endpointSource,
    /requestedLimit \+ ignoredApps\.Count/,
    'expected the server to over-fetch enough candidates to refill the visible ranking',
  );

  assert.match(
    endpointSource,
    /IgnoredApps = \[\.\. ignoredApps\]/,
    'expected dashboard responses to carry the authoritative global ignore list',
  );
});

test('network ranking ignore controls remain usable on mobile', async () => {
  const source = await readFile(observatoryStylesPath, 'utf8');

  assert.match(
    source,
    /@media \(max-width: 720px\)[\s\S]*?\.network-page \.ranking-ignore-button\s*\{[^}]*position:\s*absolute;[^}]*min-width:\s*44px;[^}]*min-height:\s*44px;/,
    'expected a fixed 44px mobile ignore target that does not overlap text',
  );

  assert.match(
    source,
    /@media \(max-width: 720px\)[\s\S]*?\.ranking-ignored-item\s*\{[^}]*grid-template-columns:\s*minmax\(0, 1fr\);/,
    'expected ignored rows to stack into one column on mobile',
  );
});
