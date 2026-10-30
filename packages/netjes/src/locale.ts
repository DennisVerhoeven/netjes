import { usePagePayload } from './router.tsx';

/** The request culture the server rendered this page in (`AddNetjesLocalization`), e.g. `nl`; null without localization. */
export function useLocale(): string | null {
  return usePagePayload().locale ?? null;
}
