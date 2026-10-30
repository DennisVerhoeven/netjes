using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using TaskBoard.Data;
using TaskBoard.Features.Auth;
using TaskBoard.Features.Board;
using TaskBoard.Features.Members;
using TaskBoard.Features.Projects;
using TaskBoard.Features.Shell;
using TaskBoard.Features.Tasks;
using TaskBoard.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// The factory also registers TaskBoardDb per request. Page.Defer work runs alongside the rest of the request, so it
// creates its own context from the factory (a DbContext must not be shared between concurrent operations).
builder.Services.AddDbContextFactory<TaskBoardDb>(options =>
    TaskBoardDb.Configure(options, builder.Configuration.GetConnectionString("TaskBoard")));
// ASP.NET Core's validation for minimal API endpoints. Server actions do not need it: MapAction checks the
// DataAnnotations of its inputs itself and answers with ServerAction.Invalid.
builder.Services.AddValidation();
// Enums travel as camelCase strings ("inProgress"); the generated zod schemas follow the same options.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "taskboard.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // Never sent over plain http, even when a proxy in front does not pass the scheme on. Development runs on
        // http://localhost, so it follows the request.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        // Not redirects: Netjes renders app/unauthorized.tsx and the nearest forbidden.tsx for these statuses.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAuthLimits();
builder.Services.AddNetjes(options =>
    options.UseSharedProps(http => ValueTask.FromResult(new SharedProps(SignedInUser.From(http.User)))));
builder.Services.AddHealthChecks().AddNetjes();

var app = builder.Build();

if (args.Contains("migrate"))
{
    await Database.MigrateAsync(app.Services);
    return;
}

// Not while the netjes CLI runs a command (`netjes generate --check` in CI, `netjes routes`): those need no database.
if (app.Environment.IsDevelopment() && app.GetNetjesCommand() is null)
{
    await Database.MigrateAndSeedAsync(app.Services);
}

// In production a proxy terminates TLS in front of the app (compose.prod.yaml), so requests arrive as http from it.
// X-Forwarded-Proto and -For carry the visitor's scheme and address, believed only from the networks in
// ForwardedHeaders:KnownNetworks. With none configured the middleware stays out: ForwardedHeadersOptions without
// known networks or proxies would believe anyone.
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

// Outside Development: HSTS on https responses, and http redirected to https when the app knows its https port
// (ASPNETCORE_HTTPS_PORT). Behind the compose proxy every request is https already: the proxy redirects plain http.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseNetjes();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapHealthChecks("/healthz");
app.MapAuth();
app.MapAppShell();
app.MapProjects();
app.MapProjectLayout();
app.MapBoard();
app.MapTasks();
app.MapMembers();
app.MapPages();

app.Run();

public partial class Program;
