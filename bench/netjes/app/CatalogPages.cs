namespace NetjesBench;

/// <summary>The loaders of the benchmark's pages.</summary>
public static class CatalogPages
{
    public sealed record ItemSummary(int Id, string Name, string Summary, int PriceCents, IReadOnlyList<string> Tags);

    public sealed record ItemListProps(IReadOnlyList<ItemSummary> Items);

    public sealed record ItemDetail(int Id, string Name, IReadOnlyList<string> Description, int PriceCents, IReadOnlyList<string> Tags);

    public sealed record Stock(int Available, string Warehouse);

    public sealed record ItemProps(ItemDetail Item, Deferred<Stock> Stock);

    public static void MapCatalogPages(this IEndpointRouteBuilder app)
    {
        // Measured as a document and as a client navigation's data request (X-Netjes-Data).
        app.MapPageLoader("/items", (Catalog catalog) => Page.Props(List(catalog)));

        // The same page, served from the output cache after the first request.
        app.MapPageLoader("/cached", (Catalog catalog) => Page.Props(List(catalog)))
            .CachePage(TimeSpan.FromHours(1));

        // Streams: the page goes out at once, and the stock line follows when its slow lookup is done.
        app.MapPageLoader("/items/[id]", (int id, Catalog catalog, HttpContext http) =>
            catalog.Find(id) is { } item
                ? Page.Props(new ItemProps(
                    new ItemDetail(item.Id, item.Name, item.Description, item.PriceCents, item.Tags),
                    Page.Defer(StockAsync(catalog, item, http.RequestAborted))))
                : Page.NotFound());
    }

    private static ItemListProps List(Catalog catalog) =>
        new([.. catalog.Items.Select(i => new ItemSummary(i.Id, i.Name, i.Summary, i.PriceCents, i.Tags))]);

    private static async Task<Stock> StockAsync(Catalog catalog, Item item, CancellationToken cancellationToken)
    {
        await Task.Delay(catalog.SlowField, cancellationToken);
        return new Stock(item.Id * 3 % 17, "Rotterdam");
    }
}
