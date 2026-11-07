namespace Blog.Features.Posts;

/// <summary>Loaders for the home page, the post index (/blog?tag=) and post pages.</summary>
public static class PostPages
{
    public sealed record PostSummary(string Slug, string Title, string Summary, IReadOnlyList<string> Tags, DateOnly PublishedOn);

    public sealed record HomeProps(IReadOnlyList<PostSummary> Latest);

    public sealed record BlogIndexProps(string? Tag, IReadOnlyList<string> Tags, IReadOnlyList<PostSummary> Posts);

    public sealed record PostDetail(
        string Slug, string Title, IReadOnlyList<string> Body, IReadOnlyList<string> Tags, DateOnly PublishedOn);

    public sealed record PostProps(PostDetail Post, Deferred<IReadOnlyList<PostSummary>> Related);

    public static void MapPostPages(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/", (PostStore store) =>
            Page.Props(new HomeProps(store.All.OrderByDescending(p => p.PublishedOn).Take(3).Select(Summarize).ToList())));

        // The title depends on the tag, so the loader sends it; the root layout's template adds the site name.
        // The tag comes from the query string (/blog?tag=react), which netjes export does not send: the exported file
        // lists every post, a static host serves it for every tag, and the export warns about this loader
        // (Netjes.Export.QueryString). The Blog keeps the filter as its example of a loader that reads the query string.
        // A site that exports its tag pages makes the tag a route parameter (/blog/tags/[tag]) with .StaticParams(...).
        app.MapPageLoader("/blog", (string? tag, PostStore store) =>
            Page.Props(new BlogIndexProps(
                    tag,
                    store.All.SelectMany(p => p.Tags).Distinct().Order().ToList(),
                    store.All
                        .Where(p => tag is null || p.Tags.Contains(tag))
                        .OrderByDescending(p => p.PublishedOn)
                        .Select(Summarize)
                        .ToList()))
                .WithMetadata(new PageMetadata { Title = tag is null ? "Blog" : $"Posts tagged {tag}" }));

        // Output-cached for 30 seconds outside the Development environment, like incremental static regeneration, but
        // here only the payloads of client navigations: the Blog's Content-Security-Policy uses a nonce (Program.cs),
        // and a document with a nonce is never stored (Netjes logs a warning once), so every full page load is
        // rendered. A hash-based policy would let documents be stored, but no hash covers the scripts that stream the
        // Page.Defer values below. The like count is seeded into the page's TanStack Query cache (key ["likes", slug],
        // see LikeButton.tsx) and refreshed on the client. Related posts are deferred: the page streams without them
        // and they fill in when ready.
        app.MapPageLoader("/blog/[slug]", (string slug, PostStore store, HttpContext http) =>
                store.Find(slug) is { } post
                    ? Page.Props(new PostProps(
                            new PostDetail(post.Slug, post.Title, post.Body, post.Tags, post.PublishedOn),
                            Page.Defer(RelatedAsync(store, slug, http.RequestAborted))))
                        .WithQuery(["likes", slug], new PostLikesApi.LikesResponse(slug, store.LikesOf(slug)))
                        .WithMetadata(new PageMetadata
                        {
                            Title = post.Title,
                            Description = post.Summary,
                            // Crawlers expect an absolute image URL, so it is built from the request.
                            OpenGraph = new PageMetadataOpenGraph { Images = [new PageMetadataImage($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/hero.png", 1600, 800, "A soft gradient")] },
                        })
                    : Page.NotFound())
            .CachePage(TimeSpan.FromSeconds(30))
            // The post reads nothing from the query string, so a request with one, such as a tracking link's
            // ?utm_source=..., is rendered for itself and stored nowhere, rather than adding a copy of the post to the
            // cache for each query string a link or a visitor makes up.
            .CacheQueryKeys()
            .StaticParams<PostStore>(store => store.All.Select(post => new { slug = post.Slug }));
    }

    private static async Task<IReadOnlyList<PostSummary>> RelatedAsync(PostStore store, string slug, CancellationToken cancellationToken) =>
        [.. (await store.RelatedAsync(slug, cancellationToken)).Select(Summarize)];

    private static PostSummary Summarize(Post post) =>
        new(post.Slug, post.Title, post.Summary, post.Tags, post.PublishedOn);
}
