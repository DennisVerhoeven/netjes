using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Netjes.Routing;

namespace Netjes.Examples.HelpCenter;

/// <summary>
/// How an app takes the help center: <c>builder.Services.AddHelpCenter()</c>, then <c>app.MapHelpCenter("/help")</c>
/// before <c>app.MapPages()</c>. The package's loaders, page action, server action and endpoint map through its mount,
/// so their URLs follow the prefix the app picks.
/// </summary>
public static class HelpCenterEndpoints
{
    public const int MaxCommentLength = 500;

    public const int MaxMessageLength = 2000;

    /// <summary>The longest email address there is (RFC 5321's limit on a path, less its angle brackets).</summary>
    public const int MaxEmailLength = 254;

    public sealed record ArticleLink(string Slug, string Title, string Summary);

    public sealed record HomeProps(IReadOnlyList<ArticleLink> Articles);

    /// <summary>The <c>(center)</c> layout's list of articles, next to every page in that group.</summary>
    public sealed record CenterProps(IReadOnlyList<ArticleLink> Articles);

    public sealed record FeedbackForm
    {
        public string? Comment { get; init; }
    }

    public sealed record ArticleProps(
        HelpArticle Article,
        HelpVotes Votes,
        int FeedbackCount,
        bool FeedbackSent,
        IReadOnlyDictionary<string, string>? Errors = null,
        FeedbackForm? Values = null);

    public sealed record ContactProps(string Email, string Hours);

    public sealed record VoteInput(string Slug, bool Helpful);

    public sealed record MessageRequest(string Email, string Message);

    public sealed record MessageReceipt(int Number);

    /// <summary>The store the package's pages read and write.</summary>
    public static IServiceCollection AddHelpCenter(this IServiceCollection services)
    {
        services.TryAddSingleton<HelpCenterStore>();
        return services;
    }

    /// <summary>Mounts the help center at <paramref name="prefix"/>, in folder syntax: <c>/help</c>, <c>/(marketing)/help</c>.</summary>
    /// <returns>The mount, for the app's conventions and overrides.</returns>
    public static PagePackageBuilder MapHelpCenter(this IEndpointRouteBuilder app, string prefix = "/help")
    {
        var help = app.MapPagePackage(HelpCenterPackage.Package, prefix);
        var mount = help.Mount;

        help.MapPageLoader("/", (HelpCenterStore store) => Page.Props(new HomeProps(Links(store))));

        help.MapLayoutLoader("app/(center)", (HelpCenterStore store) => Page.Props(new CenterProps(Links(store))));

        help.MapPageLoader("/articles/[slug]", (string slug, string? feedback, HelpCenterStore store) =>
            store.Find(slug) is { } article
                ? Page.Props(Article(store, article, feedback == "sent")).WithMetadata(new PageMetadata { Title = $"{article.Title} · Help center" })
                : Page.NotFound());

        // A note about the article, from a <Form method="post"> on its page: it works with and without JavaScript.
        help.MapPageAction("/articles/[slug]", (string slug, [FromForm] FeedbackForm form, HelpCenterStore store, HttpContext http) =>
        {
            if (store.Find(slug) is not { } article)
            {
                return Page.NotFound();
            }

            var comment = form.Comment?.Trim();
            if (string.IsNullOrEmpty(comment) || comment.Length > MaxCommentLength)
            {
                var error = string.IsNullOrEmpty(comment) ? "Tell us what was missing." : $"Keep it under {MaxCommentLength} characters.";
                return Page.Props(Article(store, article, sent: false) with { Errors = new Dictionary<string, string> { ["comment"] = error }, Values = form })
                    .WithStatus(StatusCodes.Status422UnprocessableEntity);
            }

            store.AddFeedback(slug, comment);
            // The article's URL wherever the app mounted the package, with the prefix's values from this request.
            return Page.Redirect(mount.Url($"/articles/{Uri.EscapeDataString(slug)}?feedback=sent", http.Request.RouteValues));
        });

        help.MapPageLoader("/contact", () => Page.Props(new ContactProps("help@example.com", "Monday to Friday, 9:00 to 17:00 UTC")));

        // A server action shares one namespace with the app's and every other package's, so its name starts with the package's.
        help.MapAction("help.vote", (VoteInput input, HelpCenterStore store) =>
            store.Find(input.Slug) is null
                ? ServerAction.Invalid("slug", "There is no such article.")
                : ServerAction.Ok(store.Vote(input.Slug, input.Helpful)));

        // A JSON endpoint below the mount, /help/messages where the package is mounted at /help. Its body binds from JSON
        // only, which keeps cross-site form posts out.
        help.MapPost("/messages", Results<Ok<MessageReceipt>, BadRequest> (MessageRequest request, HelpCenterStore store) =>
        {
            var email = request.Email?.Trim();
            var message = request.Message?.Trim();
            return string.IsNullOrEmpty(email) || string.IsNullOrEmpty(message) || email.Length > MaxEmailLength || message.Length > MaxMessageLength
                ? TypedResults.BadRequest()
                : TypedResults.Ok(new MessageReceipt(store.AddMessage(email, message)));
        });

        return help;
    }

    private static IReadOnlyList<ArticleLink> Links(HelpCenterStore store) =>
        [.. store.Articles.Select(a => new ArticleLink(a.Slug, a.Title, a.Summary))];

    private static ArticleProps Article(HelpCenterStore store, HelpArticle article, bool sent) =>
        new(article, store.VotesOf(article.Slug), store.FeedbackCount(article.Slug), sent);
}
