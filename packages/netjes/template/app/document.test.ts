// @vitest-environment node
// The home page's whole document, as the server sends it, from the props its C# loader returns
// (Features/Home/HomePage.cs). renderDocument renders without a DOM, as the server does, hence the node environment.
import { CSP_SCRIPT_HASHES } from 'netjes';
import { inlineScriptHashes, renderDocument } from 'netjes/testing';
import { describe, expect, it } from 'vitest';

const home = () =>
  renderDocument('app/page.tsx', {
    props: { message: 'Hello from C#', renderedAt: '2026-11-05T12:00:00Z' },
    shared: { siteName: '__TITLE__' },
  });

describe('the home page', () => {
  it("is a document with the loader's heading, inside the root layout", async () => {
    const html = await home();

    expect(html.startsWith('<!DOCTYPE html><html lang="en">')).toBe(true);
    expect(html).toMatch(/<h1[^>]*>Hello from C#<\/h1>/);
    expect(html).toContain('<nav aria-label="Main"');
  });

  it('runs no inline script but the one whose hash Netjes lists for a policy without a nonce', async () => {
    // NetjesContentSecurityPolicy.ScriptHashes in C# holds the same hashes.
    expect(await inlineScriptHashes(await home())).toEqual(CSP_SCRIPT_HASHES.split(' '));
  });
});
