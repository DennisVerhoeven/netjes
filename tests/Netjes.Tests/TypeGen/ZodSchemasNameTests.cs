using System.Text.Json;
using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

/// <summary>The names of the schemas: a type's own, unless it carries <see cref="TypeScriptNameAttribute"/>.</summary>
public class ZodSchemasNameTests
{
    public sealed record AuditFilters(AuditFilters.Values Applied)
    {
        public sealed record Values(string? Actor);
    }

    public sealed record NamedAuditFilters(NamedAuditFilters.Values Applied)
    {
        [TypeScriptName("NamedAuditFilterValues")]
        public sealed record Values(string? Actor);
    }

    [TypeScriptName("Level")]
    public enum Severity
    {
        Low,
        High,
    }

    public sealed record WithSeverity(Severity Severity);

    public sealed record Level(string Label);

    public sealed record UsesBoth(Level Plain, Severity Named);

    [TypeScriptName("Shared")]
    public sealed record FirstOfTwo(string A);

    [TypeScriptName("Shared")]
    public sealed record SecondOfTwo(string B);

    public sealed record UsesTwoNamedShared(FirstOfTwo First, SecondOfTwo Second);

    [TypeScriptName("pages")]
    public sealed record NamedLikeAnExport(string A);

    [TypeScriptName("my-name")]
    public sealed record NamedLikeNoIdentifier(string A);

    [TypeScriptName("class")]
    public sealed record NamedLikeAReservedWord(string A);

    private static (string Declarations, string Reference) Generate(Type type, IReadOnlySet<string>? reserved = null)
    {
        var schemas = new ZodSchemas(new JsonSerializerOptions(JsonSerializerDefaults.Web), reserved);
        schemas.Add(type);
        return (schemas.Declarations(), schemas.Reference(type));
    }

    [Fact]
    public void ANestedRecord_WithoutTheAttribute_KeepsItsOwnName()
    {
        var (declarations, _) = Generate(typeof(AuditFilters));

        Assert.Contains("export const Values = z.object({", declarations);
        Assert.Contains("applied: Values,", declarations);
    }

    [Fact]
    public void ANestedRecord_WithTheAttribute_TakesTheNameItGives()
    {
        var (declarations, _) = Generate(typeof(NamedAuditFilters));

        Assert.Contains("export const NamedAuditFilterValues = z.object({\n  actor: z.string().nullable(),\n});", declarations);
        Assert.Contains("export type NamedAuditFilterValues = z.infer<typeof NamedAuditFilterValues>;", declarations);
        Assert.Contains("applied: NamedAuditFilterValues,", declarations);
        Assert.DoesNotContain("export const Values", declarations);
    }

    [Fact]
    public void AnEnum_WithTheAttribute_TakesTheNameItGives()
    {
        var (declarations, _) = Generate(typeof(WithSeverity));

        Assert.Contains("export const Level = ", declarations);
        Assert.Contains("export type Level = z.infer<typeof Level>;", declarations);
        Assert.Contains("severity: Level,", declarations);
    }

    [Fact]
    public void ANameThatTheAttributeGives_IsKept_AndAnotherTypeOfThatNameYields()
    {
        var (declarations, _) = Generate(typeof(UsesBoth));

        Assert.Contains("named: Level,", declarations);
        Assert.Contains("export const Level = ", declarations);
        Assert.Contains("plain: ZodSchemasNameTestsLevel,", declarations);
        Assert.Contains("export const ZodSchemasNameTestsLevel = z.object({", declarations);
    }

    [Fact]
    public void TwoTypes_GivenOneNameByTheAttribute_StopTheGenerator_NamingBoth()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Generate(typeof(UsesTwoNamedShared)));

        Assert.Equal(
            $"The TypeScript name 'Shared' is given by [TypeScriptName] to both {typeof(FirstOfTwo).FullName} and {typeof(SecondOfTwo).FullName}. Give each type a name of its own.",
            error.Message);
    }

    [Fact]
    public void ANameOfTheGeneratedFile_GivenByTheAttribute_StopsTheGenerator()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Generate(typeof(NamedLikeAnExport), TypeScriptGenerator.ReservedExports));

        Assert.Equal(
            $"[TypeScriptName(\"pages\")] on {typeof(NamedLikeAnExport).FullName} names something the generated file declares or imports already. Give the type another name.",
            error.Message);
    }

    [Theory]
    [InlineData(typeof(NamedLikeNoIdentifier), "my-name")]
    [InlineData(typeof(NamedLikeAReservedWord), "class")]
    public void ANameThatIsNoTypeScriptIdentifier_GivenByTheAttribute_StopsTheGenerator(Type type, string name)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Generate(type));

        Assert.StartsWith($"[TypeScriptName(\"{name}\")] on {type.FullName} is no name of an export:", error.Message);
    }
}
