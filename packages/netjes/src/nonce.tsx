import { createContext, useContext } from 'react';

const NonceContext = createContext<string | undefined>(undefined);

/** Provided by Netjes from the first document (`NetjesOptions.ContentSecurityPolicy` or `HttpContext.GetCspNonce()`). */
export const NonceProvider = NonceContext.Provider;

/**
 * This page's Content-Security-Policy nonce, for inline scripts and styles of your own; undefined without one (no
 * policy, or one without `{nonce}`).
 */
export function useNonce(): string | undefined {
  return useContext(NonceContext);
}
