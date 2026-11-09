using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

/// <summary>
/// What <see cref="TypeGenRegistry"/> refuses: each export it holds becomes <c>export const group = { key: { slot: Schema } }</c>
/// in a generated file, so a name that is no identifier, or a second entry, would break the file or change it silently.
/// </summary>
public class TypeGenRegistryTests
{
    public sealed record Hero(string Title);

    private static Dictionary<string, Type> Props(Type type) => new() { ["props"] = type };

    [Fact]
    public void ASecondEntryOfOneKeyInAGroup_Throws_WhereItOverwroteTheFirst()
    {
        var registry = new TypeGenRegistry();
        registry.Add("blocks", "hero", Props(typeof(Hero)), owner: "@acme/blocks");

        var error = Assert.Throws<InvalidOperationException>(() => registry.Add("blocks", "hero", Props(typeof(string)), owner: "@acme/blocks"));

        Assert.Equal("The TypeScript export 'blocks' has an entry 'hero' already, which '@acme/blocks' added. Give each entry a key of its own.", error.Message);
        var entry = Assert.Single(Assert.Single(registry.Snapshot()).Entries);
        Assert.Equal(typeof(Hero), Assert.Single(entry.Slots).Value);
    }
}
