// @vitest-environment node
import { type ChildProcess, spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it } from 'vitest';

const SIDECAR = fileURLToPath(new URL('../sidecar/server.mjs', import.meta.url));
const ENTRY = fileURLToPath(new URL('./fixtures/server-entry.mjs', import.meta.url));
const TOKEN = 'test-token';

type Sidecar = { process: ChildProcess; port: number; output: string[]; errors: string[] };
const running: ChildProcess[] = [];

afterEach(() => {
  for (const child of running.splice(0)) child.kill();
});

function start(env: Record<string, string> = {}): Promise<Sidecar> {
  const child = spawn(process.execPath, [SIDECAR], {
    env: { ...process.env, NETJES_MODE: 'production', NETJES_TOKEN: TOKEN, NETJES_SERVER_ENTRY: ENTRY, ...env },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  running.push(child);
  const output: string[] = [];
  const errors: string[] = [];
  child.stderr.setEncoding('utf8').on('data', (chunk: string) => errors.push(chunk));
  return new Promise((resolve, reject) => {
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => {
      for (const line of chunk.split('\n').filter(Boolean)) {
        output.push(line);
        if (line.startsWith('NETJES_READY ')) resolve({ process: child, port: JSON.parse(line.slice(12)).port, output, errors });
      }
    });
    child.on('exit', (code) => reject(new Error(`sidecar exited with ${code}`)));
  });
}

function resolveRequest(sidecar: Sidecar, body: Record<string, unknown>) {
  return fetch(`http://127.0.0.1:${sidecar.port}/resolve`, {
    method: 'POST',
    headers: { 'X-Netjes-Token': TOKEN, 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

function renderRequest(
  sidecar: Sidecar,
  page: string,
  init: { token?: string; signal?: AbortSignal; waitForAll?: boolean; renderId?: string; headers?: Record<string, string> } = {},
) {
  return fetch(`http://127.0.0.1:${sidecar.port}/render`, {
    method: 'POST',
    headers: {
      'X-Netjes-Token': init.token ?? TOKEN,
      'Content-Type': 'application/json',
      ...(init.waitForAll ? { 'X-Netjes-Wait-All': '1' } : {}),
      ...(init.renderId ? { 'X-Netjes-Render-Id': init.renderId } : {}),
      ...init.headers,
    },
    body: JSON.stringify({ page }),
    signal: init.signal,
  });
}

async function eventually(check: () => boolean, timeoutMs = 2000) {
  const until = Date.now() + timeoutMs;
  while (!check()) {
    if (Date.now() > until) throw new Error('condition not met in time');
    await new Promise((r) => setTimeout(r, 20));
  }
}

describe('sidecar deferred values', () => {
  it('hands values posted to /resolve to the render with that id', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'deferred', { renderId: 'r1' });
    const resolved = await resolveRequest(sidecar, { renderId: 'r1', id: 'd0', value: { likes: 3 } });

    expect(resolved.status).toBe(204);
    expect(await response.text()).toBe('<html>shell {"likes":3}</html>');
  });

  it('keeps values that arrive before the render asks for them', async () => {
    const sidecar = await start();

    await resolveRequest(sidecar, { renderId: 'early', id: 'd0', value: 'ready' });
    const response = await renderRequest(sidecar, 'deferred', { renderId: 'early' });

    expect(await response.text()).toBe('<html>shell "ready"</html>');
  });

  it('rejects the value when .NET reports an error', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'deferred', { renderId: 'r2' });
    await resolveRequest(sidecar, { renderId: 'r2', id: 'd0', error: 'comments down' });

    expect(await response.text()).toBe('<html>shell error: comments down</html>');
  });

  it('refuses /resolve without the token', async () => {
    const sidecar = await start();

    const response = await fetch(`http://127.0.0.1:${sidecar.port}/resolve`, { method: 'POST', body: '{}' });

    expect(response.status).toBe(403);
  });
});

describe('sidecar streaming', () => {
  it('sends the shell as soon as it is ready and the rest as it arrives', async () => {
    const sidecar = await start();
    const startedAt = Date.now();

    const response = await renderRequest(sidecar, 'stream');
    const reader = response.body!.getReader();
    const first = new TextDecoder().decode((await reader.read()).value);
    const firstAt = Date.now() - startedAt;
    let rest = '';
    for (let chunk = await reader.read(); !chunk.done; chunk = await reader.read()) rest += new TextDecoder().decode(chunk.value);

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toContain('text/html');
    expect(first).toBe('<html>shell waitForAll=false');
    expect(firstAt).toBeLessThan(140);
    expect(rest).toBe(' rest</html>');
  });

  it('asks the entry to wait for everything when told to', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'stream', { waitForAll: true });

    expect(await response.text()).toBe('<html>shell waitForAll=true rest</html>');
  });

  it('gives crawlers (wait for everything) the stream timeout, not the shell timeout', async () => {
    const sidecar = await start({ NETJES_RENDER_TIMEOUT_MS: '100', NETJES_STREAM_TIMEOUT_MS: '5000' });

    const crawler = await renderRequest(sidecar, 'slow-all', { waitForAll: true });
    const browser = await renderRequest(sidecar, 'slow-all');

    expect(crawler.status).toBe(200);
    expect(await crawler.text()).toBe('<html>all ready waitForAll=true</html>');
    expect(browser.status).toBe(500);
  });

  it('answers 500 with the error when the shell fails', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'stream-fail');

    expect(response.status).toBe(500);
    expect(((await response.json()) as { error: { message: string } }).error.message).toBe('shell failed');
  });

  it('stops streaming when the caller goes away', async () => {
    const sidecar = await start();
    const controller = new AbortController();

    const response = await renderRequest(sidecar, 'stream', { signal: controller.signal });
    await response.body!.getReader().read();
    controller.abort();

    await eventually(() => sidecar.output.includes('STREAM_ABORTED'));
  });
});

describe('sidecar', () => {
  it('renders with the server entry', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'app/page.tsx');

    expect(response.status).toBe(200);
    expect(await response.text()).toBe('<html>app/page.tsx</html>');
  });

  it('refuses requests without the token', async () => {
    const sidecar = await start();

    expect((await renderRequest(sidecar, 'x', { token: 'wrong' })).status).toBe(403);
    expect((await renderRequest(sidecar, 'x', { token: '' })).status).toBe(403);
  });

  it('reports render errors as JSON', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'fail');

    expect(response.status).toBe(500);
    expect((await response.json()).error.message).toBe('boom');
  });

  it('reports render errors as plain text, without terminal colours, line breaks kept', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'coloured-fail');

    const { error } = (await response.json()) as { error: { message: string; stack: string } };
    const plain = [
      'Transform failed with 1 error:',
      '',
      '[PARSE_ERROR] Expected `>` but found `}`',
      '   ╭─[ app/about/page.tsx:3:1 ]',
      '   │',
      ' 3 │ }',
      '   │ ┬  ',
      '   │ ╰── `>` expected',
      '───╯',
      '',
    ].join('\n');
    expect(error.message).toBe(plain);
    expect(error.stack).toContain(plain);
    expect(error.stack).not.toContain('\u001b');
    await eventually(() => sidecar.errors.join('').includes('[PARSE_ERROR] Expected'));
    expect(sidecar.errors.join('')).not.toContain('\u001b');
  });

  it('reports render errors without any other terminal escape sequences', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'escapes-fail');

    const { error } = (await response.json()) as { error: { message: string; stack: string } };
    expect(error.message).toBe('bold, a link, copied');
    expect(error.stack).not.toMatch(/[\u001b\u009b]/);
  });

  it('fails at once for a page the build does not have: only development builds the page module map again', async () => {
    const sidecar = await start();

    const response = await renderRequest(sidecar, 'missing-module');

    expect(response.status).toBe(500);
    expect(((await response.json()) as { error: { message: string } }).error.message).toBe('netjes: no module for app/missing/page.tsx in this build');
    expect(await (await renderRequest(sidecar, 'missing-module-renders')).text()).toBe('<html>1</html>');
  });

  it('logs render errors with the trace they belong to', async () => {
    const sidecar = await start();
    const traceparent = '00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01';

    await renderRequest(sidecar, 'fail', { headers: { traceparent } });

    await eventually(() => sidecar.errors.join('').includes(`traceparent=${traceparent}`));
  });

  it('keeps the token away from page code', async () => {
    const sidecar = await start();

    expect(await (await renderRequest(sidecar, 'env')).text()).toBe('<html>no token</html>');
  });

  it('survives a promise rejection nobody handles', async () => {
    const sidecar = await start();

    await renderRequest(sidecar, 'stray-rejection');
    await new Promise((r) => setTimeout(r, 50));

    expect(await (await renderRequest(sidecar, 'after')).text()).toBe('<html>after</html>');
  });

  it('aborts the render when the caller disconnects', async () => {
    const sidecar = await start();
    const caller = new AbortController();

    const pending = renderRequest(sidecar, 'hang', { signal: caller.signal }).catch(() => undefined);
    await new Promise((r) => setTimeout(r, 100));
    caller.abort();
    await pending;

    await eventually(() => sidecar.output.includes('RENDER_ABORTED'));
  });

  it('aborts a render that outlives the timeout', async () => {
    const sidecar = await start({ NETJES_RENDER_TIMEOUT_MS: '150' });

    const response = await renderRequest(sidecar, 'hang');

    expect(response.status).toBe(500);
    expect((await response.json()).error.message).toMatch(/timed out/);
    expect(sidecar.output).toContain('RENDER_ABORTED');
  });
});

describe('sidecar token', () => {
  it.each([['unset', undefined], ['empty', '']])('refuses to start with the token %s', async (_label, value) => {
    const env: Record<string, string | undefined> = { ...process.env, NETJES_MODE: 'production', NETJES_SERVER_ENTRY: ENTRY };
    if (value === undefined) delete env.NETJES_TOKEN;
    else env.NETJES_TOKEN = value;
    const child = spawn(process.execPath, [SIDECAR], { env: env as NodeJS.ProcessEnv, stdio: ['pipe', 'pipe', 'pipe'] });
    running.push(child);
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
    child.stderr.setEncoding('utf8').on('data', (chunk: string) => (stderr += chunk));

    const code = await new Promise<number | null>((resolve) => child.on('exit', resolve));

    expect(code).not.toBe(0);
    expect(stderr).toContain('NETJES_TOKEN is required');
    expect(stdout).not.toContain('NETJES_READY');
  });
});
