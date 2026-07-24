import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const apiPath = new URL('./src/services/api.ts', import.meta.url);
const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const stylesPath = new URL('./src/styles.css', import.meta.url);

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
    /@click="selectApp\(item\)"/,
    'expected ranking rows to select an app on click',
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
