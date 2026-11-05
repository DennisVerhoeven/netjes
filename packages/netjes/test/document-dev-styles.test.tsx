import { act } from '@testing-library/react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Document } from '../src/document.tsx';
import type { DocumentInfo } from '../src/payload.ts';

// In development the sidecar inlines the CSS a page imports as <style data-vite-dev-id> elements, so that the page is
// styled before its scripts run. Vite's client takes each element over as the CSS module with its id loads, which the
// client entry waits for before it hydrates, and sets the element's text to the module's.
const info: DocumentInfo = {
  lang: 'en',
  assets: { stylesheets: [], modulePreloads: [], scripts: [] },
  errorPage: null,
  devStyles: [{ id: '/app/styles.css', css: 'h1{color:red}' }],
};

const page = (
  <Document info={info} dataJson="{}">
    <h1>Hi</h1>
  </Document>
);

let root: Root | undefined;

afterEach(async () => {
  await act(async () => root?.unmount());
  root = undefined;
  vi.restoreAllMocks();
});

describe('development styles', () => {
  it('hydrates a development style whose text Vite replaced before React ran, without a hydration error', async () => {
    document.open();
    document.write(renderToString(page));
    document.close();
    const style = document.querySelector('style[data-vite-dev-id="/app/styles.css"]');
    // Vite's client, with its own build of the file: an edit between the render and the import changes it.
    if (style) style.textContent = 'h1{color:blue}';

    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    const recoverable = vi.fn();
    await act(async () => {
      root = hydrateRoot(document, page, { onRecoverableError: recoverable });
    });

    expect(errors).not.toHaveBeenCalled();
    expect(recoverable).not.toHaveBeenCalled();
    // React keeps the element, which Vite's client holds on to for later updates, and leaves its text as Vite set it.
    expect(document.querySelector('style[data-vite-dev-id="/app/styles.css"]')).toBe(style);
    expect(style?.textContent).toBe('h1{color:blue}');
  });
});
