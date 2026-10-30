import { createContext } from 'react';

/**
 * During a server render: registers something to release once the document is done, such as the render's
 * QueryClient (whose queries may have their own gcTime timers). Null in the browser.
 */
export const RenderCleanupContext = createContext<((cleanup: () => void) => void) | null>(null);
