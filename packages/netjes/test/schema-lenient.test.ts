import { describe, expect, it } from 'vitest';
import { z } from 'zod';
import { parseLenient, type StandardSchemaV1 } from '../src/schema.ts';

/** Wraps a schema and counts its validations: the work parseLenient does, without wall-clock time. */
function counted<Schema extends StandardSchemaV1>(schema: Schema) {
  const calls = { validations: 0 };
  const wrapped: StandardSchemaV1 = {
    '~standard': {
      ...schema['~standard'],
      validate: (value: unknown) => {
        calls.validations++;
        return schema['~standard'].validate(value);
      },
    },
  };
  return { schema: wrapped, calls };
}

describe('parseLenient', () => {
  it('drops keys a strict schema does not know, though that issue names no key', () => {
    const strict = z.object({ q: z.string().optional(), page: z.coerce.number().default(1) }).strict();

    expect(parseLenient(strict, { q: 'shoes', utm_source: 'mail', fbclid: 'x' }, 'search params')).toEqual({ q: 'shoes', page: 1 });
  });

  it('drops unknown keys without dropping one a refinement needs', () => {
    const strict = z
      .object({ q: z.string().optional(), tag: z.string().optional() })
      .strict()
      .refine((value) => value.q || value.tag, 'need q or tag');

    expect(parseLenient(strict, { q: 'shoes', utm: '1' }, 'search params')).toEqual({ q: 'shoes' });
  });

  it('drops thousands of unknown keys in one pass', () => {
    const strict = z.object({ q: z.string().optional() }).strict();
    const input = Object.fromEntries(Array.from({ length: 2700 }, (_, i) => [`k${i}`, '1']));

    const { schema, calls } = counted(strict);
    expect(parseLenient(schema, { ...input, q: 'x' }, 'search params')).toEqual({ q: 'x' });
    // One pass drops every key the issue lists, one more confirms: not a pass per key.
    expect(calls.validations).toBeLessThanOrEqual(3);
  });

  it('gives up instead of searching through many keys when an issue names none', () => {
    // A schema whose pathless issue does not list the keys (unlike zod's unrecognized_keys), but counts them.
    const known = new Set(['q']);
    const opaque = {
      '~standard': {
        version: 1 as const,
        vendor: 'test',
        validate: (value: unknown) => {
          const unknown = Object.keys(value as object).filter((key) => !known.has(key));
          return unknown.length === 0 ? { value } : { issues: [{ message: `${unknown.length} unexpected keys` }] };
        },
      },
    };
    const input = Object.fromEntries(Array.from({ length: 2000 }, (_, i) => [`k${i}`, '1']));

    expect(parseLenient(opaque, { q: 'x', stray: '1' }, 'search params')).toEqual({ q: 'x' });
    const { schema, calls } = counted(opaque);
    expect(() => parseLenient(schema, { ...input, q: 'x' }, 'search params')).toThrow('Invalid search params');
    // It does not try the 2000 keys one by one.
    expect(calls.validations).toBeLessThan(10);
  });

  it('stops after a bounded number of passes when a schema reports one bad key at a time', () => {
    const shape = z.record(z.string(), z.string()).superRefine((value, ctx) => {
      const bad = Object.keys(value).find((key) => key.startsWith('bad'));
      if (bad) ctx.addIssue({ code: 'custom', path: [bad], message: 'bad key' });
    });
    const input = Object.fromEntries(Array.from({ length: 3000 }, (_, i) => [`bad${i}`, '1']));

    const { schema, calls } = counted(shape);
    expect(() => parseLenient(schema, { ...input, q: 'x' }, 'search params')).toThrow('Invalid search params');
    // Bounded by the pass limit (64 passes plus the final failing one), not by the 3000 bad keys.
    expect(calls.validations).toBeLessThanOrEqual(65);
    expect(parseLenient(shape, { bad1: '1', bad2: '2', q: 'x' }, 'search params')).toEqual({ q: 'x' });
  });

  it('still fails when no key is to blame', () => {
    const never = z.object({ a: z.string().optional() }).refine(() => false, 'never valid');

    expect(() => parseLenient(never, { a: 'x' }, 'search params')).toThrow('Invalid search params: never valid');
  });
});
