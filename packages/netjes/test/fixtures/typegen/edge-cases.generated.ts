import { z } from 'zod';

export const Circle = z.object({
  r: z.number().int(),
  name: z.string(),
});
export type Circle = z.infer<typeof Circle>;
export const Shape = z.discriminatedUnion("$type", [z.object({ name: z.string() }).extend({ "$type": z.literal("base") }), Circle.extend({ "$type": z.literal("circle") })]);
export type Shape = z.infer<typeof Shape>;
export const Puppy = z.object({
  age: z.number().int(),
  name: z.string(),
});
export type Puppy = z.infer<typeof Puppy>;
export const Dog2 = z.union([Puppy.extend({ "$type": z.literal("puppy") }), z.object({ name: z.string() })]);
export type Dog2 = z.infer<typeof Dog2>;
export const Pet = z.discriminatedUnion("$type", [z.object({ name: z.string() }).extend({ "$type": z.literal("dog") }), Puppy.extend({ "$type": z.literal("puppy") })]);
export type Pet = z.infer<typeof Pet>;
export const PinnedComment = z.object({
  text: z.string(),
  get replies() { return z.array(Comment); },
});
export type PinnedComment = z.infer<typeof PinnedComment>;
export const Comment = z.union([PinnedComment.extend({ "$type": z.literal("pinned") }), z.object({ text: z.string(), get replies() { return z.array(Comment); } })]);
export type Comment = z.infer<typeof Comment>;
export const Invoice = z.object({
  count: z.coerce.number().int(),
  price: z.coerce.number(),
});
export type Invoice = z.infer<typeof Invoice>;
export const Owner = z.object({
  n: z.string(),
});
export type Owner = z.infer<typeof Owner>;
export const Counters = z.object({
  ids: z.array(z.coerce.number().int()),
  totals: z.record(z.string(), z.coerce.number().nullable()),
  owners: z.array(Owner),
});
export type Counters = z.infer<typeof Counters>;
export const Precise = z.object({
  small: z.coerce.number(),
  plain: z.number(),
});
export type Precise = z.infer<typeof Precise>;
export const ZodSchemasEdgeCaseTestsRecord = z.object({
  a: z.string(),
});
export type ZodSchemasEdgeCaseTestsRecord = z.infer<typeof ZodSchemasEdgeCaseTestsRecord>;
export const ZodSchemasEdgeCaseTestsFormData = z.object({
  b: z.string(),
});
export type ZodSchemasEdgeCaseTestsFormData = z.infer<typeof ZodSchemasEdgeCaseTestsFormData>;
export const ZodSchemasEdgeCaseTestsSearchParamsInput = z.object({
  c: z.string(),
});
export type ZodSchemasEdgeCaseTestsSearchParamsInput = z.infer<typeof ZodSchemasEdgeCaseTestsSearchParamsInput>;
export const ZodSchemasEdgeCaseTestsNumber = z.object({
  d: z.number().int(),
});
export type ZodSchemasEdgeCaseTestsNumber = z.infer<typeof ZodSchemasEdgeCaseTestsNumber>;
export const UsesGlobals = z.object({
  record: ZodSchemasEdgeCaseTestsRecord,
  form: ZodSchemasEdgeCaseTestsFormData,
  search: ZodSchemasEdgeCaseTestsSearchParamsInput,
  number: ZodSchemasEdgeCaseTestsNumber,
});
export type UsesGlobals = z.infer<typeof UsesGlobals>;
