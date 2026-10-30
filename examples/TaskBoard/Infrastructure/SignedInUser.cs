using System.Security.Claims;

namespace TaskBoard.Infrastructure;

/// <summary>The signed-in user as the cookie carries it, and as every page gets it through shared props.</summary>
public sealed record SignedInUser(Guid Id, string Name, string Email)
{
    public static SignedInUser? From(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true
        && Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? new SignedInUser(id, principal.FindFirstValue(ClaimTypes.Name) ?? "", principal.FindFirstValue(ClaimTypes.Email) ?? "")
            : null;

    public ClaimsPrincipal ToPrincipal(string scheme) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Id.ToString()), new Claim(ClaimTypes.Name, Name), new Claim(ClaimTypes.Email, Email)],
            scheme));
}

public sealed record SharedProps(SignedInUser? User);

public static class ClaimsPrincipalExtensions
{
    /// <summary>The signed-in user's id. Endpoints that call this require authorization, so there always is one.</summary>
    public static Guid UserId(this ClaimsPrincipal principal) =>
        SignedInUser.From(principal)?.Id ?? throw new InvalidOperationException("No signed-in user; is the endpoint authorized?");
}
