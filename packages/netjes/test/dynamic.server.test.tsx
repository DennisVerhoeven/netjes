// @vitest-environment node
import { Writable } from 'node:stream';
import type { ReactNode } from 'react';
import { renderToPipeableStream } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { clientOnly, dynamic } from '../src/dynamic.tsx';

/** Renders like the sidecar does: waits for every Suspense boundary. */
function renderComplete(element: ReactNode): Promise<string> {
  return new Promise((resolve, reject) => {
    let html = '';
    const sink = new Writable({
      write(chunk, _encoding, callback) {
        html += String(chunk);
        callback();
      },
      final(callback) {
        resolve(html);
        callback();
      },
    });
    const stream = renderToPipeableStream(element, { onAllReady: () => stream.pipe(sink), onShellError: reject });
  });
}

const Chart = ({ points }: { points: number }) => <p>{`chart with ${points} points`}</p>;
const Loading = () => <p>loading chart…</p>;

describe('dynamic on the server', () => {
  it('renders the component by default', async () => {
    const LazyChart = dynamic(async () => ({ default: Chart }), { loading: Loading });

    const html = await renderComplete(<LazyChart points={2} />);

    expect(html).toContain('chart with 2 points');
  });

  it('renders only the loading component with ssr: false', async () => {
    const LazyChart = dynamic(async () => ({ default: Chart }), { ssr: false, loading: Loading });

    const html = await renderComplete(<LazyChart points={2} />);

    expect(html).toContain('loading chart…');
    expect(html).not.toContain('chart with');
  });

  it('renders the fallback of a clientOnly component', async () => {
    const Clock = clientOnly(() => <p>client clock</p>, { fallback: <p>…</p> });

    const html = await renderComplete(<Clock />);

    expect(html).toBe('<p>…</p>');
  });
});
