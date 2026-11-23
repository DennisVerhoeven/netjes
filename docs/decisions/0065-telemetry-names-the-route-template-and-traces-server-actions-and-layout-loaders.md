# 0065 – Telemetry names the route template and traces server actions and layout loaders

Status: accepted

## Context

Netjes tagged its spans and metrics with `netjes.route`, the route in page-file syntax (`/blog/[slug]`), while ASP.NET Core tags the request with `http.route`, its route template (`/blog/{slug}`). Nothing joined the two, so an app that wanted the render times of `netjes.render.duration` beside `http.server.request.duration` converted one notation into the other by Netjes's own rules for building its endpoints. The two duration histograms are in seconds and advised no bucket boundaries, so the OpenTelemetry SDK's defaults, made for milliseconds, put nearly every render in the first bucket. And server actions and layout loaders ran without a span, where page loaders and page actions had one.

## Decision

- **`http.route`.** Every span and metric that has a route also carries `http.route`, the route template of the same route, built with the rules that build the endpoints (`[p]` is `{p}`, `[...p]` is `{**p:minlength(1)}`, `[[...p]]` is `{**p}`). `netjes.route` stays as it is. The name is OpenTelemetry's semantic convention for it.
- **Bucket advice.** The histograms advise explicit boundaries: the seconds ASP.NET Core advises for `http.server.request.duration` for the two durations, and the Core Web Vitals' own limits for the vitals, in milliseconds, and for the layout shift. A view the app registers wins, as for any advice.
- **Spans.** A server action is a `netjes.server_action` span with `netjes.server_action.name`, inside the filter that turns its failures into responses, so a failing call marks the span. A layout loader is a `netjes.layout_loader` span with the layout's URL as `netjes.route` and `http.route`, and `netjes.layout`, the layout file's id. Both carry `netjes.package` for a page package's. They are opened by the filter that already traces page loaders and page actions, which is now one function for all four.

## Alternatives considered

- **A public helper that converts a pattern to a template.** It would put the conversion in every app's hands and a new public API in the listings, where a tag needs none: the app reads `http.route`.
- **Replacing `netjes.route` with `http.route`.** It would change the tag of every dashboard and view an app has built on it.
- **Bucket boundaries finer than ASP.NET Core's** (a tenth of a millisecond up), for renders that take less than 5 ms. An app that wants them registers a view; the default stays what the framework's own histogram advises, so the two compare.
- **A `netjes.route` on server action spans**, with `/_netjes/actions/{name}`. The URL is the same for every call and says nothing the name does not.

## Consequences

- Nothing an app has to do. Series gain a tag with one value for each existing `netjes.route` value, so no series are added.
- A route group's prefix on the endpoint's own route pattern is not in `http.route` of Netjes's spans, as it is not in `netjes.route`; ASP.NET Core's `http.route` has it.
