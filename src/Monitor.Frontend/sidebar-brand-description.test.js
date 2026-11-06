import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles/observatory.css', import.meta.url);

test('global runtime replaces the brand description and remains visible on mobile', async () => {
  const [styles, app] = await Promise.all([
    readFile(stylesPath, 'utf8'), readFile(new URL('./src/App.vue', import.meta.url), 'utf8')
  ]);
  assert.doesNotMatch(app, /硬件状态与网络流量|sidebar-description/);
  assert.match(app, /<h1>服务器远程监控<\/h1>\s*<dl class="sidebar-runtime"/);
  assert.equal((app.match(/<dt>/g) ?? []).length, 2);
  assert.match(app, /<dt>开机时间<\/dt>/);
  assert.match(app, /<dt>运行时间<\/dt>/);
  assert.match(styles, /\.sidebar-runtime dd\s*\{[^}]*white-space:\s*normal;[^}]*overflow-wrap:\s*anywhere;/);
  const mobile = styles.slice(styles.indexOf('@media (max-width: 720px)'));
  assert.match(mobile, /\.sidebar-runtime\s*\{[^}]*flex-direction:\s*column/);
  assert.doesNotMatch(mobile, /\.sidebar-runtime[^{}]*\{[^}]*display:\s*none/);
});
