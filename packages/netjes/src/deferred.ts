// Deferred loader values (Page.Defer in C#): placeholders in the payload that become promises the page reads with
// React's use() inside a Suspense boundary.
import type { PagePayload, SegmentRef } from './payload.ts';

/** A promise that records its outcome the way React's `use()` reads it, so a settled one renders without suspending. */
export type Thenable<T> = Promise<T> & { status?: 'pending' | 'fulfilled' | 'rejected'; value?: T; reason?: unknown };

export type Deferred<T> = { promise: Thenable<T>; resolve(value: T): void; reject(reason: unknown): void };

export function createDeferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise: Thenable<T> = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  promise.status = 'pending';
  return {
    promise,
    resolve(value) {
      if (promise.status !== 'pending') return;
      promise.status = 'fulfilled';
      promise.value = value;
      resolve(value);
    },
    reject(reason) {
      if (promise.status !== 'pending') return;
      promise.status = 'rejected';
      promise.reason = reason;
      reject(reason);
    },
  };
}

/** Where revived placeholders get their promise from, by id. */
export type DeferredSource = (id: string) => Thenable<unknown>;

function isPlaceholder(value: object): value is { $netjes: 'deferred'; id: string } {
  return (value as Record<string, unknown>).$netjes === 'deferred' && typeof (value as Record<string, unknown>).id === 'string';
}

/** Replaces every `{ $netjes: 'deferred', id }` in a JSON value; returns the same object when there are none. */
export function reviveDeferred(value: unknown, source: DeferredSource): unknown {
  if (typeof value !== 'object' || value === null) return value;
  if (Array.isArray(value)) {
    let changed = false;
    const items = value.map((item) => {
      const revived = reviveDeferred(item, source);
      if (revived !== item) changed = true;
      return revived;
    });
    return changed ? items : value;
  }
  if (isPlaceholder(value)) return source(value.id);
  let changed = false;
  const entries = Object.entries(value).map(([key, item]) => {
    const revived = reviveDeferred(item, source);
    if (revived !== item) changed = true;
    return [key, revived] as const;
  });
  // Keys are defined, not assigned: JSON.parse keeps "__proto__" as a key, and assigning it would set the prototype.
  return changed ? Object.fromEntries(entries) : value;
}

/** Revives the placeholders in a page's props and its segments' props. */
export function revivePayload<T extends PagePayload>(payload: T, source: DeferredSource): T {
  const props = reviveDeferred(payload.props, source) as Record<string, unknown>;
  let segmentsChanged = false;
  const segments = payload.segments.map((segment): SegmentRef => {
    if (!segment.props) return segment;
    const revived = reviveDeferred(segment.props, source) as Record<string, unknown>;
    if (revived === segment.props) return segment;
    segmentsChanged = true;
    return { ...segment, props: revived };
  });
  return props === payload.props && !segmentsChanged ? payload : { ...payload, props, segments };
}

/** A source that hands out one deferred per id and settles it when the value arrives. */
export function deferredRegistry() {
  const entries = new Map<string, Deferred<unknown>>();
  const entry = (id: string) => {
    let found = entries.get(id);
    if (!found) {
      found = createDeferred<unknown>();
      // Unread values must not surface as unhandled rejections.
      found.promise.catch(() => {});
      entries.set(id, found);
    }
    return found;
  };
  return {
    source: ((id) => entry(id).promise) as DeferredSource,
    resolve(id: string, value: unknown) {
      entry(id).resolve(value);
    },
    reject(id: string, message: string) {
      entry(id).reject(new Error(message));
    },
    /** Rejects every value still pending, e.g. when the stream that was to deliver them ended. */
    rejectPending(message: (id: string) => string) {
      for (const [id, deferred] of entries) {
        if (deferred.promise.status === 'pending') deferred.reject(new Error(message(id)));
      }
    },
  };
}

/**
 * Inline in every document: collects deferred values streamed as `<script>self.__netjes_d(id, value, error)</script>`
 * until the client entry takes over (it replaces `__netjes_d`). The async client entry can run first, so this never
 * replaces a handler that is already there. It creates `__netjes_v` up front, as a window property of its own: until
 * then, page markup with `id="__netjes_v"` would be what that name reads (DOM clobbering).
 */
export const DEFERRED_BOOTSTRAP =
  "self.__netjes_d=self.__netjes_d||function(i,v,e){self.__netjes_v[i]=[v,e]};Object.prototype.hasOwnProperty.call(self,'__netjes_v')||(self.__netjes_v={})";

const UNSAFE = /[<>&\u2028\u2029]/g;

/** JSON that can sit inside a <script> element. */
export function htmlSafeJson(value: unknown): string {
  return (JSON.stringify(value) ?? 'null').replace(UNSAFE, (c) => `\\u${c.charCodeAt(0).toString(16).padStart(4, '0')}`);
}

/** The script that hands one deferred value (or its error) to the browser. */
export function deferredScript(id: string, value: unknown, error?: string, nonce?: string): string {
  // The value goes as JSON text for JSON.parse, which reads it as the other paths do: as an object literal, a
  // "__proto__" key would set the object's prototype instead.
  const json = htmlSafeJson(JSON.stringify(value) ?? 'null');
  const args = error === undefined ? `${htmlSafeJson(id)},JSON.parse(${json})` : `${htmlSafeJson(id)},null,${htmlSafeJson(error)}`;
  // Under a CSP with nonces, inline scripts run only with the page's nonce (base64, but escaped all the same).
  const attribute = nonce ? ` nonce="${nonce.replace(/[^A-Za-z0-9+/=_-]/g, '')}"` : '';
  return `<script${attribute}>self.__netjes_d(${args})</script>`;
}
