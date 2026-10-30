import type { ReactNode } from 'react';
import { QueryShell } from 'virtual:netjes/query';
import { Document } from './document.tsx';
import { ImageConfigProvider } from './image.tsx';
import { NonceProvider } from './nonce.tsx';
import { PublicConfigProvider } from './public-config.tsx';
import type { ModuleLoader, RouteModules } from './modules.ts';
import type { DocumentInfo, InitialPayload } from './payload.ts';
import { RouterProvider } from './router.tsx';
import { VitalsEndpointProvider } from './vitals-config.ts';

type RootProps = { payload: InitialPayload; modules: RouteModules; loader: ModuleLoader; dataJson: string };

/** App-wide configuration the server sends with the first document (it does not change between navigations). */
function RootProviders({ info, children }: { info: DocumentInfo; children: ReactNode }) {
  let element = children;
  if (info.images) element = <ImageConfigProvider value={info.images}>{element}</ImageConfigProvider>;
  if (info.publicConfig) element = <PublicConfigProvider value={info.publicConfig}>{element}</PublicConfigProvider>;
  if (info.nonce) element = <NonceProvider value={info.nonce}>{element}</NonceProvider>;
  if (info.vitalsEndpoint) element = <VitalsEndpointProvider value={info.vitalsEndpoint}>{element}</VitalsEndpointProvider>;
  return element;
}

/** The same tree on the server and in the browser, so hydration matches. */
export function Root({ payload, modules, loader, dataJson }: RootProps) {
  return (
    <Document info={payload.document} dataJson={dataJson}>
      <RootProviders info={payload.document}>
        <RouterProvider
          initial={payload}
          initialModules={modules}
          loader={loader}
          errorPage={payload.document.errorPage}
          shell={QueryShell}
        />
      </RootProviders>
    </Document>
  );
}
