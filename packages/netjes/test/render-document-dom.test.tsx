// netjes/testing in a DOM environment, jsdom here, as in an app's component tests. There, code that looks for
// `document` or `window` (a form's server action, the query client, the router's first URL) takes its browser path,
// so a page would not render as the sidecar renders it.
import { createHash } from 'node:crypto';
import { describe, expect, it, vi } from 'vitest';
import { createTestApp, inlineScriptHashes, renderDocument } from '../src/testing.tsx';

// netjes/testing loads the server renderer only when renderDocument runs: a component test must not load it.
vi.mock('react-dom/server', () => {
  throw new Error('react-dom/server was loaded');
});

describe('netjes/testing in a DOM environment', () => {
  it('loads no server renderer for component tests', () => {
    expect(createTestApp().navigations).toEqual([]);
  });

  it('refuses to render a document, which the sidecar renders without a DOM', async () => {
    // Vitest reads its environment annotation anywhere in a file, so the message's is split here.
    const annotation = ['@vitest', 'environment node'].join('-');

    await expect(renderDocument('app/page.tsx', { modules: { '/app/page.tsx': async () => ({ default: () => null }) } })).rejects.toThrow(
      `netjes: renderDocument renders a page as the Node sidecar does, without a DOM. Put '// ${annotation}' at the top of the test file.`,
    );
  });

  it('hashes inline scripts as in the node environment', async () => {
    expect(await inlineScriptHashes('<script>x()</script>')).toEqual([`'sha256-${createHash('sha256').update('x()').digest('base64')}'`]);
  });
});
