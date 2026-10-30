import { act, render, screen } from '@testing-library/react';
import { hydrateRoot } from 'react-dom/client';
import { describe, expect, it, vi } from 'vitest';
import { clientOnly, dynamic } from '../src/dynamic.tsx';

function Chart({ points }: { points: number }) {
  return <p>{`chart with ${points} points`}</p>;
}

const Loading = () => <p>loading chart…</p>;

describe('dynamic', () => {
  it('shows the loading component until the module arrives', async () => {
    let resolve: (module: { default: typeof Chart }) => void = () => {};
    const LazyChart = dynamic(() => new Promise<{ default: typeof Chart }>((r) => (resolve = r)), { loading: Loading });

    render(<LazyChart points={3} />);
    expect(screen.getByText('loading chart…')).toBeDefined();
    await act(async () => resolve({ default: Chart }));

    expect(screen.getByText('chart with 3 points')).toBeDefined();
  });

  it('accepts a module that exports the component itself', async () => {
    const LazyChart = dynamic(async () => Chart);

    render(<LazyChart points={1} />);

    expect(await screen.findByText('chart with 1 points')).toBeDefined();
  });

  it('with ssr: false, hydrates the server fallback and renders the component only in the browser', async () => {
    const LazyChart = dynamic(async () => ({ default: Chart }), { ssr: false, loading: Loading });
    const container = document.createElement('div');
    container.innerHTML = '<p>loading chart…</p>';
    document.body.append(container);
    const recoverable = vi.fn();

    await act(async () => {
      hydrateRoot(container, <LazyChart points={5} />, { onRecoverableError: recoverable });
    });

    expect(recoverable).not.toHaveBeenCalled();
    expect(container.textContent).toBe('chart with 5 points');
    container.remove();
  });
});

describe('clientOnly', () => {
  it('hydrates its fallback, then renders the component', async () => {
    const Clock = clientOnly(() => <p>{`now: ${typeof window}`}</p>, { fallback: <p>…</p> });
    const container = document.createElement('div');
    container.innerHTML = '<p>…</p>';
    document.body.append(container);
    const recoverable = vi.fn();

    await act(async () => {
      hydrateRoot(container, <Clock />, { onRecoverableError: recoverable });
    });

    expect(recoverable).not.toHaveBeenCalled();
    expect(container.textContent).toBe('now: object');
    container.remove();
  });

  it('renders the component straight away when not hydrating', () => {
    const Clock = clientOnly(() => <p>client</p>);

    render(<Clock />);

    expect(screen.getByText('client')).toBeDefined();
  });
});
