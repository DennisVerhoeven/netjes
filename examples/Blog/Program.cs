using System.Text.Json;
using System.Text.Json.Serialization;
using Blog;
using Blog.Features.Docs;
using Blog.Features.Guestbook;
using Blog.Features.Posts;
using Blog.Features.Streaming;
using Microsoft.AspNetCore.HttpOverrides;
using Netjes.Examples.HelpCenter;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddNetjes(options =>
{
    // <WebVitals /> in the root layouts reports Core Web Vitals; they become netjes.web_vitals.* metrics.
    options.CollectWebVitals = true;

    // Only scripts Netjes rendered (and what they load) may run.
    options.ContentSecurityPolicy = "script-src 'nonce-{nonce}' 'strict-dynamic'; object-src 'none'; base-uri 'self'";

    // Available in every page and layout through useSharedProps(sharedProps); typed in generated/netjes.ts, where
    // each Href is the Href schema, so the links reach <Link> checked against the app's pages.
    options.UseSharedProps(_ => ValueTask.FromResult(new SiteProps(
        "Netjes Blog",
        [
            new(new Href("/blog"), "Blog"), new(new Href("/docs/introduction"), "Docs"), new(new Href("/guestbook"), "Guestbook"),
            new(new Href("/about"), "About"),
        ])));
});
// <Image> resizes and serves WebP through SkiaSharp; without this it serves originals.
builder.Services.AddNetjesSkiaImages();
builder.Services.AddAntiforgery();
builder.Services.AddOutputCache();
builder.Services.AddRateLimiter(options => GuestbookPage.ConfigureRateLimit(options, builder.Configuration));
builder.Services.AddHealthChecks().AddNetjes();
builder.Services.AddSingleton<PostStore>();
builder.Services.AddSingleton<DocsStore>();
builder.Services.AddSingleton<GuestbookStore>();
builder.Services.AddHelpCenter();

var app = builder.Build();

// Mounted under a base path when configured (PathBase=/blog), like Next.js's basePath. Before UseNetjes.
if (app.Configuration["PathBase"] is { Length: > 0 } pathBase)
{
    app.UsePathBase(pathBase);
}

// Behind a reverse proxy, X-Forwarded-For carries the visitor's address (the guestbook limits per address). It is
// believed only from the networks in ForwardedHeaders:KnownNetworks; with none configured the middleware stays out,
// because ForwardedHeadersOptions without known networks or proxies would believe anyone.
if (app.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() is { Length: > 0 } proxyNetworks)
{
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var network in proxyNetworks)
    {
        forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }

    app.UseForwardedHeaders(forwarded);
}

app.UseNetjes();
app.UseRateLimiter();
app.UseAntiforgery();
// For the posts' .CachePage, in every environment: in Development, Netjes renders every page for each request instead
// and stores none, so an edit shows on the next request.
app.UseOutputCache();

app.MapHealthChecks("/healthz");
app.MapPostPages();
app.MapPostLikesApi();
app.MapDocsPages();
app.MapGuestbookPage();
app.MapStreamingDemo();
// The help center page package (examples/HelpCenter), mounted at /help inside the Blog's root layout. The Blog is the
// app the package is developed in, so it writes the package's own generated file. A static export leaves the package's
// pages out: its article page posts feedback, which needs a server.
app.MapHelpCenter("/help")
    .WriteTypes("../HelpCenter/generated/netjes.ts")
    .ExcludeFromExport();
// sitemap.xml from the route table (docs through their StaticParams), and robots.txt pointing at it. netjes export
// writes both files from this URL, so set SiteUrl for an export that goes online.
var siteUrl = new Uri(app.Configuration["SiteUrl"] ?? "http://localhost:5080");
app.MapSitemap(siteUrl);
app.MapRobotsTxt(siteUrl);
app.MapPages();

app.Run();

// Lets the tests start the real app.
public partial class Program;
