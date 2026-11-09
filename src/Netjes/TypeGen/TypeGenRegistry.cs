namespace Netjes.TypeGen;

/// <summary>
/// Extra exports for the generated TypeScript, registered by Netjes features as they are mapped (server actions,
/// layout loaders): each becomes <c>export const group = { key: { slot: Schema } }</c>.
/// </summary>
internal sealed class TypeGenRegistry
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, SortedDictionary<string, IReadOnlyList<KeyValuePair<string, Type>>>> _groups = new(StringComparer.Ordinal);

    /// <param name="group">A TypeScript identifier, e.g. <c>actions</c>; must not be one of the built-in exports.</param>
    /// <param name="key">The entry's key, e.g. an action name.</param>
    /// <param name="slots">Named types for the entry, e.g. <c>input</c> and <c>result</c>.</param>
    public void Add(string group, string key, IReadOnlyDictionary<string, Type> slots)
    {
        if (TypeScriptGenerator.ReservedExports.Contains(group))
        {
            throw new ArgumentException($"'{group}' is a built-in export of the generated file.", nameof(group));
        }

        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out var entries))
            {
                _groups[group] = entries = new SortedDictionary<string, IReadOnlyList<KeyValuePair<string, Type>>>(StringComparer.Ordinal);
            }

            // A copy in ordinal order: the file is the same in every process, whatever order the dictionary keeps.
            entries[key] = [.. slots.OrderBy(s => s.Key, StringComparer.Ordinal)];
        }
    }

    public IReadOnlyList<(string Group, IReadOnlyList<(string Key, IReadOnlyList<KeyValuePair<string, Type>> Slots)> Entries)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _groups.Select(g => (g.Key, (IReadOnlyList<(string, IReadOnlyList<KeyValuePair<string, Type>>)>)[.. g.Value.Select(e => (e.Key, e.Value))]))];
        }
    }
}
