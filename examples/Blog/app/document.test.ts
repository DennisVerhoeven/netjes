// @vitest-environment node
// A page's whole document, as the server sends it to a crawler: the about page inside the root layout, from the
// shared props Program.cs sends. renderDocument renders without a DOM, as the server does, hence the node environment.
import { CSP_SCRIPT_HASHES } from 'netjes';
import { inlineScriptHashes, renderDocument } from 'netjes/testing';
import { describe, expect, it } from 'vitest';

const shared = {
  siteName: 'Netjes Blog',
  navigation: [
    { href: '/blog', label: 'Blog' },
    { href: '/docs/introduction', label: 'Docs' },
    { href: '/guestbook', label: 'Guestbook' },
    { href: '/about', label: 'About' },
  ],
};

const about = () => renderDocument('app/about/page.tsx', { url: '/about', shared });

describe('the about page', () => {
  it('has the main navigation, which marks the page it is on', async () => {
    const html = await about();

    const navigation = /<nav aria-label="Main">(.*?)<\/nav>/s.exec(html)?.[1] ?? '';
    expect([...navigation.matchAll(/<a [^>]*>([^<]*)<\/a>/g)].map(([, label]) => label)).toEqual(['Blog', 'Docs', 'Guestbook', 'About']);
    expect(navigation).toMatch(/<a [^>]*aria-current="page"[^>]*>About<\/a>/);
    expect(html).toContain('<title>About · Netjes Blog</title>');
  });

  it('has no inline script in its document but the one Netjes lists the hash of, for a policy without a nonce', async () => {
    expect(await inlineScriptHashes(await about())).toEqual(CSP_SCRIPT_HASHES.split(' '));
  });
});
