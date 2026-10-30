import { z } from 'zod';

export const Measurements = z.object({
  mean: z.union([z.enum(["NaN", "Infinity", "-Infinity"]).transform(Number), z.coerce.number()]),
  peak: z.union([z.enum(["NaN", "Infinity", "-Infinity"]).transform(Number), z.coerce.number()]),
  cost: z.coerce.number(),
  samples: z.coerce.number().int(),
});
export type Measurements = z.infer<typeof Measurements>;
