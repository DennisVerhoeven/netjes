using NetjesBench;

// The benchmark app (bench/netjes/run.sh): a list page, a detail page whose slow field streams in (Page.Defer), and
// the same list from the output cache. Its data lives in memory and is the same on every run.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNetjes();
builder.Services.AddOutputCache();
builder.Services.AddHealthChecks().AddNetjes();
builder.Services.AddSingleton<Catalog>();

var app = builder.Build();

app.UseNetjes();
app.UseAntiforgery();
app.UseOutputCache();

app.MapHealthChecks("/healthz");
app.MapCatalogPages();
app.MapPages();

app.Run();
