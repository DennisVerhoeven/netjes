import { afterEach, describe, expect, it } from 'vitest';
import { DEFERRED_BOOTSTRAP, createDeferred, deferredScript, reviveDeferred } from '../src/deferred.ts';

describe('deferred values', () => {
  it('replace placeholders anywhere in the props with the promises from the source', () => {
    const asked: string[] = [];
    const source = (id: string) => {
      asked.push(id);
      return createDeferred<unknown>().promise;
    };

    const props = reviveDeferred(
      { title: 'Post', comments: { $netjes: 'deferred', id: 'd0' }, nested: [{ likes: { $netjes: 'deferred', id: 'd1' } }] },
      source,
    ) as { title: string; comments: unknown; nested: { likes: unknown }[] };

    expect(props.title).toBe('Post');
    expect(props.comments).toBeInstanceOf(Promise);
    expect(props.nested[0]?.likes).toBeInstanceOf(Promise);
    expect(asked).toEqual(['d0', 'd1']);
  });

  it('keep a "__proto__" key as a key of a rebuilt object, as JSON.parse does, not as its prototype', () => {
    const value = JSON.parse('{"user":{"__proto__":{"isAdmin":true},"name":"x","later":{"$netjes":"deferred","id":"d0"}}}') as unknown;

    const revived = reviveDeferred(value, () => createDeferred<unknown>().promise) as { user: Record<string, unknown> };

    expect(Object.getPrototypeOf(revived.user)).toBe(Object.prototype);
    expect(Object.keys(revived.user)).toEqual(['__proto__', 'name', 'later']);
    expect(revived.user.isAdmin).toBeUndefined();
  });

  it('leave objects without placeholders as they are', () => {
    const value = { a: [1, { b: 'c' }] };

    expect(reviveDeferred(value, () => createDeferred<unknown>().promise)).toBe(value);
  });

  it('record their outcome on the promise, so React can read a settled value without suspending', async () => {
    const ok = createDeferred<number>();
    const failed = createDeferred<number>();
    failed.promise.catch(() => {});

    ok.resolve(3);
    failed.reject(new Error('down'));

    expect(ok.promise.status).toBe('fulfilled');
    expect(ok.promise.value).toBe(3);
    expect(failed.promise.status).toBe('rejected');
    await expect(ok.promise).resolves.toBe(3);
  });
});

describe('deferred-value scripts', () => {
  /** Runs a deferred value's script the way the browser does, and returns the value it hands over. */
  function handedOver(script: string): unknown {
    const body = /^<script[^>]*>(.*)<\/script>$/s.exec(script)?.[1] ?? '';
    let received: unknown;
    const run = new Function('self', body) as (self: unknown) => void;
    run({ __netjes_d: (_id: string, value: unknown) => (received = value) });
    return received;
  }

  it('hand over a "__proto__" key as a key, as JSON.parse reads it on the other paths', () => {
    const value = handedOver(deferredScript('d0', JSON.parse('{"__proto__":{"isAdmin":true},"name":"x"}')));

    expect(Object.getPrototypeOf(value)).toBe(Object.prototype);
    expect(Object.keys(value as object)).toEqual(['__proto__', 'name']);
  });

  it('hand over text that would close the script as it was', () => {
    expect(handedOver(deferredScript('d0', ['</script><b>hi</b>', '\u2028']))).toEqual(['</script><b>hi</b>', '\u2028']);
  });
});

describe('the deferred-values bootstrap', () => {
  /** Loads a document in a frame: unlike the test's own globals, a frame's window has named access, as browsers do. */
  function frameWindow(body: string) {
    const frame = document.createElement('iframe');
    document.body.append(frame);
    const doc = frame.contentDocument!;
    doc.open();
    doc.write(`<!doctype html><html><head><script>${DEFERRED_BOOTSTRAP}</script></head><body>${body}</body></html>`);
    doc.close();
    return frame.contentWindow as unknown as { __netjes_v?: Record<string, unknown[]>; Object: ObjectConstructor };
  }

  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('owns the collected-values global before page markup with id="__netjes_v" can stand in for it', () => {
    const window = frameWindow('<a id="__netjes_v">a</a><a id="__netjes_v">b</a><script>self.__netjes_d("d0",1)</script>');

    expect(Object.prototype.toString.call(window.__netjes_v)).toBe('[object Object]');
    expect(Object.getPrototypeOf(window.__netjes_v) === window.Object.prototype).toBe(true);
    expect(Object.keys(window.__netjes_v ?? {})).toEqual(['d0']);
    expect(window.__netjes_v?.d0?.[0]).toBe(1);
  });
});
