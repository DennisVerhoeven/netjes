namespace Netjes.TypeGen;

/// <summary>
/// Extra exports for the generated TypeScript, registered as the app is mapped: each becomes
/// <c>export const group = { key: { slot: Schema } }</c>.
/// </summary>
internal sealed class TypeGenRegistry
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, Group> _groups = new(StringComparer.Ordinal);

    private sealed record Group(string? Owner, SortedDictionary<string, IReadOnlyList<KeyValuePair<string, Type>>> Entries);

    /// <param name="group">A TypeScript identifier, e.g. <c>actions</c>; must not be one of the built-in exports.</param>
    /// <param name="key">The entry's key, e.g. an action name.</param>
    /// <param name="slots">Named types for the entry, e.g. <c>input</c> and <c>result</c>.</param>
    /// <param name="owner">The npm name of the page package that adds the export; null for the app's own.</param>
    public void Add(string group, string key, IReadOnlyDictionary<string, Type> slots, string? owner = null)
    {
        if (TypeScriptGenerator.ReservedExports.Contains(group))
        {
            throw new ArgumentException($"'{group}' is a built-in export of the generated file.", nameof(group));
        }

        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out var entries))
            {
                _groups[group] = entries = new Group(owner, new SortedDictionary<string, IReadOnlyList<KeyValuePair<string, Type>>>(StringComparer.Ordinal));
            }

            // A copy in ordinal order: the file is the same in every process, whatever order the dictionary keeps.
            entries.Entries[key] = [.. slots.OrderBy(s => s.Key, StringComparer.Ordinal)];
        }
    }

    /// <summary>The groups in ordinal order, each with its owner (null for the app's) and its entries by key.</summary>
    public IReadOnlyList<(string Group, string? Owner, IReadOnlyList<(string Key, IReadOnlyList<KeyValuePair<string, Type>> Slots)> Entries)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _groups.Select(g => (g.Key, g.Value.Owner, (IReadOnlyList<(string, IReadOnlyList<KeyValuePair<string, Type>>)>)[.. g.Value.Entries.Select(e => (e.Key, e.Value))]))];
        }
    }
}
