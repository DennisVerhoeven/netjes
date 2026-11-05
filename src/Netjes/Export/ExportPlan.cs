namespace Netjes.Export;

/// <summary>
/// What <c>netjes export</c> writes, for the endpoints it requests: <c>MapSitemap</c> lists these pages instead of the
/// route table's, so the sitemap names exactly the pages there are files for.
/// </summary>
internal sealed class ExportPlan
{
    private volatile IReadOnlyList<(string Url, string Pattern)>? _pages;

    /// <summary>
    /// The URL and page pattern of each page the export writes, in the order it writes them. Set once the plan has no
    /// problems, before the export's first request; null outside an export, and in an export that cannot run.
    /// </summary>
    public IReadOnlyList<(string Url, string Pattern)>? Pages
    {
        get => _pages;
        set => _pages = value;
    }
}
