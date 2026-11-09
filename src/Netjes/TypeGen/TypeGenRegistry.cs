namespace Netjes.TypeGen;

/// <summary>
/// Extra exports for the generated TypeScript, registered as the app is mapped: each becomes
/// <c>export const group = { key: { slot: Schema } }</c>.
/// </summary>
internal sealed class TypeGenRegistry
{
    /// <summary>
    /// What a module cannot declare with <c>export const</c>: JavaScript's reserved words, those of strict mode, which a
    /// module is in, <c>await</c>, and <c>arguments</c> and <c>eval</c>, which strict mode does not let a declaration take.
    /// </summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do", "else", "enum", "export",
        "extends", "false", "finally", "for", "function", "if", "import", "in", "instanceof", "new", "null", "return", "super",
        "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while", "with",
        "implements", "interface", "let", "package", "private", "protected", "public", "static", "yield",
        "await", "arguments", "eval",
    };

    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, Group> _groups = new(StringComparer.Ordinal);

    private sealed record Group(string? Owner, SortedDictionary<string, IReadOnlyList<KeyValuePair<string, Type>>> Entries);

    /// <param name="group">
    /// The export's name, e.g. <c>blocks</c>: an identifier of ASCII letters, digits, <c>_</c> and <c>$</c>, and no reserved
    /// word or name the file takes already (a page package's file takes more).
    /// </param>
    /// <param name="key">The entry's key, e.g. an action name.</param>
    /// <param name="slots">Named types for the entry, e.g. <c>input</c> and <c>result</c>.</param>
    /// <param name="owner">The npm name of the page package that adds the export; null for the app's own.</param>
    public void Add(string group, string key, IReadOnlyDictionary<string, Type> slots, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(slots);
        if (!IsIdentifier(group))
        {
            throw new ArgumentException(
                $"'{group}' cannot be the name of a TypeScript export, which the generated file declares as `export const {group}`: " +
                "a name is ASCII letters, digits, '_' and '$', and does not start with a digit.",
                nameof(group));
        }

        if (ReservedWords.Contains(group))
        {
            throw new ArgumentException($"'{group}' cannot be the name of a TypeScript export: it is a reserved word in a JavaScript module.", nameof(group));
        }

        // A page package's export is in the app's file and in the package's, which takes more names.
        if ((owner is null ? TypeScriptGenerator.ReservedExports : TypeScriptGenerator.PackageReservedExports).Contains(group))
        {
            throw new ArgumentException(
                $"'{group}' cannot be the name of a TypeScript export: {(owner is null ? "the app's" : "a page package's")} generated file " +
                "declares, imports or uses it already.",
                nameof(group));
        }

        // `{ "__proto__": ... }` sets the prototype of the object literal, quoted or not, where every other key is its own.
        const string Prototype = "__proto__";
        if (key == Prototype)
        {
            throw new ArgumentException(
                $"The TypeScript export '{group}' cannot have an entry '{key}': an object literal takes that key for its prototype.", nameof(key));
        }

        foreach (var (name, type) in slots)
        {
            if (name == Prototype)
            {
                throw new ArgumentException(
                    $"The entry '{key}' of the TypeScript export '{group}' cannot have a slot '{name}': an object literal takes that key for its prototype.",
                    nameof(slots));
            }

            if (type is null)
            {
                throw new ArgumentException($"The slot '{name}' of '{key}' in the TypeScript export '{group}' has no type.", nameof(slots));
            }

            if (type.ContainsGenericParameters || type.IsByRef || type.IsPointer || type.IsFunctionPointer || type == typeof(void))
            {
                throw new ArgumentException(
                    $"The slot '{name}' of '{key}' in the TypeScript export '{group}' has the type {type}, which no value has: give it a " +
                    "closed type that is no by-ref type, pointer or void.",
                    nameof(slots));
            }
        }

        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out var entries))
            {
                _groups[group] = entries = new Group(owner, new SortedDictionary<string, IReadOnlyList<KeyValuePair<string, Type>>>(StringComparer.Ordinal));
            }

            if (entries.Owner != owner)
            {
                var who = Who(owner);
                throw new InvalidOperationException(
                    $"{char.ToUpperInvariant(who[0])}{who[1..]} cannot add '{key}' to the TypeScript export '{group}', which {Who(entries.Owner)} added. " +
                    "Give the export a name of its own.");
            }

            if (entries.Entries.ContainsKey(key))
            {
                throw new InvalidOperationException(
                    $"The TypeScript export '{group}' has an entry '{key}' already, which {Who(entries.Owner)} added. Give each entry a key of its own.");
            }

            // A copy in ordinal order: the file is the same in every process, whatever order the dictionary keeps.
            entries.Entries[key] = [.. slots.OrderBy(s => s.Key, StringComparer.Ordinal)];
        }
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');

    private static string Who(string? owner) => owner is null ? "the app" : $"'{owner}'";

    /// <summary>The groups in ordinal order, each with its owner (null for the app's) and its entries by key.</summary>
    public IReadOnlyList<(string Group, string? Owner, IReadOnlyList<(string Key, IReadOnlyList<KeyValuePair<string, Type>> Slots)> Entries)> Snapshot()
    {
        lock (_gate)
        {
            return [.. _groups.Select(g => (g.Key, g.Value.Owner, (IReadOnlyList<(string, IReadOnlyList<KeyValuePair<string, Type>>)>)[.. g.Value.Entries.Select(e => (e.Key, e.Value))]))];
        }
    }
}
