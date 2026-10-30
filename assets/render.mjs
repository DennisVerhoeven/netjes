#!/usr/bin/env node
// @ts-check
// Renders the PNGs in this folder from their SVG sources, with the Chromium that Playwright installs for the examples'
// end-to-end tests (`npx playwright install chromium`), and copies the mark into the app template as its favicon. The
// PNGs are committed, so packing needs no browser; run this from the repository root after changing an SVG, and commit
// what it writes:
//
//   node assets/render.mjs
import { chromium } from '@playwright/test';
import { copyFile, readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

/** Each PNG, its SVG source and its size in pixels. */
const RENDERS = [
  { svg: 'netjes.svg', png: 'icon.png', width: 128, height: 128 }, // the NuGet packages' icon
  { svg: 'social-preview.svg', png: 'social-preview.png', width: 1280, height: 640 }, // GitHub's social preview
];

/** @param {string} file a path relative to this folder */
const here = (file) => fileURLToPath(new URL(file, import.meta.url));

// A new app's favicon is the mark as it is, until the app puts its own in public/.
await copyFile(here('netjes.svg'), here('../packages/netjes/template/public/favicon.svg'));
console.log('packages/netjes/template/public/favicon.svg  from netjes.svg');

const browser = await chromium.launch();
try {
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const { svg, png, width, height } of RENDERS) {
    const source = (await readFile(here(svg))).toString('base64');
    await page.setViewportSize({ width, height });
    await page.setContent(
      `<body style="margin:0"><img src="data:image/svg+xml;base64,${source}" width="${width}" height="${height}" style="display:block"></body>`,
    );
    await page.locator('img').evaluate((img) => /** @type {HTMLImageElement} */ (img).decode());
    // Transparent where the SVG draws nothing: the mark's rounded corners.
    await page.screenshot({ path: here(png), omitBackground: true });
    console.log(`${png}  ${width}x${height}  from ${svg}`);
  }
} finally {
  await browser.close();
}
