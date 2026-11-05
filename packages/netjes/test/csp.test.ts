// @vitest-environment node
// CSP_SCRIPT_HASHES against the documents the sidecar renders. tests/Netjes.Tests/Endpoints/CspTests.cs checks
// NetjesContentSecurityPolicy.ScriptHashes against the shell .NET writes when no sidecar is ready.
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import type { Readable } from 'node:stream';
import { createElement as h } from 'react';
import { describe, expect, it } from 'vitest';
import { CSP_SCRIPT_HASHES, Form, SubmitButton, serverAction, useAction } from '../src/index.ts';
import { createServerRenderer } from '../src/server-render.tsx';

const save = serverAction('notes.save');

/** A form for a server action, as the guides write it. */
function ServerActionForm() {
  const [, formAction] = useAction(save);
  return h(Form, { action: formAction }, h('button', { type: 'submit' }, 'Save'));
}

/** The same server action on a submit button instead. */
function ServerActionButton() {
  const [, formAction] = useAction(save);
  return h(Form, { method: 'post', action: '/notes' }, h(SubmitButton, { formAction }, 'Save'));
}

/** A form whose action is a function of the page's own. */
function FunctionForm() {
  return h(Form, { action: async () => {} }, h('button', { type: 'submit' }, 'Go'));
}

const renderer = createServerRenderer({
  '/app/page.tsx': async () => ({ default: () => null }),
  '/app/server-action-form.tsx': async () => ({ default: ServerActionForm }),
  '/app/server-action-button.tsx': async () => ({ default: ServerActionButton }),
  '/app/function-form.tsx': async () => ({ default: FunctionForm }),
});

/** A page with no layouts, props or deferred values, and the one script a build always has: the client entry. */
function payload(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({
    kind: 'page',
    route: '/',
    page: 'app/page.tsx',
    segments: [],
    props: {},
    params: {},
    shared: null,
    status: 200,
    url: '/',
    version: 'v1',
    ...overrides,
    document: { lang: 'en', assets: { stylesheets: [], modulePreloads: [], scripts: ['/assets/client-entry.js'] }, errorPage: null },
  });
}

/** What a URL that no page matches gets: Netjes's own not-found page. */
const notFound = { route: null, page: null, status: 404, url: '/missing' };

async function text(stream: Readable): Promise<string> {
  let html = '';
  for await (const chunk of stream) html += String(chunk);
  return html;
}

/**
 * The text of each inline script in a document that a browser runs, and that a policy's script-src governs: every
 * script element without `src`, except the `application/json` payload, which is data.
 */
function inlineScripts(html: string): string[] {
  return [...html.matchAll(/<script([^>]*)>([\s\S]*?)<\/script>/g)]
    .filter(([, attributes = '']) => !attributes.includes(' src=') && !attributes.includes(' type="application/json"'))
    .map(([, , script = '']) => script);
}

/** The source that allows a script by its text, as a browser hashes it: SHA-256 of its UTF-8 bytes. */
const hashSource = (script: string) => `'sha256-${createHash('sha256').update(script).digest('base64')}'`;

describe('CSP_SCRIPT_HASHES', () => {
  it.each([
    ['a complete document', () => renderer.render(payload())],
    ['a streamed document', async () => text(await renderer.renderToStream(payload()))],
    ['a not-found document', () => renderer.render(payload(notFound))],
  ])('lists the hash of every inline script in %s', async (_, render) => {
    const html = await render();

    expect(html).toContain('<script type="module" src="/assets/client-entry.js"');
    const hashes = [...new Set(inlineScripts(html).map(hashSource))].sort();
    expect(hashes).toEqual(CSP_SCRIPT_HASHES.split(' ').sort());
  });

  it('is the value of NetjesContentSecurityPolicy.ScriptHashes in C#', async () => {
    const source = await readFile(new URL('../../../src/Netjes/NetjesContentSecurityPolicy.cs', import.meta.url), 'utf8');

    expect(/ScriptHashes \{ get; \} =\s*"([^"]*)"/.exec(source)?.[1]).toBe(CSP_SCRIPT_HASHES);
  });

  it('is the value the security guide gives for this version', async () => {
    const guide = await readFile(new URL('../../../docs/guides/security.md', import.meta.url), 'utf8');

    expect(guide).toContain(`\`${CSP_SCRIPT_HASHES}\``);
  });
});

describe('a form action', () => {
  it('from useAction in <Form action> adds no inline script, since the server renders the form with its URL', async () => {
    const html = await renderer.render(payload({ page: 'app/server-action-form.tsx' }));

    expect(html).toContain('<form action="/_netjes/actions/notes.save" method="post">');
    expect([...new Set(inlineScripts(html).map(hashSource))].sort()).toEqual(CSP_SCRIPT_HASHES.split(' ').sort());
  });

  // React's script replays a submission made before the page hydrates. Its text follows the React version, so
  // CSP_SCRIPT_HASHES leaves it out, and the security guide tells apps under a hash-based policy what to do about it.
  it.each([
    ['another function in <Form action>', 'app/function-form.tsx'],
    ['a server action in <SubmitButton formAction>', 'app/server-action-button.tsx'],
  ])('that is %s makes React add an inline script that CSP_SCRIPT_HASHES does not list', async (_, page) => {
    const html = await renderer.render(payload({ page }));

    const listed = CSP_SCRIPT_HASHES.split(' ');
    const hashes = [...new Set(inlineScripts(html).map(hashSource))];
    expect(hashes.filter((hash) => !listed.includes(hash))).toHaveLength(1);
  });
});
