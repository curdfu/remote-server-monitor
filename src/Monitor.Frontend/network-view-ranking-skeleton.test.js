import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);

test('ranking panel shows skeleton rows for initial and filter-triggered loading only', async () => {
  const source = await readFile(viewPath, 'utf8');

  assert.match(
    source,
    /v-if="showRankingSkeleton"[\s\S]*?class="ranking-list ranking-list-skeleton"/,
    'expected ranking skeleton block to use a dedicated visibility condition',
  );

  assert.match(
    source,
    /const showRankingSkeleton = computed\(\(\) => isLoading\.value && \(!topRanking\.value\.length \|\| loadingSource\.value === 'filter'\)\);/,
    'expected ranking skeleton visibility to include filter-triggered reloads while preserving manual refreshes with existing data',
  );

  assert.match(
    source,
    /const loadingSource = ref<'filter' \| 'manual'>\('filter'\);/,
    'expected loading source state to default to filter-style loading for the initial render',
  );

  assert.match(
    source,
    /\(\) => reloadController\.filtersChanged\(\)/,
    'expected filter changes to schedule a filter-driven reload',
  );

  assert.match(
    source,
    /function refreshApps\(\) \{[\s\S]*?reloadController\.refresh\(\);[\s\S]*?\}/,
    'expected manual refreshes to use the controller rather than a filter reload',
  );

  assert.match(
    source,
    /v-for="placeholder in rankingSkeletonRows"/,
    'expected ranking skeleton rows to render from a dedicated placeholder list',
  );

  assert.match(
    source,
    /<li v-if="!topRanking\.length" class="network-empty-state">[\s\S]*?当前条件没有可展示的排行数据[\s\S]*?调整筛选[\s\S]*?<\/li>/,
    'expected empty state and recovery actions to remain after loading completes',
  );
});
