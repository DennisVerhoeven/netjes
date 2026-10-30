import { z } from 'zod';

export const Status = z.union([z.literal(0), z.literal(1)]);
export type Status = z.infer<typeof Status>;
export const Author = z.object({
  name: z.string(),
  bio: z.string().nullable(),
});
export type Author = z.infer<typeof Author>;
export const Post = z.object({
  title: z.string(),
  subtitle: z.string().nullable(),
  views: z.number().int(),
  rating: z.number().nullable(),
  published: z.boolean(),
  publishedOn: z.iso.date(),
  updatedAt: z.iso.datetime({ offset: true, local: true }),
  createdAt: z.iso.datetime({ offset: true, local: true }),
  at: z.iso.time(),
  id: z.guid(),
  link: z.string(),
  status: Status,
  author: Author,
  tags: z.array(z.string()),
  coauthors: z.array(Author),
  counts: z.record(z.string(), z.number().int()),
  thumbnail: z.base64(),
  extra: z.unknown(),
  raw: z.unknown(),
  renamed: z.string(),
  maybe: z.string().nullable().optional(),
});
export type Post = z.infer<typeof Post>;
export const Category = z.object({
  name: z.string(),
  get children() { return z.array(Category); },
  get parent() { return Category.nullable(); },
});
export type Category = z.infer<typeof Category>;
export const Circle = z.object({
  radius: z.number(),
  color: z.string(),
});
export type Circle = z.infer<typeof Circle>;
export const Square = z.object({
  side: z.number(),
  color: z.string(),
});
export type Square = z.infer<typeof Square>;
export const Shape = z.discriminatedUnion("$type", [Circle.extend({ "$type": z.literal("circle") }), Square.extend({ "$type": z.literal("square") })]);
export type Shape = z.infer<typeof Shape>;
export const PagedOfAuthor = z.object({
  items: z.array(Author),
  total: z.number().int(),
});
export type PagedOfAuthor = z.infer<typeof PagedOfAuthor>;
