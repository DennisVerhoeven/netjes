namespace Blog;

/// <summary>The shared props every page and layout gets: the site name and the main navigation.</summary>
public sealed record SiteProps(string SiteName, IReadOnlyList<NavLink> Navigation);

public sealed record NavLink(string Href, string Label);
