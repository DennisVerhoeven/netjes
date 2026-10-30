using System.Text.Json;
using System.Text.Json.Serialization;
using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

/// <summary>Edge cases of the schema generator: each must accept what System.Text.Json writes.</summary>
public class ZodSchemasEdgeCaseTests
{
    public sealed record Href(string Url);

    public sealed record Holder(Href Link);

    public sealed record WithNullableItems(List<string?> Names, Dictionary<string, string?> Labels, List<string> Required, string?[] Array);

    public sealed record WithNumberHandling([property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] int Count, int Plain);

    public sealed class UpperConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString()!;

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUpperInvariant());
    }

    public sealed record WithConverter([property: JsonConverter(typeof(UpperConverter))] string Name);

    public sealed record WithStamp(DateTime At);

    public sealed class WithExtension
    {
        public string Name { get; init; } = "";

        [JsonExtensionData]
        public Dictionary<string, object>? Extra { get; init; }
    }

    [JsonDerivedType(typeof(Dog), "dog")]
    public record Animal(string Name);

    public sealed record Dog(string Name, bool Barks) : Animal(Name);

    [Flags]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum Access { None = 0, Read = 1, Write = 2 }

    public sealed record WithFlags(Access Access);

    public sealed record WithBigNumbers(long Id, ulong Hash, int Small);

    public sealed record Owner(string N);

    [JsonDerivedType(typeof(PinnedComment), "pinned")]
    public record Comment(string Text, List<Comment> Replies);

    public sealed record PinnedComment(string Text, List<Comment> Replies) : Comment(Text, Replies);

    [JsonDerivedType(typeof(Shape), "base")]
    [JsonDerivedType(typeof(Circle), "circle")]
    public class Shape
    {
        public string Name { get; set; } = "";
    }

    public class Circle : Shape
    {
        public int R { get; set; }
    }

    [JsonDerivedType(typeof(Dog2), "dog")]
    [JsonDerivedType(typeof(Puppy), "puppy")]
    public abstract class Pet
    {
        public string Name { get; set; } = "";
    }

    [JsonDerivedType(typeof(Puppy), "puppy")]
    public class Dog2 : Pet;

    public class Puppy : Dog2
    {
        public int Age { get; set; }
    }

    public sealed record Prices(decimal Total, decimal? Discount);

    public sealed class FixedDecimals : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("0.00"));
    }

    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public sealed record Invoice(int Count, decimal Price);

    public sealed record Counters(
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] List<int> Ids,
        [property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] Dictionary<string, long?> Totals,
        List<Owner> Owners);

    public sealed record Measurements(double Mean, float Peak, decimal Cost, int Samples);

    public sealed record Precise([property: JsonNumberHandling(JsonNumberHandling.WriteAsString)] Half Small, Half Plain);

    public sealed record Record(string A);

    public sealed record FormData(string B);

    public sealed record SearchParamsInput(string C);

    public sealed record Number(int D);

    public sealed record UsesGlobals(Record Record, FormData Form, SearchParamsInput Search, Number Number);

    public sealed record Later(Deferred<Owner?> Owner, Deferred<string?> Note, Deferred<int?> Count, Deferred<Owner> Required);

    [JsonPolymorphic]
    public class Bare
    {
        public Owner Owner { get; set; } = new("o");
    }

    private static (string Declarations, string Reference) Generate(Type type, Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        configure?.Invoke(options);
        var schemas = new ZodSchemas(options);
        schemas.Add(type);
        return (schemas.Declarations(), schemas.Reference(type));
    }

    [Fact]
    public void TypeNamedLikeAnExportOfTheGeneratedFile_GetsAnotherName()
    {
        var (declarations, _) = Generate(typeof(Holder));

        Assert.DoesNotContain("export const Href =", declarations);
        Assert.Contains("export const ZodSchemasEdgeCaseTestsHref = ", declarations);
        Assert.Contains("link: ZodSchemasEdgeCaseTestsHref", declarations);
    }

    [Fact]
    public void NullableItemsOfListsArraysAndDictionaries_AreNullable()
    {
        var (declarations, _) = Generate(typeof(WithNullableItems));

        Assert.Contains("names: z.array(z.string().nullable())", declarations);
        Assert.Contains("labels: z.record(z.string(), z.string().nullable())", declarations);
        Assert.Contains("required: z.array(z.string())", declarations);
        Assert.Contains("array: z.array(z.string().nullable())", declarations);
    }

    [Fact]
    public void NumbersWrittenAsStrings_AcceptStrings()
    {
        var (declarations, _) = Generate(typeof(WithNumberHandling));

        Assert.Contains("count: z.coerce.number().int()", declarations);
        Assert.Contains("plain: z.number().int()", declarations);
        Assert.Contains("count: z.coerce.number().int()", Generate(typeof(WithNumberHandling), o => o.NumberHandling = JsonNumberHandling.WriteAsString).Declarations.Replace("plain", "count"));
    }

    [Fact]
    public void PropertiesWithCustomConverters_AreUnknown()
    {
        Assert.Contains("name: z.unknown()", Generate(typeof(WithConverter)).Declarations);
    }

    [Fact]
    public void TypesAConverterInTheOptionsWrites_AreUnknown()
    {
        var (declarations, _) = Generate(typeof(WithStamp), o => o.Converters.Add(new EpochConverter()));

        Assert.Contains("at: z.unknown()", declarations);
    }

    [Fact]
    public void ExtensionData_IsNotARequiredKeyButAnyExtraKey()
    {
        var (declarations, _) = Generate(typeof(WithExtension));

        Assert.Contains("export const WithExtension = z.object({\n  name: z.string(),\n}).catchall(z.unknown());", declarations);
    }

    [Fact]
    public void ConcretePolymorphicBase_IsInItsOwnUnion()
    {
        var (declarations, _) = Generate(typeof(Animal));

        Assert.Contains("export const Animal = z.union([Dog.extend({ \"$type\": z.literal(\"dog\") }), z.object({ name: z.string() })]);", declarations);
    }

    [Fact]
    public void PolymorphicWithoutDerivedTypes_IsAPlainObject_WithItsPropertyTypesDeclared()
    {
        var (declarations, reference) = Generate(typeof(Bare));

        Assert.Equal("Bare", reference);
        Assert.Contains("export const Owner = z.object({\n  n: z.string(),\n});", declarations);
        Assert.Contains("export const Bare = z.object({\n  owner: Owner,\n});", declarations);
    }

    [Fact]
    public void ConcretePolymorphicBase_ReferencingItself_ReadsItThroughAGetter()
    {
        var (declarations, _) = Generate(typeof(Comment));

        Assert.Contains(
            "export const Comment = z.union([PinnedComment.extend({ \"$type\": z.literal(\"pinned\") }), " +
            "z.object({ text: z.string(), get replies() { return z.array(Comment); } })]);",
            declarations);
    }

    [Fact]
    public void BaseListedAsItsOwnDerivedType_IsItsOwnObjectWithItsDiscriminator()
    {
        var (declarations, _) = Generate(typeof(Shape));

        Assert.Contains(
            "export const Shape = z.discriminatedUnion(\"$type\", [z.object({ name: z.string() }).extend({ \"$type\": z.literal(\"base\") }), " +
            "Circle.extend({ \"$type\": z.literal(\"circle\") })]);",
            declarations);
    }

    [Fact]
    public void DerivedTypeThatIsPolymorphicItself_IsItsOwnObjectInTheBasesUnion()
    {
        var (declarations, _) = Generate(typeof(Pet));

        // Under Pet, a Dog2 is written with Pet's discriminator and Dog2's properties, not as Dog2's own union.
        Assert.Contains(
            "export const Pet = z.discriminatedUnion(\"$type\", [z.object({ name: z.string() }).extend({ \"$type\": z.literal(\"dog\") }), " +
            "Puppy.extend({ \"$type\": z.literal(\"puppy\") })]);",
            declarations);
    }

    [Fact]
    public void NumbersAConverterInTheOptionsWrites_AreUnknown_EvenWrittenAsStrings()
    {
        var (declarations, _) = Generate(typeof(Prices), o =>
        {
            o.NumberHandling = JsonNumberHandling.WriteAsString;
            o.Converters.Add(new FixedDecimals());
        });

        Assert.Contains("total: z.unknown(),", declarations);
        Assert.Contains("discount: z.unknown().nullable(),", declarations);
        Assert.DoesNotContain("z.coerce.unknown", declarations);
    }

    [Fact]
    public void NumberHandlingOnTheClass_AppliesToItsProperties()
    {
        var (declarations, _) = Generate(typeof(Invoice));

        Assert.Contains("count: z.coerce.number().int(),", declarations);
        Assert.Contains("price: z.coerce.number(),", declarations);
    }

    [Fact]
    public void NumbersInCollections_FollowTheNumberHandling()
    {
        var (declarations, _) = Generate(typeof(Counters));

        Assert.Contains("ids: z.array(z.coerce.number().int()),", declarations);
        Assert.Contains("totals: z.record(z.string(), z.coerce.number().nullable()),", declarations);
        Assert.Contains("owners: z.array(Owner),", declarations);
        // From the options, for every number anywhere, collection items included.
        var (everywhere, reference) = Generate(typeof(List<long>), o => o.NumberHandling = JsonNumberHandling.WriteAsString);
        Assert.Equal("z.array(z.coerce.number())", reference);
        Assert.Equal("", everywhere);
    }

    [Fact]
    public void NamedFloatingPointLiterals_AcceptNaNAndInfinity_AsNumbers()
    {
        var named = Generate(typeof(Measurements), o => o.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals).Declarations;
        var asStrings = Generate(typeof(Measurements), o =>
            o.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.WriteAsString).Declarations;

        const string Literals = "z.enum([\"NaN\", \"Infinity\", \"-Infinity\"]).transform(Number)";
        Assert.Contains($"mean: z.union([z.number(), {Literals}]),", named);
        Assert.Contains($"peak: z.union([z.number(), {Literals}]),", named);
        // Only float and double have them.
        Assert.Contains("cost: z.number(),", named);
        Assert.Contains("samples: z.number().int(),", named);
        Assert.Contains($"mean: z.union([{Literals}, z.coerce.number()]),", asStrings);
        Assert.Contains("cost: z.coerce.number(),", asStrings);
    }

    [Fact]
    public void Half_IsANumber_WrittenAsAStringWhenAsked()
    {
        var (declarations, _) = Generate(typeof(Precise));

        Assert.Contains("small: z.coerce.number(),", declarations);
        Assert.Contains("plain: z.number(),", declarations);
    }

    [Fact]
    public void TypesNamedLikeWhatTheGeneratedFileImportsOrUsesAsGlobals_GetOtherNames()
    {
        var (declarations, _) = Generate(typeof(UsesGlobals));

        // `Record<string, never>`, `FormData`, `SearchParamsInput` and `.transform(Number)` must keep meaning the real ones.
        foreach (var name in new[] { "Record", "FormData", "SearchParamsInput", "Number" })
        {
            Assert.DoesNotContain($"export const {name} =", declarations);
            Assert.Contains($"export const ZodSchemasEdgeCaseTests{name} =", declarations);
        }
    }

    [Fact]
    public void DeferredValues_KeepTheNullabilityOfTheirTypeArgument()
    {
        var (declarations, _) = Generate(typeof(Later));

        Assert.Contains("owner: deferred(Owner.nullable()),", declarations);
        Assert.Contains("note: deferred(z.string().nullable()),", declarations);
        Assert.Contains("count: deferred(z.number().int().nullable()),", declarations);
        Assert.Contains("required: deferred(Owner),", declarations);
    }

    [Fact]
    public void FlagsWrittenAsStrings_AreStrings()
    {
        Assert.Contains("export const Access = z.string();", Generate(typeof(WithFlags)).Declarations);
    }

    [Fact]
    public void SixtyFourBitIntegers_AreNotLimitedToSafeIntegers()
    {
        var (declarations, _) = Generate(typeof(WithBigNumbers));

        Assert.Contains("id: z.number(),", declarations);
        Assert.Contains("hash: z.number(),", declarations);
        Assert.Contains("small: z.number().int(),", declarations);
    }

    private sealed class EpochConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => DateTime.UnixEpoch;

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteNumberValue(0);
    }
}
