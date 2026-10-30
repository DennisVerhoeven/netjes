// zod helpers (netjes/zod). Only this module imports zod, so apps on another schema library do not need it.
import { z } from 'zod';

/**
 * What `deferred()` parses to: a thenable that React's `use()` reads (it records `status` and `value` as React
 * expects). Not a Promise, because zod would then treat the whole schema as async.
 */
export class DeferredValue<T> implements PromiseLike<T> {
  status: 'pending' | 'fulfilled' | 'rejected' = 'pending';
  value?: T;
  reason?: unknown;
  private readonly settled: Promise<T>;

  constructor(source: PromiseLike<unknown>, parse: (value: unknown) => T) {
    this.settled = Promise.resolve(source).then(
      (raw) => {
        try {
          const value = parse(raw);
          this.status = 'fulfilled';
          this.value = value;
          return value;
        } catch (error) {
          this.status = 'rejected';
          this.reason = error;
          throw error;
        }
      },
      (reason) => {
        this.status = 'rejected';
        this.reason = reason;
        throw reason;
      },
    );
    // Values nobody reads must not surface as unhandled rejections.
    this.settled.catch(() => {});
  }

  then<R1 = T, R2 = never>(
    onFulfilled?: ((value: T) => R1 | PromiseLike<R1>) | null,
    onRejected?: ((reason: unknown) => R2 | PromiseLike<R2>) | null,
  ): PromiseLike<R1 | R2> {
    return this.settled.then(onFulfilled, onRejected);
  }
}

const validated = new WeakMap<object, Map<z.ZodType, DeferredValue<unknown>>>();

/**
 * A `Page.Defer` value in page or layout props: a promise of `schema`'s output. Read it with React's `use()` inside
 * a Suspense boundary. The resolved value is validated; a mismatch rejects (and reaches the error boundary).
 */
export function deferred<Schema extends z.ZodType>(schema: Schema): z.ZodType<PromiseLike<z.output<Schema>>, Promise<unknown>> {
  const parsed = z
    .custom<Promise<unknown>>((value) => value instanceof Promise, { message: 'Expected a deferred value (Page.Defer)' })
    .transform((promise): DeferredValue<z.output<Schema>> => {
      // The same thenable every time, so use() does not suspend again after each render.
      let bySchema = validated.get(promise);
      if (!bySchema) validated.set(promise, (bySchema = new Map()));
      let result = bySchema.get(schema);
      if (!result) {
        result = new DeferredValue(promise, (value) => {
          const parsed = schema.safeParse(value);
          if (parsed.success) return parsed.data;
          throw new Error(`Invalid deferred value: ${parsed.error.issues.map((i) => `${i.path.join('.') || '(value)'}: ${i.message}`).join('; ')}`);
        });
        bySchema.set(schema, result);
      }
      return result as DeferredValue<z.output<Schema>>;
    });
  // zod types a transform returning a thenable as its awaited value; it is the thenable.
  return parsed as unknown as z.ZodType<PromiseLike<z.output<Schema>>, Promise<unknown>>;
}
