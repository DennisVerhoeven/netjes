namespace Blog.Features.Docs;

/// <summary>Serialized as "paragraph" and "code" (Program.cs adds a camelCase string enum converter).</summary>
public enum DocBlockKind { Paragraph, Code }

public sealed record DocBlock(DocBlockKind Kind, string Text);

public sealed record Doc(string Path, string Title, IReadOnlyList<DocBlock> Blocks);

/// <summary>Documentation served through the catch-all page app/docs/[...path]/page.tsx.</summary>
public sealed class DocsStore
{
    private static DocBlock P(string text) => new(DocBlockKind.Paragraph, text);

    private static DocBlock Code(string text) => new(DocBlockKind.Code, text);

    public IReadOnlyList<Doc> All { get; } =
    [
        new("introduction", "Introduction",
        [
            P("Netjes serves React pages from ASP.NET Core. Pages live in app/ as page.tsx files; routing, data loading, forms and caching are C#."),
            Code("""
                builder.Services.AddNetjes();
                var app = builder.Build();
                app.UseNetjes();
                app.MapPageLoader("/blog/[slug]", (string slug, PostStore posts) =>
                    posts.Find(slug) is { } post ? Page.Props(new { post }) : Page.NotFound());
                app.MapPages();
                """),
        ]),
        new("getting-started/routing", "Routing",
        [
            P("Folders map to URLs: app/page.tsx is /, app/blog/[slug]/page.tsx is /blog/{slug}, app/docs/[...path]/page.tsx matches any depth below /docs."),
            P("Folders in parentheses group pages without adding a URL segment. layout.tsx wraps every page below its folder. Folders starting with an underscore are private, and other files in a folder are colocated code, not pages."),
        ]),
        new("getting-started/data-loading", "Data loading",
        [
            P("MapPageLoader binds like a minimal API handler: route values, query strings and services. Return Page.Props, Page.NotFound or Page.Redirect."),
            Code("""
                app.MapPageLoader("/blog", (string? tag, PostStore store) =>
                    Page.Props(new { posts = store.ByTag(tag) }));
                """),
            P("Pages receive the props directly. Wrap the component in definePage(schema, Page) to validate them with zod or any Standard Schema library."),
        ]),
        new("guides/forms", "Forms and actions",
        [
            P("MapPageAction handles POSTs to a page URL. The Form component submits with fetch when JavaScript is available and as a regular form post when it is not."),
        ]),
        new("guides/deployment", "Deployment",
        [
            P("npm run build writes dist/client (browser assets and a manifest) and dist/server (the server bundle and sidecar). dotnet publish runs it and ships dist/ with the app."),
            P("The production image needs the ASP.NET Core runtime and Node.js; see the Dockerfile in this example."),
        ]),
    ];

    public Doc? Find(string path) => All.FirstOrDefault(d => d.Path == path.Trim('/'));
}
