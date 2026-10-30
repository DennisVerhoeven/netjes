namespace Blog.Features.Docs;

/// <summary>The docs section, on the app router: app/docs/layout.tsx and app/docs/[...path]/page.tsx.</summary>
public static class DocsPages
{
    public sealed record DocLink(string Path, string Title);

    public sealed record DocsLayoutProps(IReadOnlyList<DocLink> Toc);

    public sealed record DocProps(Doc Doc);

    public static void MapDocsPages(this IEndpointRouteBuilder app)
    {
        // Runs once per visit to /docs: navigating between docs pages reuses the table of contents.
        app.MapLayoutLoader("/docs", (DocsStore docs) =>
            Page.Props(new DocsLayoutProps([.. docs.All.Select(d => new DocLink(d.Path, d.Title))])));

        // A catch-all route value arrives as the remaining path, e.g. "getting-started/routing". An unknown path
        // renders app/docs/not-found.tsx inside the docs layout.
        app.MapPageLoader("/docs/[...path]", (string path, DocsStore docs) =>
            docs.Find(path) is { } doc
                ? Page.Props(new DocProps(doc)).WithMetadata(new PageMetadata { Title = doc.Title, Description = doc.Blocks.FirstOrDefault()?.Text })
                : Page.NotFound())
            // For `netjes export`: every doc, like generateStaticParams. Catch-all values are the path's segments.
            .StaticParams<DocsStore>(docs => docs.All.Select(d => new { path = d.Path.Split('/') }));

        app.MapGet("/docs", () => Results.Redirect("/docs/introduction"));
    }
}
