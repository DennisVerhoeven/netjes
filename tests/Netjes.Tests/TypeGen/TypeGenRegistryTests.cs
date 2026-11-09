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

    [Theory]
    [InlineData("@acme/blocks", "@acme/forms", "'@acme/forms' cannot add 'quote' to the TypeScript export 'blocks', which '@acme/blocks' added.")]
    [InlineData("@acme/blocks", null, "The app cannot add 'quote' to the TypeScript export 'blocks', which '@acme/blocks' added.")]
    [InlineData(null, "@acme/blocks", "'@acme/blocks' cannot add 'quote' to the TypeScript export 'blocks', which the app added.")]
    public void ASecondOwnerOfAGroup_Throws(string? first, string? second, string message)
    {
        var registry = new TypeGenRegistry();
        registry.Add("blocks", "hero", Props(typeof(Hero)), owner: first);

        var error = Assert.Throws<InvalidOperationException>(() => registry.Add("blocks", "quote", Props(typeof(Hero)), owner: second));

        Assert.Equal(message + " Give the export a name of its own.", error.Message);
        Assert.Equal("hero", Assert.Single(Assert.Single(registry.Snapshot()).Entries).Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1blocks")]
    [InlineData("my-blocks")]
    [InlineData("blocks.hero")]
    [InlineData("two words")]
    [InlineData("bl\u00f6cks")]
    [InlineData("x = 1; alert(1); const y")]
    public void AGroupThatIsNoIdentifier_Throws(string group)
    {
        var registry = new TypeGenRegistry();

        var error = Assert.Throws<ArgumentException>(() => registry.Add(group, "hero", Props(typeof(Hero)), owner: "@acme/blocks"));

        Assert.Equal("group", error.ParamName);
        Assert.StartsWith(
            $"'{group}' cannot be the name of a TypeScript export, which the generated file declares as `export const {group}`: " +
            "a name is ASCII letters, digits, '_' and '$', and does not start with a digit.",
            error.Message);
        Assert.Empty(registry.Snapshot());
    }

    /// <summary>
    /// Words a module cannot declare with <c>export const</c>: JavaScript's reserved words, those of strict mode, which
    /// a module is in, <c>await</c>, and <c>arguments</c> and <c>eval</c>, which strict mode does not let a declaration take.
    /// </summary>
    [Theory]
    [InlineData("class")]
    [InlineData("default")]
    [InlineData("delete")]
    [InlineData("enum")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("await")]
    [InlineData("yield")]
    [InlineData("let")]
    [InlineData("static")]
    [InlineData("implements")]
    [InlineData("package")]
    [InlineData("arguments")]
    [InlineData("eval")]
    public void AGroupNamedLikeAReservedWord_Throws(string group)
    {
        var registry = new TypeGenRegistry();

        var error = Assert.Throws<ArgumentException>(() => registry.Add(group, "hero", Props(typeof(Hero)), owner: "@acme/blocks"));

        Assert.Equal("group", error.ParamName);
        Assert.StartsWith($"'{group}' cannot be the name of a TypeScript export: it is a reserved word in a JavaScript module.", error.Message);
        Assert.Empty(registry.Snapshot());
    }
}
