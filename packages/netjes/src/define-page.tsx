import type { ComponentType, FunctionComponent, ReactNode } from 'react';
import type { LayoutProps } from './modules.ts';
import { type StandardSchemaV1, parseWith } from './schema.ts';

export type { StandardSchemaV1 } from './schema.ts';

/**
 * Declares a page whose props (sent by the C# loader) are validated with a Standard Schema before rendering,
 * so the component receives parsed, typed props instead of trusting the server's JSON.
 */
export function definePage<Schema extends StandardSchemaV1>(
  schema: Schema,
  Page: ComponentType<StandardSchemaV1.InferOutput<Schema>>,
): FunctionComponent<Record<string, unknown>> {
  function ValidatedPage(props: Record<string, unknown>) {
    const parsed = parseWith(schema, props, 'props') as StandardSchemaV1.InferOutput<Schema> & object;
    return <Page {...parsed} />;
  }
  ValidatedPage.displayName = `definePage(${Page.displayName ?? Page.name ?? 'Page'})`;
  return ValidatedPage;
}

/**
 * Declares a layout whose loader props (from `MapLayoutLoader` in C#) are validated with a Standard Schema. The
 * component gets the parsed props next to `children` and `params`.
 */
export function defineLayout<Schema extends StandardSchemaV1>(
  schema: Schema,
  Layout: ComponentType<StandardSchemaV1.InferOutput<Schema> & LayoutProps>,
): FunctionComponent<LayoutProps & Record<string, unknown>> {
  function ValidatedLayout({ children, params, ...props }: LayoutProps & Record<string, unknown>): ReactNode {
    const parsed = parseWith(schema, props, 'layout props') as StandardSchemaV1.InferOutput<Schema> & object;
    return (
      <Layout {...parsed} params={params}>
        {children}
      </Layout>
    );
  }
  ValidatedLayout.displayName = `defineLayout(${Layout.displayName ?? Layout.name ?? 'Layout'})`;
  return ValidatedLayout;
}
