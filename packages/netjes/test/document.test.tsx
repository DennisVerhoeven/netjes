import { act } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { type Root, createRoot, hydrateRoot } from 'react-dom/client';
import { renderToString } from 'react-dom/server';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Document } from '../src/document.tsx';
import type { DocumentInfo } from '../src/payload.ts';

const info: DocumentInfo = {
  lang: 'en',
  assets: { stylesheets: ['/assets/app.css'], modulePreloads: [], scripts: ['/assets/entry.js'] },
  errorPage: null,
};

const documentWith = (content: ReactNode) => (
  <Document info={info} dataJson="{}">
    {content}
  </Document>
);

const page = documentWith(<main>Hi</main>);

let root: Root | undefined;

/** The document as the server sent it: React's output for `tree`, parsed into the test's document. */
function serve(tree: ReactElement) {
  document.open();
  document.write(renderToString(tree));
  document.close();
}

beforeEach(() => serve(page));

afterEach(async () => {
  await act(async () => root?.unmount());
  root = undefined;
  vi.restoreAllMocks();
});

/** Hydrates the document with `tree` the way the client entry does, and returns what React reported. */
async function hydrate(tree: ReactElement = page) {
  const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
  const recoverable = vi.fn();
  await act(async () => {
    root = hydrateRoot(document, tree, { onRecoverableError: recoverable });
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

  it('still reports an attribute added to the page\'s own markup, below <body>', async () => {
    document.querySelector('main')?.setAttribute('data-clock', '12:00');

    const { errors } = await hydrate();

    expect(errors).toHaveBeenCalled();
  });

  it('does not compare the text that sits directly in <body> either, outside any element', async () => {
    // The prop on <body> also covers the text nodes that are its own children, and React's production build skips them
    // too. A layout or a page puts text there when it renders it at its top level, outside any element or Suspense
    // boundary: nothing that Netjes renders between <body> and the app is an element.
    serve(documentWith('Rendered on the server'));

    const { errors, recoverable } = await hydrate(documentWith('Rendered in the browser'));

    expect(recoverable).not.toHaveBeenCalled();
    expect(errors).not.toHaveBeenCalled();
    // Nothing renders it again either: the page keeps the server's text.
    expect(document.body.textContent).toContain('Rendered on the server');
    expect(document.body.textContent).not.toContain('Rendered in the browser');
  });

  it('still reports text that differs inside an element in the body', async () => {
    serve(documentWith(<p>Rendered on the server</p>));

    const { recoverable } = await hydrate(documentWith(<p>Rendered in the browser</p>));

    expect(recoverable).toHaveBeenCalled();
    expect(recoverable.mock.calls[0]?.[0]).toMatchObject({ message: expect.stringContaining("rendered text didn't match") });
  });

  it('renders <html> with its language and <body> with nothing, as the client-render shell writes them', () => {
    const markup = renderToString(page);

    expect(markup).toMatch(/^<html lang="en"><head>/);
    expect(markup).toContain('</head><body><script id="__NETJES_DATA__"');
  });

  it('does not keep what a script set on <html> and <body> when the browser renders the document itself', async () => {
    document.documentElement.setAttribute('data-theme', 'dark');
    document.body.setAttribute('data-gr-ext-installed', '');

    // No sidecar was ready, so the client entry renders the document instead of hydrating it. React sets the attributes
    // of both elements itself then.
    await act(async () => {
      root = createRoot(document);
      root.render(page);
    });

    expect(document.documentElement.getAttributeNames()).toEqual(['lang']);
    expect(document.body.getAttributeNames()).toEqual([]);
  });
});
