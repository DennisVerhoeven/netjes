// JavaScript, as a package may ship a module it imports by name: Vite would hand such a file in node_modules to Node
// for server rendering, a second instance of it, unless it processes the package itself.

/** One object per module instance: a page that renders its id shows which instance it got. */
export const marker = { id: `installed-${Math.random().toString(36).slice(2)}` };
