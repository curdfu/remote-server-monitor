import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const stylesPath = new URL('./src/styles.css', import.meta.url);
const observatoryStylesPath = new URL('./src/styles/observatory.css', import.meta.url);

test('network page analytics cards stretch to the full content width on mobile and tablet portrait', async () => {
  const source = await readFile(stylesPath, 'utf8');
  const observatoryStyles = await readFile(observatoryStylesPath, 'utf8');

  assert.match(
    source,
    /@media \(max-width: 1100px\) \{[\s\S]*?\.network-page > \.network-section-analytics \{[^}]*align-items: stretch;/,
    'expected the mobile analytics container to stretch its children to the page content width',
  );

  assert.match(
    source,
    /@media \(max-width: 1100px\) \{[\s\S]*?\.network-page \.network-section-column-ranking \{[^}]*order: 1;/,
    'expected the ranking column to move ahead of the summary column on mobile and tablet portrait',
  );

  assert.match(
    source,
    /@media \(max-width: 1100px\) \{[\s\S]*?\.network-page \.network-section-column-analytics \{[^}]*order: 2;/,
    'expected the upload and WAN\/LAN summary column to follow the ranking column on mobile and tablet portrait',
  );

  assert.match(
    source,
    /@media \(max-width: 1100px\) \{[\s\S]*?\.network-page \.network-section-column \{[^}]*width: 100%;/,
    'expected the stacked analytics column to use the full available width on mobile and tablet portrait',
  );

  assert.match(
    source,
    /@media \(max-width: 1100px\) \{[\s\S]*?\.network-page \.network-section-ranking,[\s\S]*?\.network-page \.network-section-upload-download,[\s\S]*?\.network-page \.network-section-wan-lan \{[^}]*width: 100%;[^}]*max-width: 100%;[^}]*align-self: stretch;/,
    'expected ranking, upload/download, and WAN/LAN panels to all stretch to the full page content width on mobile and tablet portrait',
  );

  assert.match(
    observatoryStyles,
    /@media \(max-width: 1120px\) \{[\s\S]*?\.network-page \.network-section-column-analytics \{[^}]*grid-template-columns: 1fr;/,
    'expected the summary column to use one explicit grid column below the tablet breakpoint',
  );

  assert.match(
    observatoryStyles,
    /@media \(max-width: 1120px\) \{[\s\S]*?\.network-page \.network-section-upload-download,[\s\S]*?\.network-page \.network-section-wan-lan \{[^}]*grid-column: 1;[^}]*grid-row: auto;[^}]*height: auto;/,
    'expected summary cards to reset their desktop grid placement below the tablet breakpoint',
  );

  assert.match(
    observatoryStyles,
    /@media \(max-width: 720px\) \{[\s\S]*?\.network-page \.filters-card \{[^}]*position: static;[^}]*backdrop-filter: none;/,
    'expected the full filter card to leave the sticky layer on phones',
  );

  assert.match(
    observatoryStyles,
    /@media \(max-width: 720px\) \{[\s\S]*?\.network-page \.preset-row \.chip-button \{[^}]*min-height: 44px;/,
    'expected preset controls to use a phone-friendly touch height',
  );

  assert.match(
    observatoryStyles,
    /@media \(max-width: 720px\) \{[\s\S]*?\.network-page \.network-section-upload-download \.panel-header,[\s\S]*?\.network-page \.network-section-wan-lan \.panel-header \{[^}]*flex-direction: column;[^}]*align-items: stretch;/,
    'expected summary panel headers to stack without squeezing their titles on phones',
  );
});
