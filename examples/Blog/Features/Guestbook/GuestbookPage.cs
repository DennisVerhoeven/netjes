using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Blog.Features.Guestbook;

/// <summary>The guestbook page: a loader for the entries and an action for signing it.</summary>
public static class GuestbookPage
{
    public sealed record SignForm
    {
        public string? Name { get; init; }
        public string? Message { get; init; }
    }

    public sealed record GuestbookProps(
        IReadOnlyList<GuestbookEntry> Entries,
        IReadOnlyDictionary<string, string>? Errors = null,
        SignForm? Values = null);

    public const int MaxNameLength = 40;
    public const int MaxMessageLength = 280;
    public const int SignsPerMinute = 10;
    public const string RateLimitPolicy = "guestbook";

    /// <summary>
    /// Signing is anonymous, so it is limited per client address. Behind a reverse proxy, put the proxy's network in
    /// <c>ForwardedHeaders:KnownNetworks</c> so the address is the visitor's and not the proxy's (otherwise every
    /// visitor shares one limit). An IPv6 visitor usually has a whole /64, so that counts as one address.
    /// </summary>
    /// <param name="options">The limiter's options.</param>
    /// <param name="configuration">
    /// <c>Guestbook:SignsPerMinute</c> replaces <see cref="SignsPerMinute"/> when it is a positive number; the end-to-end
    /// suites raise it because every test signs from one address.
    /// </param>
    public static void ConfigureRateLimit(RateLimiterOptions options, IConfiguration configuration)
    {
        var permits = int.TryParse(configuration["Guestbook:SignsPerMinute"], out var configured) && configured > 0 ? configured : SignsPerMinute;
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
            AddressKey(http.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    /// <summary>The limiter's key for a client: IPv4-mapped addresses as IPv4, other IPv6 ones as their /64, none as "".</summary>
    internal static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return "";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    public static void MapGuestbookPage(this IEndpointRouteBuilder app)
    {
        // Signing needs a server, so a static export leaves this page out.
        app.MapPageLoader("/guestbook", (GuestbookStore store) => Page.Props(new GuestbookProps(store.Recent())))
            .ExcludeFromExport();

        // [FromForm] binding validates the antiforgery token the Form component adds.
        app.MapPageAction("/guestbook", ([FromForm] SignForm form, GuestbookStore store) =>
        {
            var errors = Validate(form);
            if (errors.Count > 0)
            {
                return Page.Props(new GuestbookProps(store.Recent(), errors, form)).WithStatus(StatusCodes.Status422UnprocessableEntity);
            }

            store.Sign(form.Name!.Trim(), form.Message!.Trim());
            return Page.Redirect("/guestbook");
        })
        .RequireRateLimiting(RateLimitPolicy);
    }

    internal static Dictionary<string, string> Validate(SignForm form)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(form.Name))
        {
            errors["name"] = "Tell us who you are.";
        }
        else if (form.Name.Trim().Length > MaxNameLength)
        {
            errors["name"] = $"Keep your name under {MaxNameLength} characters.";
        }

        if (string.IsNullOrWhiteSpace(form.Message))
        {
            errors["message"] = "Leave a message.";
        }
        else if (form.Message.Trim().Length > MaxMessageLength)
        {
            errors["message"] = $"Keep it under {MaxMessageLength} characters.";
        }

        return errors;
    }
}
