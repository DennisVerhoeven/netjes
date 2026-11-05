# 0043 – Netjes marks the errors whose message it withheld

Status: accepted

## Context

Outside development, Netjes keeps an exception's message from the browser and writes a stand-in in its place: the status's reason phrase on an error page the server rendered ("Internal Server Error"), "Something went wrong" for a failed `Page.Defer` value, `Server action <name> failed with status 500` for a server action whose answer had no message, and "Something went wrong" in the browser for a thrown value that is not an Error. An `error.tsx` could not tell these stand-ins from a message worth showing, such as the text of an error thrown in the browser or the reason phrase for a `Page.Forbidden()`, because only a server setting, `IncludeErrorDetails`, tells them apart, and React cannot see it. The template's and the examples' error pages showed every message, so in production they printed the stand-in under their own heading.

The router's top-level boundary, which renders the root `app/error.tsx` for an error no segment caught, also replaced what it caught with a new Error with status 500: the page lost the error's class and stack, a server action's status, and a thrown string's text.

## Decision

- An error Netjes builds around a stand-in carries `withheld: true`, as it carries `status`: the error an error page gets from the server's props when the server withheld the exception's message, a deferred value's rejection when the server withheld its failure, a `ServerActionError` whose answer had no message, and the stand-in for a thrown value that has no message. `!error.withheld` means the message is worth showing. `ErrorPageProps` types it, and `ServerActionError` has it as a property.
- The server marks it: an error page's props carry `withheld: true` when an exception's message was replaced, which happens only with error details off. A status without an exception keeps its reason phrase, not withheld, in every environment.
- A withheld deferred failure travels as an empty error string on the existing transports: the `__netjes_d` scripts, the NDJSON lines, `ISsrRenderer.ResolveAsync` and the sidecar's `/resolve`. The client turns it into its stand-in, marked, and so does the sidecar for the server render, which writes it on as an empty string. Netjes's own messages for a value that did not arrive in time or was cancelled are not withheld.
- A server error sent without a stack has no stack in the browser either, not the stack of the client code that built it.
- The top-level boundary hands the app's error page the error as it caught it. The built-in error page still says 500.

## Alternatives considered

- **A `details` flag that says whether error details are on**, through a context, a set of errors or a flag in the document. It describes the server, not the error: an error thrown in the browser keeps its message in production, and Netjes's own message for a value that timed out is worth showing, so a flag would hide both or neither.
- **A prop of the error page next to `error`.** Every boundary would need it passed through, and code that catches a `ServerActionError` itself never sees an error page's props.
- **An empty message.** A page could test `error.message`, but a page that shows the message as it is would show nothing, and the reason phrase it shows now would be gone.
- **A hook that exposes `IncludeErrorDetails`.** It has the flag's problem, and it would send the setting to every page.
- **A fourth argument to `__netjes_d`.** It changes the bootstrap script, and with it the hash that hash-based policies list.
- **An error id that the server logs and the page shows** in place of the message. It is a feature of its own and not part of this decision.

## Consequences

- An error page renders `error.message` only when `!error.withheld`. The template's and the examples' error pages do, so in production they no longer print "Internal Server Error" under their heading.
- An error only the top-level boundary caught reaches the root `app/error.tsx` with its own class and stack and no status of 500, so a page that shows a status uses `error.status ?? 500`. A thrown string keeps its text there and on the built-in page.
- An empty error string means withheld on the transports. A custom `ISsrRenderer` passes it on as it is, and in development an exception with an empty message reads as withheld.
- Netjes sets `withheld` only on errors it builds, but it reads the property on any error, so an app's error class with a `withheld` property of its own reads as withheld.
