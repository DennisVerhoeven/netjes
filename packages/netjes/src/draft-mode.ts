import { usePagePayload } from './router.tsx';

/** Whether this page was rendered in draft mode (`IDraftMode.Enable()` on the server), like Next.js's `draftMode()`. */
export function useDraftMode(): { isEnabled: boolean } {
  return { isEnabled: usePagePayload().draftMode === true };
}
