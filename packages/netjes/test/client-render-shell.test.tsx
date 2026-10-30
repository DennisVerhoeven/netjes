// @vitest-environment node
// The document .NET writes while no sidecar is ready (ClientRenderShell.cs) holds .NET's copy of the head Document
// renders. tests/Netjes.Tests/Endpoints/ClientRenderFallbackTests.cs pins the shell for a fixture page in
// fixtures/client-render/shell.html; here Document renders that page from the same payload, as a sidecar would, and
// the two heads must hold the same elements. A change to Document's head fails here until the shell follows.
import { readFileSync } from 'node:fs';
import type { Readable } from 'node:stream';
import type { ReactNode } from 'react';
import { describe, expect, it } from 'vitest';
import { createServerRenderer } from '../src/server-render.tsx';

const shell = readFileSync(new URL('./fixtures/client-render/shell.html', import.meta.url), 'utf8');

/** The fixture page renders nothing into the head: what is there comes from Document and the C# metadata. */
const renderer = createServerRenderer({
  '/app/layout.tsx': async () => ({ default: ({ children }: { children: ReactNode }) => <main>{children}</main> }),
  '/app/error.tsx': async () => ({ default: () => <p>Something went wrong</p> }),
  '/app/posts/[id]/page.tsx': async () => ({ default: ({ id }: { id: string }) => <h1>{`Post ${id}`}</h1> }),
});

function payloadOf(html: string): string {
  const match = /<script id="__NETJES_DATA__"[^>]*>([^<]*)<\/script>/.exec(html);
  if (!match?.[1]) throw new Error('no payload in the shell');
  return match[1];
}

async function text(stream: Readable): Promise<string> {
  let html = '';
  for await (const chunk of stream) html += String(chunk);
  return html;
}

const ENTITY = /&(#x[0-9a-f]+|#[0-9]+|amp|lt|gt|quot|apos);/gi;
const NAMED: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };

/** Character references as both sides write them (React escapes fewer characters than .NET's HtmlEncoder). */
function decode(value: string): string {
  return value.replace(ENTITY, (_, ref: string) =>
    ref.startsWith('#')
      ? String.fromCodePoint(ref[1] === 'x' || ref[1] === 'X' ? Number.parseInt(ref.slice(2), 16) : Number(ref.slice(1)))
      : (NAMED[ref.toLowerCase()] ?? ''),
  );
}

const VOID = new Set(['meta', 'link', 'base']);

/**
 * The elements of an HTML fragment, each written one way: its name, its attributes by name with their values decoded
 * (`async` and `async=""` alike, `charSet` and `charset` alike), and the text of a script or title. Neither side writes
 * a `>` inside an attribute value.
 */
function elements(html: string): string[] {
  const result: string[] = [];
  const tag = /<([a-zA-Z]+)([^>]*?)\/?>/g;
  for (let match = tag.exec(html); match; match = tag.exec(html)) {
    const name = (match[1] ?? '').toLowerCase();
    const attributes = [...(match[2] ?? '').matchAll(/([^\s="]+)(?:="([^"]*)")?/g)]
      .map(([, key = '', value = '']) => ` ${key.toLowerCase()}=${JSON.stringify(decode(value))}`)
      .sort();
    let content = '';
    if (!VOID.has(name)) {
      const end = html.indexOf(`</${name}>`, tag.lastIndex);
      if (end < 0) throw new Error(`<${name}> is not closed in ${html}`);
      const raw = html.slice(tag.lastIndex, end);
      content = name === 'script' ? raw : decode(raw);
      tag.lastIndex = end + name.length + 3;
    }
    result.push(`<${name}${attributes.join('')}>${content}`);
  }
  return result;
}

function headOf(html: string): string {
  const start = html.indexOf('<head>');
  const end = html.indexOf('</head>');
  if (start < 0 || end < start) throw new Error(`no head in ${html}`);
  return html.slice(start + '<head>'.length, end);
}

/** `<html>` itself (its lang), every element in the head, and the payload's script element without the payload. */
function documentElements(html: string): string[] {
  const root = /<html[^>]*>/.exec(html)?.[0] ?? '';
  const data = /<script id="__NETJES_DATA__"[^>]*>/.exec(html)?.[0] ?? '';
  return [...elements(`${root}</html>`), ...elements(headOf(html)).sort(), ...elements(`${data}</script>`)];
}

describe('the client-render shell', () => {
  it('has the head Document renders for the same page', async () => {
    // What a sidecar would have been sent: the same payload, not marked for the browser to render.
    const marked = payloadOf(shell);
    const payload = marked.replace(',"clientRender":true', '');
    expect(payload).not.toBe(marked);

    const rendered = await text(await renderer.renderToStream(payload));

    expect(rendered).toContain('<h1>Post 7</h1>');
    // The fixture has every kind of element the shell writes, and a nonce on each that takes one.
    const expected = documentElements(shell);
    const kinds = ['rel="stylesheet"', 'rel="modulepreload"', 'type="module"', 'property="csp-nonce"', '__netjes_d', '<title>', 'name="description"', 'name="robots"'];
    for (const kind of kinds) expect(expected.some((e) => e.includes(kind)), kind).toBe(true);
    for (const e of expected.filter((e) => /^<(script|link)/.test(e))) expect(e).toContain(' nonce="fixture+nonce/A=="');
    // Order aside: React moves async scripts ahead of the rest, so only the shell keeps Document's source order.
    expect(documentElements(rendered)).toEqual(expected);
  });
});
