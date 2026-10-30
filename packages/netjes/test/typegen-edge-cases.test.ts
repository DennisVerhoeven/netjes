// @vitest-environment node
// The generator's edge cases (pinned by tests/Netjes.Tests/TypeGen/GoldenFileTests.cs): each schema module must load
// and accept what System.Text.Json writes. Typechecking this file also proves they compile.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { Measurements } from './fixtures/typegen/named-floats.generated.ts';
import { Comment, Counters, Invoice, Pet, Precise, Shape, UsesGlobals } from './fixtures/typegen/edge-cases.generated.ts';

const samples = JSON.parse(readFileSync(new URL('./fixtures/typegen/edge-case-samples.json', import.meta.url), 'utf8')) as Record<string, unknown>;

describe('generated zod schemas: edge cases of the generator', () => {
  it('parse polymorphic types: a base that lists itself, a derived type with its own union, a recursive concrete base', () => {
    expect(Shape.parse(samples.shapeBase)).toEqual({ $type: 'base', name: 'b' });
    expect(Shape.parse(samples.shapeCircle)).toMatchObject({ $type: 'circle', r: 1 });
    expect(Pet.parse(samples.petDog)).toEqual({ $type: 'dog', name: 'd' });
    expect(Pet.parse(samples.petPuppy)).toMatchObject({ $type: 'puppy', age: 1 });
    const comment: Comment = Comment.parse(samples.comment);
    expect(comment.replies[0]?.text).toBe('b');
    expect(Comment.parse(samples.pinned)).toMatchObject({ $type: 'pinned', text: 'p' });
  });

  it('parse numbers written as strings: by the class, in collections, Half', () => {
    expect(Invoice.parse(samples.invoice)).toEqual({ count: 3, price: 1.5 });
    const counters = Counters.parse(samples.counters);
    expect(counters.ids).toEqual([1, 2]);
    expect(counters.totals.b).toBeNull();
    expect(Precise.parse(samples.precise)).toEqual({ small: 1.5, plain: 2 });
  });

  it('parse named floating-point literals as numbers', () => {
    const measurements = Measurements.parse(samples.measurements);
    expect(measurements.mean).toBeNaN();
    expect(measurements.peak).toBe(Number.NEGATIVE_INFINITY);
    expect(measurements).toMatchObject({ cost: 2.5, samples: 4 });
  });

  it('keep types named like globals apart from them', () => {
    const parsed = UsesGlobals.parse(samples.usesGlobals);
    const empty: Record<string, never> = {};
    expect(parsed.number.d).toBe(4);
    expect(empty).toEqual({});
  });
});
