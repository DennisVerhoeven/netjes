# 0035 – Development styles are inlined from the client environment's build

Status: accepted; extends [ADR 0004](0004-vite-for-dev-server-and-builds.md)

## Context

In development, the sidecar inlines the CSS that a page and its segment files, such as its layouts, import into the document it renders, as `<style data-vite-dev-id>` elements, so that the page is styled before its scripts run ([ADR 0004](0004-vite-for-dev-server-and-builds.md)). Vite's client adopts those elements. The client entry loads the page's modules before it hydrates, and as the module Vite serves for each CSS file runs, it finds the element with its id and sets the element's text to its own when the two differ.

The sidecar took that text from Vite's SSR environment, by loading each file as `<file>?inline`, while the module the browser imports is the client environment's build of the file. Vite builds a file once in each environment, and a plugin can build it differently in each. Tailwind CSS keeps the classes it has seen per environment, so the two builds can drift apart after many edits. With `css.devSourcemap` on, they differed for every file: the client's text ends with a source map comment that the `?inline` build lacks. Whenever the texts differed, Vite's client replaced the server's text before hydration, and React's development build, which compares the text of an element rendered with `dangerouslySetInnerHTML`, logged a hydration error for the `<style>`. The error pointed at Netjes's markup rather than at the app's, an end-to-end test that fails on console errors failed on it, and a page loaded without JavaScript stayed styled by the SSR build, which the page's scripts would have replaced.

## Decision

- The sidecar still finds a page's CSS files in the SSR environment's module graph, which loaded the page and its segment files. It takes each file's text from the client environment's build (`server.environments.client.transformRequest`): the module the browser imports, which Vite keeps in its cache and serves to the browser until an edit invalidates it. The text is the JSON string on the module's line that starts with `const __vite__css = `, the string Vite's client passes to `updateStyle`. The sidecar finds the line by splitting the code on `\n`: `JSON.stringify` leaves U+2028 and U+2029 in the string, and a regular expression's `.` stops at both.
- For a module without that line, from a Vite or a plugin that writes it otherwise, the sidecar inlines the SSR environment's `?inline` build of the file, as before, and logs a warning, once per process, that the inlined CSS can differ from the browser's.
- `Document` renders each `<style data-vite-dev-id>` with `suppressHydrationWarning`. Vite's client owns the element's text once it runs, and a file edited between the render and the import still differs, which is no fault of the page.

## Alternatives considered

- **`suppressHydrationWarning` alone.** React would stop reporting the difference, but each file would still be built twice, and a page seen before its scripts run, or without JavaScript, would be styled by the SSR build, which the page's scripts replace.
- **A `<link rel="stylesheet">` to the file's CSS request (`?direct`)** in place of the inlined text. Vite builds that request apart from the module the browser imports, so it is a third build, which can drift as the second did. A link without the module's `data-vite-dev-id` also makes the module add a `<style>` of its own, so the CSS applies twice.
- **A plugin that records each file's CSS in the client environment** as Vite's CSS plugin hands it on, before `vite:css-post` turns it into a module. It would read no generated code, but under `css.devSourcemap` it records the CSS before the source map comment is added, so the text would still differ from the one the browser sets.

## Consequences

- In development, the CSS inlined into a document is the text the browser's module for the file sets, so Vite's client leaves the element as it is, and React has no difference to report. A page seen without JavaScript gets the same CSS as one whose scripts run.
- React no longer reports a difference in the text of these elements, whatever its cause, such as an edit between the render and the import. Vite's client applies the edit as before.
- The sidecar runs the client environment's plugins on a page's CSS during the render, and the browser gets the result from Vite's cache. Tailwind CSS scans the files on disk for classes, so a build made before the browser loads the page's modules misses none.
- The sidecar reads a detail of Vite's output that is not an API: the `const __vite__css = ` line of the module Vite serves for a CSS file in development. Its tests check that line against the installed Vite. If a Vite release writes the module otherwise, pages still render, with the SSR build and one warning, until Netjes reads the new form.
- The module of a CSS Modules file (`*.module.css`) that the sidecar read for a page no browser has loaded since the dev server started is in the client environment's module graph with nothing importing it. Vite treats an edit to that file as a dead end and reloads every open page of the app.
- Production is unchanged: its documents link the built stylesheets, and nothing is inlined.
