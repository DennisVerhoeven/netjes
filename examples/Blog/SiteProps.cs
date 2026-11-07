namespace Blog;

/// <summary>The shared props every page and layout gets: the site name and the main navigation.</summary>
public sealed record SiteProps(string SiteName, IReadOnlyList<NavLink> Navigation);

/// <summary>A link of the main navigation, to one of the Blog's pages: the props parse checks it against them.</summary>
public sealed record NavLink(Href Href, string Label);
