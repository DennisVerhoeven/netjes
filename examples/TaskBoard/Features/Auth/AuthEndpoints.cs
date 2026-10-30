using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TaskBoard.Data;
using TaskBoard.Infrastructure;

namespace TaskBoard.Features.Auth;

/// <summary>Sign up, sign in and out: server actions behind app/(marketing)/register and login, cookie authentication.</summary>
public static class AuthEndpoints
{
    public sealed record RegisterInput
    {
        [Required, StringLength(80)] public string Name { get; init; } = "";
        [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";

        [Required, StringLength(200, MinimumLength = 10, ErrorMessage = "Use at least 10 characters.")]
        public string Password { get; init; } = "";
    }

    // No email of an account is longer than 254 characters (users.email is varchar(254)), and the per-account limit
    // keeps every email it is given for 15 minutes, so a longer one stops at validation.
    public sealed record LoginInput
    {
        [Required, StringLength(254)] public string Email { get; init; } = "";
        [Required] public string Password { get; init; } = "";
    }

    public sealed record AuthPageProps(string? ReturnUrl);

    private const string WrongCredentials = "That email and password do not match.";

    // Verified against when the email is unknown, so a miss takes as long as a wrong password.
    private static readonly User Nobody = new() { Email = "", Name = "" };
    private static readonly string NobodyHash = new PasswordHasher<User>().HashPassword(Nobody, "not a real password");

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/login", AuthPage);
        app.MapPageLoader("/register", AuthPage);

        app.MapAction("auth.register", async (
            [FromForm] RegisterInput input, TaskBoardDb db, IPasswordHasher<User> hasher, TimeProvider time, HttpContext http) =>
        {
            var email = Normalize(input.Email);
            if (await db.Users.AnyAsync(u => u.Email == email, http.RequestAborted))
            {
                return ServerAction.Invalid("email", "There is already an account with this email. Sign in instead?");
            }

            var user = new User { Email = email, Name = input.Name.Trim(), CreatedAt = time.GetUtcNow() };
            user.PasswordHash = hasher.HashPassword(user, input.Password);
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(http.RequestAborted);
            }
            catch (DbUpdateException)
            {
                // Two sign-ups with the same email at once: the unique index decides.
                return ServerAction.Invalid("email", "There is already an account with this email. Sign in instead?");
            }

            await SignInAsync(http, user);
            return ServerAction.Redirect("/projects");
        })
            .RequireRateLimiting(AuthLimits.SignUp);

        app.MapAction("auth.login", async (
            [FromForm] LoginInput input, [FromForm] string? returnUrl, [FromForm] bool? stay, TaskBoardDb db, IPasswordHasher<User> hasher,
            HttpContext http) =>
        {
            var email = Normalize(input.Email);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, http.RequestAborted);
            if (user is null)
            {
                hasher.VerifyHashedPassword(Nobody, NobodyHash, input.Password);
                return ServerAction.Invalid("password", WrongCredentials);
            }

            var verdict = hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password);
            if (verdict == PasswordVerificationResult.Failed)
            {
                return ServerAction.Invalid("password", WrongCredentials);
            }

            if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
            {
                user.PasswordHash = hasher.HashPassword(user, input.Password);
                await db.SaveChangesAsync(http.RequestAborted);
            }

            await SignInAsync(http, user);
            // The landing page's form signs in where it is: the client router loads that page again for the signed-in
            // user, and a post without JavaScript goes back to it.
            return stay is true ? ServerAction.Ok() : ServerAction.Redirect(Page.IsLocalUrl(returnUrl) ? returnUrl : "/projects");
        })
            .RequireRateLimiting(AuthLimits.SignIn)
            .AddEndpointFilter(AuthLimits.PerAccount);

        app.MapAction("auth.logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return ServerAction.Redirect("/");
        });
    }

    /// <summary>Signed in already: go where the visitor was headed. Otherwise the form, which posts back the return URL.</summary>
    private static PageResult<AuthPageProps> AuthPage(string? returnUrl, ClaimsPrincipal user)
    {
        var target = Page.IsLocalUrl(returnUrl) ? returnUrl : null;
        return SignedInUser.From(user) is null
            ? Page.Props(new AuthPageProps(target))
            : Page.LocalRedirect(target ?? "/projects");
    }

    internal static string Normalize(string email) => email.Trim().ToLowerInvariant();

    private static Task SignInAsync(HttpContext http, User user) =>
        http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new SignedInUser(user.Id, user.Name, user.Email).ToPrincipal(CookieAuthenticationDefaults.AuthenticationScheme));
}
