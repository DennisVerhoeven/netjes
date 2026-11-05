// Turns zod's compiled parsers off in the browser (ADR 0036). zod 4.4.0 and newer read this object whenever they make
// an object schema, whether zod loaded before this ran or after. Without `jitless`, zod tests whether it may make a
// function from a string, and a browser reports that test as a violation of any Content-Security-Policy without
// 'unsafe-eval', although zod catches the error.
//
// It must stay the client entry's first import, so that it runs ahead of the entry's other imports and of the page
// modules, which make their schemas as they load. (A production build moves the modules the entry shares with pages,
// such as React and the router, into chunks that run before the entry's own code; none of them makes a zod schema.)
// It imports nothing, so the entry still loads no zod.
type ZodGlobals = { __zod_globalConfig?: { jitless?: boolean } };

((globalThis as unknown as ZodGlobals).__zod_globalConfig ??= {}).jitless = true;

// A module, not a script, so that the type above stays its own.
export {};
