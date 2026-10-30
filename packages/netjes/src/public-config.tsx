import { createContext, useContext, useMemo } from 'react';
import { type StandardSchemaV1, parseWith } from './schema.ts';

const PublicConfigContext = createContext<Record<string, unknown>>({});

/** Provided by Netjes from the first document; tests can provide their own (or use `createTestApp({ publicConfig })`). */
export const PublicConfigProvider = PublicConfigContext.Provider;

/**
 * The server's `Netjes:PublicConfig` configuration section, the runtime counterpart of Next.js's `NEXT_PUBLIC_`
 * variables: change it per environment without rebuilding the client. Values arrive as strings (configuration has
 * no other type), so use coercing schemas such as `z.coerce.number()` or `z.stringbool()`. Everything in it is public.
 */
export function usePublicConfig(): unknown;
export function usePublicConfig<Schema extends StandardSchemaV1>(schema: Schema): StandardSchemaV1.InferOutput<Schema>;
export function usePublicConfig(schema?: StandardSchemaV1): unknown {
  const config = useContext(PublicConfigContext);
  return useMemo(() => (schema ? parseWith(schema, config, 'public config') : config), [config, schema]);
}
