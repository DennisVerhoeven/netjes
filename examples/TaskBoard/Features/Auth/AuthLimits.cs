using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace TaskBoard.Features.Auth;

/// <summary>
/// How often sign-in and sign-up may be tried before the answer is 429. Binds from <c>AuthLimits</c>; a limit below 1 stops
/// the app at startup.
/// </summary>
public sealed class AuthLimitOptions
{
    /// <summary>Sign-in attempts per client address in a minute. Default 20.</summary>
    [Range(1, int.MaxValue)]
    public int SignInsPerAddress { get; set; } = 20;

    /// <summary>Sign-in attempts per account (email) in 15 minutes, right password or wrong. Default 10.</summary>
    [Range(1, int.MaxValue)]
    public int SignInsPerAccount { get; set; } = 10;

    /// <summary>Sign-ups per client address in an hour. Default 10.</summary>
    [Range(1, int.MaxValue)]
    public int SignUpsPerAddress { get; set; } = 10;
}

/// <summary>Limits on sign-in and sign-up, against password guessing, credential stuffing and mass sign-ups.</summary>
public static class AuthLimits
{
    /// <summary>The rate limiting policy of <c>auth.login</c>: per client address.</summary>
    public const string SignIn = "auth.sign-in";

    /// <summary>The rate limiting policy of <c>auth.register</c>: per client address.</summary>
    public const string SignUp = "auth.sign-up";

    public static IServiceCollection AddAuthLimits(this IServiceCollection services)
    {
        services.AddOptions<AuthLimitOptions>().BindConfiguration("AuthLimits").ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<AccountSignInLimiter>();
        services.AddRateLimiter(options =>
        {
            // Empty: Netjes answers an action call with an action failure, and a form post with the error page.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                RetryAfter(context.HttpContext, context.Lease);
                return ValueTask.CompletedTask;
            };
            options.AddPolicy(SignIn, http => ByAddress(http, Limits(http).SignInsPerAddress, TimeSpan.FromMinutes(1)));
            options.AddPolicy(SignUp, http => ByAddress(http, Limits(http).SignUpsPerAddress, TimeSpan.FromHours(1)));
        });
        return services;
    }

    private static AuthLimitOptions Limits(HttpContext http) => http.RequestServices.GetRequiredService<IOptions<AuthLimitOptions>>().Value;

    /// <summary>
    /// A fixed window per client address: behind the proxy, the visitor's (UseForwardedHeaders runs first), not the
    /// proxy's. An IPv6 visitor usually has a whole /64, so that counts as one address. Requests without an address
    /// (an in-process test server) share one window.
    /// </summary>
    private static RateLimitPartition<string> ByAddress(HttpContext http, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(AddressKey(http.Connection.RemoteIpAddress), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            QueueLimit = 0,
        });

    private static string AddressKey(IPAddress? address)
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

    /// <summary>
    /// An endpoint filter on <c>auth.login</c>: 429 once the account has had its attempts. It runs after binding, so it
    /// knows the email, and before the password is checked.
    /// </summary>
    public static async ValueTask<object?> PerAccount(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (context.Arguments.OfType<AuthEndpoints.LoginInput>().FirstOrDefault() is { Email: { } email })
        {
            using var lease = context.HttpContext.RequestServices.GetRequiredService<AccountSignInLimiter>().Attempt(AuthEndpoints.Normalize(email));
            if (!lease.IsAcquired)
            {
                RetryAfter(context.HttpContext, lease);
                return TypedResults.StatusCode(StatusCodes.Status429TooManyRequests);
            }
        }

        return await next(context);
    }

    private static void RetryAfter(HttpContext http, RateLimitLease lease)
    {
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// Sign-in attempts per account in fixed 15-minute windows. Every attempt counts, taken before the password is checked
/// so parallel guesses cannot slip past, and whether or not the account exists, so a 429 says nothing about that.
/// Someone else can use up an account's attempts too: the owner then waits out the window.
/// </summary>
public sealed class AccountSignInLimiter(IOptions<AuthLimitOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(email =>
        RateLimitPartition.GetFixedWindowLimiter(email, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.Value.SignInsPerAccount,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0,
        }));

    public RateLimitLease Attempt(string email) => _limiter.AttemptAcquire(email);

    public void Dispose() => _limiter.Dispose();
}
