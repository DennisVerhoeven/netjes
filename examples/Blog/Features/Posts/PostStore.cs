using System.Collections.Concurrent;

namespace Blog.Features.Posts;

public sealed record Post(
    string Slug,
    string Title,
    string Summary,
    IReadOnlyList<string> Body,
    IReadOnlyList<string> Tags,
    DateOnly PublishedOn);

/// <summary>In-memory posts and like counts; stands in for a database in this example.</summary>
public sealed class PostStore
{
    private readonly ConcurrentDictionary<string, int> _likes = new(StringComparer.Ordinal);

    public IReadOnlyList<Post> All { get; } =
    [
        new(
            "react-pages-on-dotnet",
            "React pages, served by ASP.NET Core",
            "Why Netjes renders React on the server through a Node sidecar while routing, data and auth stay in .NET.",
            [
                "Netjes takes the parts of Next.js that make React apps pleasant (file-based routing, server rendering, client-side navigation) and puts ASP.NET Core in charge of everything else.",
                "Each page.tsx under app/ becomes an endpoint. A C# loader mapped with MapPageLoader supplies its props, the same way getServerSideProps would, and it can use dependency injection, EF Core, authorization policies and output caching like any minimal API.",
                "Rendering React needs a JavaScript engine, so a small Node process sits next to the app. .NET sends it the page payload over loopback HTTP and gets a complete HTML document back.",
            ],
            ["dotnet", "react"],
            new DateOnly(2026, 10, 20)),
        new(
            "file-based-routing",
            "File-based routing without a Node server",
            "How app/blog/[slug]/page.tsx becomes the ASP.NET Core route /blog/{slug}, and why static segments still win.",
            [
                "The route table is built in C# from the folders in app/: page files, [param] segments, [...catchAll] and [[...optional]] segments, (group) folders and nested layout files.",
                "Every page turns into a regular endpoint with a regular route template, so ASP.NET Core's matcher decides precedence: /blog/new beats /blog/{slug}, which beats /blog/{**rest}.",
                "In development the app directory is watched. Add a file and it is routable on the next request, no restart needed.",
            ],
            ["routing", "dotnet"],
            new DateOnly(2026, 10, 24)),
        new(
            "forms-that-work-without-javascript",
            "Forms that work without JavaScript",
            "MapPageAction plus the Form component: progressive enhancement with antiforgery built in.",
            [
                "A Form posts to the page's own URL, where a MapPageAction handler binds the fields with [FromForm].",
                "Return Page.Redirect to finish (303 See Other, the post/redirect/get pattern) or Page.Props with a 422 status to show validation errors.",
                "With JavaScript the router submits with fetch and swaps in the result without a reload. Without it the browser does a normal form post and gets the same result as HTML.",
            ],
            ["forms", "react"],
            new DateOnly(2026, 10, 27)),
    ];

    public Post? Find(string slug) => All.FirstOrDefault(p => p.Slug == slug);

    public int LikesOf(string slug) => _likes.GetValueOrDefault(slug);

    /// <summary>Posts sharing a tag with this one. Deliberately slow, standing in for a recommendation service.</summary>
    public async Task<IReadOnlyList<Post>> RelatedAsync(string slug, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
        var post = Find(slug);
        return post is null ? [] : [.. All.Where(p => p.Slug != slug && p.Tags.Intersect(post.Tags).Any())];
    }

    public int Like(string slug) => _likes.AddOrUpdate(slug, 1, (_, count) => count + 1);
}
