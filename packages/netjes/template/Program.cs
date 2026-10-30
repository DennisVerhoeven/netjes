using __NAME__;
using __NAME__.Features.Home;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNetjes(options =>
    // Available in every page and layout through useSharedProps(sharedProps); typed in generated/netjes.ts.
    options.UseSharedProps(_ => ValueTask.FromResult(new SiteProps("__TITLE__"))));
builder.Services.AddHealthChecks().AddNetjes();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    // Once the site is served over https: HSTS on https responses, and http redirected to https. Behind a proxy
    // that terminates TLS, add UseForwardedHeaders() first so the app sees the original scheme.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseNetjes();
app.UseAntiforgery(); // after authentication, if you add it

app.MapHealthChecks("/healthz");
app.MapHomePage();
app.MapPages(); // every other page file, plus the 404 fallback

app.Run();
