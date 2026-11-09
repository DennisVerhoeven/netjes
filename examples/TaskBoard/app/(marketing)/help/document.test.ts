// @vitest-environment node
// The help center page package's pages as the server renders them in TaskBoard, which mounts the package at
// /(marketing)/help (Program.cs): renderDocument merges the package's files into app/ by the server's rule.
import { CSP_SCRIPT_HASHES } from 'netjes';
import { inlineScriptHashes, renderDocument } from 'netjes/testing';
import { describe, expect, it } from 'vitest';

const help = '@netjes-examples/help-center';
const mounts = { [help]: '/(marketing)/help' };
const shared = { user: null };
const articles = [
  { slug: 'getting-started', title: 'Getting started', summary: 'Where to look first.' },
  { slug: 'privacy', title: 'Privacy', summary: 'What we keep about you.' },
];
const center = { [`${help}/app/(center)/layout.tsx`]: { articles } };

const article = () =>
  renderDocument(`${help}/app/(center)/articles/[slug]/page.tsx`, {
    mounts,
    shared,
    url: '/help/articles/privacy',
    params: { slug: 'privacy' },
    layouts: center,
    props: {
      article: { slug: 'privacy', title: 'Privacy', summary: 'What we keep about you.', body: ['We keep what you give us.'] },
      votes: { helpful: 2, notHelpful: 0 },
      feedbackCount: 0,
      feedbackSent: false,
      errors: null,
      values: null,
    },
  });

describe('the help center in TaskBoard', () => {
  it('renders an article inside the marketing chrome and the package\'s layouts, linking below the mount', async () => {
    const html = await article();

    expect(html).toContain('<h1 class="mt-0 text-help-ink">Privacy</h1>');
    // TaskBoard's marketing header, then the package's band with its mounted links.
    expect(html).toMatch(/<nav aria-label="Account".*<nav aria-label="Help center"/s);
    expect(html).toMatch(/<a [^>]*href="\/help\/contact"[^>]*>Contact<\/a>/);
    expect(html).toMatch(/<a [^>]*href="\/help\/articles\/privacy"[^>]*aria-current="page"[^>]*>Privacy<\/a>/);
  });

  // The package draws no shell, so its pages get TaskBoard's main landmark from the marketing layout: one, around them.
  it("puts the package's pages in the marketing layout's main landmark", async () => {
    const html = await article();

    expect(html.match(/<main\b/g)).toHaveLength(1);
    expect(html).toMatch(/<main\b[^>]*>.*<nav aria-label="Help center">.*<h1 class="mt-0 text-help-ink">Privacy<\/h1>.*<\/main>/s);
  });

  // A package's pages render under the app's Content-Security-Policy: a hash-based one allows Netjes's own inline
  // scripts by CSP_SCRIPT_HASHES and nothing else, so the package adds none of its own.
  it("has no inline script in an article's document but Netjes's own, for a hash-based policy", async () => {
    expect(await inlineScriptHashes(await article())).toEqual(CSP_SCRIPT_HASHES.split(' '));
  });

  it("renders TaskBoard's contact page in place of the package's, with the package's props and layout", async () => {
    const html = await renderDocument('app/(marketing)/help/contact/page.tsx', {
      mounts,
      shared,
      url: '/help/contact',
      props: { email: 'help@example.com', hours: 'Monday to Friday' },
    });

    expect(html).toContain('Talk to the people who build TaskBoard');
    expect(html).toContain('href="mailto:help@example.com"');
    expect(html).toContain('<nav aria-label="Help center">');
    expect(await inlineScriptHashes(html)).toEqual(CSP_SCRIPT_HASHES.split(' '));
  });

  it("renders TaskBoard's shortcuts page inside the package's (center) layout", async () => {
    const html = await renderDocument('app/(marketing)/help/(center)/shortcuts/page.tsx', {
      mounts,
      shared,
      url: '/help/shortcuts',
      layouts: center,
    });

    expect(html).toContain('<h1 class="m-0 text-2xl font-semibold">Shortcuts</h1>');
    expect(html).toContain('<nav aria-label="Articles in the help center">');
    expect(html).toMatch(/<a [^>]*href="\/help\/articles\/getting-started"[^>]*>Getting started<\/a>/);
  });
});
