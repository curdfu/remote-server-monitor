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
    /autoRefreshTimer = window\.setTimeout\(\(\) => \{[\s\S]*?const source = pendingReloadSource;[\s\S]*?pendingReloadSource = 'filter';[\s\S]*?void loadApps\(source\);[\s\S]*?\}, 250\);/,
    'expected filter-triggered reloads to mark their loading source as filter-driven',
  );

  assert.match(
    source,
    /function refreshApps\(\) \{[\s\S]*?pendingReloadSource = 'manual';[\s\S]*?applyPreset\(presetHours\);[\s\S]*?void loadApps\('manual'\);[\s\S]*?\}/,
    'expected manual refreshes to keep using a manual loading source',
  );

  assert.match(
    source,
    /v-for="placeholder in rankingSkeletonRows"/,
    'expected ranking skeleton rows to render from a dedicated placeholder list',
  );

  assert.match(
    source,
    /<li v-if="!topRanking\.length" class="muted">当前还没有可展示的排行数据。<\/li>/,
    'expected empty state to remain after loading completes',
  );
});
