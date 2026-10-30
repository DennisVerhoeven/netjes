// @vitest-environment node
// The generated zod schemas (pinned by tests/Netjes.Tests/TypeGen/GoldenFileTests.cs) against JSON that
// System.Text.Json wrote for the same C# values. Typechecking this file also proves the schemas compile.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { Category, PagedOfAuthor, Post, Shape } from './fixtures/typegen/schemas.generated.ts';

const samples = JSON.parse(readFileSync(new URL('./fixtures/typegen/samples.json', import.meta.url), 'utf8')) as Record<string, unknown>;

describe('generated zod schemas', () => {
  it('parse what System.Text.Json writes', () => {
    const post = Post.parse(samples.post);
    const category = Category.parse(samples.category);
    const shape = Shape.parse(samples.shape);
    const paged = PagedOfAuthor.parse(samples.paged);

    expect(post.status).toBe(1);
    expect(post.author.bio).toBeNull();
    expect(category.children[0]?.name).toBe('child');
    expect(shape.$type === 'circle' && shape.radius).toBe(2);
    expect(paged.items).toHaveLength(1);
  });

  it('infer recursive and discriminated types', () => {
    const tree: Category = { name: 'a', children: [{ name: 'b', children: [], parent: null }], parent: null };
    const square: Shape = { $type: 'square', side: 1, color: 'blue' };

    expect(Category.parse(tree).children[0]?.parent).toBeNull();
    expect(Shape.parse(square).$type).toBe('square');
  });

  it('reject what the C# type could not have produced', () => {
    expect(Post.safeParse({ ...(samples.post as object), views: 1.5 }).success).toBe(false);
    expect(Shape.safeParse({ $type: 'triangle', color: 'red' }).success).toBe(false);
    expect(Category.safeParse({ name: 'a', children: [{ name: 1 }], parent: null }).success).toBe(false);
  });
});
