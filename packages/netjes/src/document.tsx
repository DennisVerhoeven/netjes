import type { ReactNode } from 'react';
import { DEFERRED_BOOTSTRAP } from './deferred.ts';
import { DATA_ELEMENT_ID, type DocumentInfo } from './payload.ts';

type DocumentProps = { info: DocumentInfo; dataJson: string; children: ReactNode };

/**
 * The `<html>` shell around every page. React renders and hydrates the whole document, so `<title>`, `<meta>`
 * and `<link>` rendered by pages and layouts end up in the head.
 *
 * Documents stream, so the client entry is `async`: it hydrates the shell as soon as it loads, not once the last
 * Suspense boundary has arrived. Everything it reads comes before the content: the deferred-values bootstrap in the
 * head, the payload first in the body.
 */
export function Document({ info, dataJson, children }: DocumentProps) {
  const { nonce } = info;
  return (
    <html lang={info.lang}>
      <head>
        <meta charSet="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        {/* Vite reads the nonce from here for the styles it injects. */}
        {nonce && <meta property="csp-nonce" nonce={nonce} />}
        {/* Collects Page.Defer values streamed in before the client script runs. A client render finds it in the
            document the server wrote; one React creates would never run. */}
        {!info.clientRender && <script nonce={nonce} dangerouslySetInnerHTML={{ __html: DEFERRED_BOOTSTRAP }} />}
        {info.assets.stylesheets.map((href) => (
          <link key={href} rel="stylesheet" href={href} nonce={nonce} />
        ))}
        {info.devStyles?.map((style) => (
          <style key={style.id} data-vite-dev-id={style.id} nonce={nonce} dangerouslySetInnerHTML={{ __html: style.css }} />
        ))}
        {info.assets.modulePreloads.map((href) => (
          <link key={href} rel="modulepreload" href={href} nonce={nonce} />
        ))}
        {info.assets.scripts.map((src) => (
          <script key={src} type="module" src={src} async nonce={nonce} />
        ))}
      </head>
      <body>
        {/* Already escaped for HTML by the server; the client reads it back verbatim to hydrate. */}
        <script id={DATA_ELEMENT_ID} type="application/json" nonce={nonce} dangerouslySetInnerHTML={{ __html: dataJson }} />
        {children}
      </body>
    </html>
  );
}
