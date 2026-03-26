import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles.css', import.meta.url);

test('network page analytics cards stretch to the full content width on mobile', async () => {
  const source = await readFile(stylesPath, 'utf8');
  const mobileSectionMatch = source.match(/@media \(max-width: 720px\) \{([\s\S]*?)\n\}/);

  assert.ok(mobileSectionMatch, 'expected mobile media query for network page styles');

  const mobileSection = mobileSectionMatch[1];
  const analyticsContainerIndex = mobileSection.indexOf('.network-page > .network-section-analytics {');
  const analyticsCardsIndex = mobileSection.indexOf('.network-page .network-section-ranking,');

  assert.match(
    mobileSection,
    /\.network-page > \.network-section-analytics \{[^}]*align-items: stretch;/,
    'expected the mobile analytics container to stretch its children to the page content width',
  );

  assert.match(
    mobileSection,
    /\.network-page \.network-section-column-ranking \{[^}]*order: 1;/,
    'expected the ranking column to move ahead of the summary column on mobile',
  );

  assert.match(
    mobileSection,
    /\.network-page \.network-section-column-analytics \{[^}]*order: 2;/,
    'expected the upload and WAN\/LAN summary column to follow the ranking column on mobile',
  );

  assert.match(
    mobileSection,
    /\.network-page \.network-section-column \{[^}]*width: 100%;/,
    'expected the stacked analytics column to use the full available width on mobile',
  );

  assert.match(
    mobileSection,
    /\.network-page \.network-section-ranking,[\s\S]*?\.network-page \.network-section-upload-download,[\s\S]*?\.network-page \.network-section-wan-lan \{[^}]*width: 100%;[^}]*max-width: 100%;[^}]*align-self: stretch;/,
    'expected ranking, upload/download, and WAN/LAN panels to all stretch to the full page content width on mobile',
  );

  assert.ok(analyticsContainerIndex >= 0, 'expected mobile analytics container rule to exist');
  assert.ok(analyticsCardsIndex > analyticsContainerIndex, 'expected mobile card width overrides to come after the container rule');
});
