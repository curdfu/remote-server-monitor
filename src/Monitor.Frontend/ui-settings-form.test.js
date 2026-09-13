import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

async function loadValidation() {
  const source = await readFile(new URL('./src/utils/settingsValidation.ts', import.meta.url), 'utf8');
  const transpiled = ts.transpileModule(source, {
    compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 }
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(transpiled).toString('base64')}`);
}

const validSettings = {
  httpPort: 5188,
  hardwareSampleIntervalMs: 1000,
  networkProcessingIntervalMs: 1000,
  aggregateIntervalSeconds: 10,
  historyRetentionDays: 30,
  topNDefault: 10
};

test('settings validation accepts boundaries and rejects non-finite values', async () => {
  const { validateSettings } = await loadValidation();
  assert.deepEqual(validateSettings(validSettings), {});
  assert.equal(Object.hasOwn(validateSettings({ ...validSettings, httpPort: 0 }), 'httpPort'), true);
  assert.equal(Object.hasOwn(validateSettings({ ...validSettings, topNDefault: 100 }), 'topNDefault'), false);
  assert.equal(Object.hasOwn(validateSettings({ ...validSettings, aggregateIntervalSeconds: Number.NaN }), 'aggregateIntervalSeconds'), true);
  assert.equal(Object.hasOwn(validateSettings({ ...validSettings, historyRetentionDays: Number.POSITIVE_INFINITY }), 'historyRetentionDays'), true);
});

test('settings page exposes one save action and protects refresh from discarding drafts', async () => {
  const source = await readFile(new URL('./src/views/SettingsView.vue', import.meta.url), 'utf8');
  assert.match(source, /\{\{ isSaving \? '保存中\.\.\.' : '保存' \}\}/);
  assert.match(source, /@click="refreshSettings\(\)"/);
  assert.match(source, /if \(options\.confirmDiscard && !confirmDiscardChanges\(\)\)/);
  assert.match(source, /isSaving\.value\) \{[\s\S]*?errorMessage\.value = '正在保存/);
});
