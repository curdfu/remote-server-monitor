import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const viewPath = new URL('./src/views/NetworkView.vue', import.meta.url);
const modalPath = new URL('./src/components/AccessibleModal.vue', import.meta.url);

test('network presentation includes Other traffic and applied-filter lifecycle', async () => {
  const source = await readFile(viewPath, 'utf8');
  assert.match(source, /otherTotalBytes/);
  assert.match(source, /otherPercent/);
  assert.match(source, /const appliedFilters = ref<NetworkFilters \| null>\(null\)/);
  assert.match(source, /requestVersion !== dashboardRequestVersion/);
  assert.match(source, /结束时间必须晚于开始时间/);
  assert.match(source, /formatRatioPercent\(overviewTotalBytes/);
});

test('app details use a native modal component with focus return metadata', async () => {
  const [source, modal] = await Promise.all([
    readFile(viewPath, 'utf8'),
    readFile(modalPath, 'utf8')
  ]);
  assert.match(source, /<AccessibleModal/);
  assert.match(source, /:return-focus="dialogTrigger"/);
  assert.match(modal, /dialog\.showModal\(\)/);
  assert.match(modal, /@cancel="handleCancel"/);
  assert.match(modal, /target\?\.isConnected/);
});
