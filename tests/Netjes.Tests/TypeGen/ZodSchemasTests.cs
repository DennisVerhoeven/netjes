using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Netjes.TypeGen;

namespace Netjes.Tests.TypeGen;

public class ZodSchemasTests
{
    public enum Status { Draft, InReview }

    public sealed record Author(string Name, string? Bio);

    public sealed record Post(
        string Title,
        string? Subtitle,
        int Views,
        double? Rating,
        bool Published,
        DateOnly PublishedOn,
        DateTimeOffset UpdatedAt,
        DateTime CreatedAt,
        TimeOnly At,
        Guid Id,
        Uri Link,
        Status Status,
        Author Author,
        IReadOnlyList<string> Tags,
        Author[] Coauthors,
        Dictionary<string, int> Counts,
        byte[] Thumbnail,
        object? Extra,
        JsonElement Raw,
        [property: JsonPropertyName("renamed")] string Original,
        [property: JsonIgnore] string Hidden,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Maybe);

    public sealed record Category(string Name, IReadOnlyList<Category> Children, Category? Parent);

    [JsonDerivedType(typeof(Circle), "circle")]
    [JsonDerivedType(typeof(Square), "square")]
    public abstract record Shape(string Color);

    public sealed record Circle(string Color, double Radius) : Shape(Color);

    public sealed record Square(string Color, double Side) : Shape(Color);

    public sealed record Paged<T>(IReadOnlyList<T> Items, int Total);

    public static class Blog
    {
        public sealed record Item(string Title);
    }

    public static class Shop
    {
        public sealed record Item(decimal Price);
    }

    private static JsonSerializerOptions Web(Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        configure?.Invoke(options);
        return options;
    }

    private static (string Declarations, string Reference) Generate(Type type, JsonSerializerOptions? options = null)
    {
        var schemas = new ZodSchemas(options ?? Web());
        schemas.Add(type);
        return (schemas.Declarations(), schemas.Reference(type));
    }

    [Fact]
    public void Object_MapsEveryPropertyAsSystemTextJsonWritesIt()
    {
        var (declarations, reference) = Generate(typeof(Post));

        Assert.Equal("Post", reference);
        Assert.Contains(
            """
            export const Post = z.object({
              title: z.string(),
              subtitle: z.string().nullable(),
              views: z.number().int(),
              rating: z.number().nullable(),
              published: z.boolean(),
              publishedOn: z.iso.date(),
              updatedAt: z.iso.datetime({ offset: true, local: true }),
              createdAt: z.iso.datetime({ offset: true, local: true }),
              at: z.iso.time(),
              id: z.guid(),
              link: z.string(),
              status: Status,
              author: Author,
              tags: z.array(z.string()),
              coauthors: z.array(Author),
              counts: z.record(z.string(), z.number().int()),
              thumbnail: z.base64(),
              extra: z.unknown(),
              raw: z.unknown(),
              renamed: z.string(),
              maybe: z.string().nullable().optional(),
            });
            export type Post = z.infer<typeof Post>;
            """.ReplaceLineEndings("\n"),
            declarations);
    }

    [Fact]
    public void Dependencies_AreDeclaredBeforeTheTypesThatUseThem()
    {
        var (declarations, _) = Generate(typeof(Post));

        Assert.True(declarations.IndexOf("export const Status", StringComparison.Ordinal) < declarations.IndexOf("export const Post", StringComparison.Ordinal));
        Assert.True(declarations.IndexOf("export const Author", StringComparison.Ordinal) < declarations.IndexOf("export const Post", StringComparison.Ordinal));
        Assert.Contains("export const Author = z.object({\n  name: z.string(),\n  bio: z.string().nullable(),\n});", declarations);
    }

    [Fact]
    public void Enum_WithoutStringConverter_IsItsNumbers()
    {
        var (declarations, _) = Generate(typeof(Status));

        Assert.Contains("export const Status = z.union([z.literal(0), z.literal(1)]);", declarations);
    }

    [Fact]
    public void Enum_WithStringConverter_IsTheNamesAsSerialized()
    {
        var (declarations, _) = Generate(typeof(Status), Web(o => o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase))));

        Assert.Contains("""export const Status = z.enum(["draft", "inReview"]);""", declarations);
    }

    [Fact]
    public void GlobalIgnoreWhenWritingNull_MakesNullablePropertiesOptional()
    {
        var (declarations, _) = Generate(typeof(Author), Web(o => o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull));

        Assert.Contains("bio: z.string().nullable().optional(),", declarations);
        Assert.Contains("name: z.string(),", declarations);
    }

    [Fact]
    public void RecursiveType_UsesGettersForTheBackReferences()
    {
        var (declarations, _) = Generate(typeof(Category));

        Assert.Contains(
            """
            export const Category = z.object({
              name: z.string(),
              get children() { return z.array(Category); },
              get parent() { return Category.nullable(); },
            });
            """.ReplaceLineEndings("\n"),
            declarations);
    }

    [Fact]
    public void Polymorphism_IsADiscriminatedUnionOnTheTypeDiscriminator()
    {
        var (declarations, reference) = Generate(typeof(Shape));

        Assert.Equal("Shape", reference);
        Assert.Contains(
            """export const Shape = z.discriminatedUnion("$type", [Circle.extend({ "$type": z.literal("circle") }), Square.extend({ "$type": z.literal("square") })]);""",
            declarations);
        Assert.Contains("export const Circle = z.object({\n  radius: z.number(),\n  color: z.string(),\n});", declarations);
    }

    [Fact]
    public void AnonymousType_IsInlined_WithReferencePropertiesNullable()
    {
        // Anonymous types are generic over their property types, so their nullable annotations do not survive
        // compilation: reference-typed properties may be null as far as the contract knows.
        var value = new { Title = "a", Author = new Author("n", null), Count = (int?)null };

        var (declarations, reference) = Generate(value.GetType());

        Assert.Equal("z.object({ title: z.string().nullable(), author: Author.nullable(), count: z.number().int().nullable() })", reference);
        Assert.DoesNotContain("AnonymousType", declarations);
    }

    [Fact]
    public void GenericType_IsNamedAfterItsArguments()
    {
        var (declarations, reference) = Generate(typeof(Paged<Author>));

        Assert.Equal("PagedOfAuthor", reference);
        Assert.Contains("items: z.array(Author),", declarations);
    }

    [Fact]
    public void SameNameInTwoClasses_IsPrefixedWithTheDeclaringClass()
    {
        var schemas = new ZodSchemas(Web());
        schemas.Add(typeof(Blog.Item));
        schemas.Add(typeof(Shop.Item));

        Assert.Equal("BlogItem", schemas.Reference(typeof(Blog.Item)));
        Assert.Equal("ShopItem", schemas.Reference(typeof(Shop.Item)));
    }

    [Fact]
    public void Primitive_IsInlinedWithoutDeclarations()
    {
        var (declarations, reference) = Generate(typeof(IReadOnlyList<int?>));

        Assert.Equal("z.array(z.number().int().nullable())", reference);
        Assert.Equal("", declarations);
    }

    [Fact]
    public void Output_IsDeterministic()
    {
        Assert.Equal(Generate(typeof(Post)).Declarations, Generate(typeof(Post)).Declarations);
    }

    [Fact]
    public void CustomConverterType_IsUnknown()
    {
        var (_, reference) = Generate(typeof(JsonNode));

        Assert.Equal("z.unknown()", reference);
    }
}
