import { buildEndpointUrl, type SearchParamsInput } from '../../../src/href.ts';

/**
 * What the app's endpoints read from their URLs, by method and route as in `api`: the route values, then the
 * query string values the handler binds. A JSON endpoint has a key for each of its methods except HEAD, and any
 * other endpoint one for GET, if it answers GET.
 */
export type EndpointParams = {
  "GET /api/posts/{slug}/likes/": { slug: string };
  "GET /api/search": { q?: string | null | undefined; take?: number | null | undefined; state?: "Open" | "InProgress" | 0 | 1 | null | undefined; tag?: readonly string[] | null | undefined; page?: number | null | undefined };
  "GET /docs/{**path:regex(\\.md$)}": { path: string };
  "GET /feed.xml": Record<string, never>;
  "GET /files/{*name}": { name: string };
  "GET /list/{page=1}": { page?: number | null | undefined };
  "GET /openapi/{documentName}.json": { documentName: string };
  "GET /opt/{id?}": { id?: number | null | undefined };
  "POST /api/posts/{slug}/likes/": { slug: string };
};
export type EndpointKey = keyof EndpointParams;
const endpointTemplates: Record<EndpointKey, string> = {
  "GET /api/posts/{slug}/likes/": "/api/posts/{slug}/likes",
  "GET /api/search": "/api/search",
  "GET /docs/{**path:regex(\\.md$)}": "/docs/{**path}",
  "GET /feed.xml": "/feed.xml",
  "GET /files/{*name}": "/files/{*name}",
  "GET /list/{page=1}": "/list/{page=1}",
  "GET /openapi/{documentName}.json": "/openapi/{documentName}.json",
  "GET /opt/{id?}": "/opt/{id?}",
  "POST /api/posts/{slug}/likes/": "/api/posts/{slug}/likes",
};

/**
 * The URL of one of the app's endpoints, with its values encoded: `endpointUrl('GET /api/posts/{slug}/likes/', { slug })`.
 * The values the route does not name go in the query string, followed by the third argument. The URL has no base
 * path: pass it through `useHref` for a `fetch` or an `<a href>`.
 */
export function endpointUrl<K extends EndpointKey>(
  key: K,
  ...args: {} extends EndpointParams[K]
    ? [params?: EndpointParams[K], search?: SearchParamsInput]
    : [params: EndpointParams[K], search?: SearchParamsInput]
): string {
  return buildEndpointUrl(endpointTemplates[key], args[0] ?? {}, args[1], key);
}
