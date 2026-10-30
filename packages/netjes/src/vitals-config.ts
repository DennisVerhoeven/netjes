import { createContext } from 'react';

/** Where `<WebVitals />` sends reports: set when the server collects them (`NetjesOptions.CollectWebVitals`). */
export const VitalsEndpointContext = createContext<string | undefined>(undefined);

export const VitalsEndpointProvider = VitalsEndpointContext.Provider;
