namespace NetjesBench;

public sealed record Item(int Id, string Name, string Summary, IReadOnlyList<string> Description, int PriceCents, IReadOnlyList<string> Tags);

/// <summary>Fifty items, the same on every run, and how long the detail page's slow field takes.</summary>
public sealed class Catalog(IConfiguration configuration)
{
    private static readonly string[] AllTags = ["garden", "kitchen", "tools", "outdoor", "lighting", "storage"];

    public IReadOnlyList<Item> Items { get; } =
    [
        .. Enumerable.Range(1, 50).Select(i => new Item(
            i,
            $"Item {i}",
            $"A short summary of item {i}, long enough to wrap on a narrow screen.",
            [.. Enumerable.Range(1, 4).Select(p => $"Paragraph {p} about item {i}. It describes what the item is for, what it is made of and how it is used, in a sentence or two.")],
            1000 + (i * 137 % 9000),
            [AllTags[i % AllTags.Length], AllTags[(i * 7) % AllTags.Length]])),
    ];

    /// <summary>The detail page's deferred stock lookup (Bench:SlowFieldMs, default 50).</summary>
    public TimeSpan SlowField { get; } = TimeSpan.FromMilliseconds(configuration.GetValue("Bench:SlowFieldMs", 50));

    public Item? Find(int id) => id >= 1 && id <= Items.Count ? Items[id - 1] : null;
}
