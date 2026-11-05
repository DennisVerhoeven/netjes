import { act } from '@testing-library/react';
import { type Root, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Document } from '../src/document.tsx';
import type { DocumentInfo } from '../src/payload.ts';

const info: DocumentInfo = {
  lang: 'en',
  assets: { stylesheets: ['/assets/app.css'], modulePreloads: [], scripts: ['/assets/entry.js'] },
  errorPage: null,
};

const page = (
  <Document info={info} dataJson="{}">
    <main>Hi</main>
  </Document>
);

let root: Root | undefined;

/** The document as the server sent it: React's output for the page, parsed into the test's document. */
beforeEach(() => {
  document.open();
  document.write(renderToString(page));
  document.close();
});

afterEach(async () => {
  await act(async () => root?.unmount());
  root = undefined;
  vi.restoreAllMocks();
});

/** Hydrates the document the way the client entry does, and returns what React reported. */
async function hydrate() {
  const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
  const recoverable = vi.fn();
  await act(async () => {
    root = hydrateRoot(document, page, { onRecoverableError: recoverable });
  });
  return { errors, recoverable };
}

describe('the document', () => {
  it('hydrates without a hydration error when a script set attributes on <html> and <body> before React ran', async () => {
    // A theme script in the head, a browser extension or a translator: all of them run before hydration.
    document.documentElement.setAttribute('data-theme', 'dark');
    document.body.setAttribute('data-gr-ext-installed', '');

    const { errors, recoverable } = await hydrate();

    expect(errors).not.toHaveBeenCalled();
    expect(recoverable).not.toHaveBeenCalled();
    // React hydrates without patching the document up: what the script wrote stays.
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(document.body.hasAttribute('data-gr-ext-installed')).toBe(true);
    expect(document.querySelector('main')?.textContent).toBe('Hi');
  });

  it('hydrates a document that nothing changed without a hydration error', async () => {
    const { errors, recoverable } = await hydrate();

    expect(errors).not.toHaveBeenCalled();
    expect(recoverable).not.toHaveBeenCalled();
  });

  it('still reports an attribute that was added below <body>, to the page\'s own markup', async () => {
    document.querySelector('main')?.setAttribute('data-clock', '12:00');

    const { errors } = await hydrate();

    expect(errors).toHaveBeenCalled();
  });

  it('renders <html> with its language and <body> with nothing, as the client-render shell writes them', () => {
    const markup = renderToString(page);

    expect(markup).toMatch(/^<html lang="en"><head>/);
    expect(markup).toContain('</head><body><script id="__NETJES_DATA__"');
  });
});
