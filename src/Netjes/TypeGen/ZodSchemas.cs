using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Netjes.TypeGen;

/// <summary>
/// zod schemas for .NET types, as System.Text.Json writes them with the given options: property names after the
/// naming policy and <c>[JsonPropertyName]</c>, nullability from nullable annotations, ignore conditions, enums as
/// they serialize, polymorphism. Named types become <c>export const X = z.object(...)</c> declarations; anonymous
/// types, collections and primitives are written inline.
/// </summary>
/// <remarks>Usage: <see cref="Add"/> every root type, then read <see cref="Declarations"/> and <see cref="Reference"/>.</remarks>
internal sealed class ZodSchemas
{
    private readonly JsonSerializerOptions _options;
    private readonly List<Type> _order = [];
    private readonly HashSet<Type> _named = [];
    private readonly HashSet<Type> _visiting = [];
    private readonly HashSet<(Type Owner, Type Target)> _backReferences = [];
    private Dictionary<Type, string>? _names;

    public ZodSchemas(JsonSerializerOptions options)
    {
        // A copy: reading contracts locks an options instance, and the app's may still be configured.
        _options = new JsonSerializerOptions(options);
        _options.TypeInfoResolver ??= new DefaultJsonTypeInfoResolver();
    }

    /// <summary>Collects a root type and every named type it reaches.</summary>
    public void Add(Type type)
    {
        if (_names is not null)
        {
            throw new InvalidOperationException("Add every type before reading declarations or references.");
        }

        Visit(type, owner: null);
    }

    /// <summary>The identifier of a named type, or the inline expression of any other type.</summary>
    public string Reference(Type type) => Expression(type, owner: null);

    /// <summary>
    /// What System.Text.Json reads into an object type, inline, when that differs from what it writes: properties it
    /// never sets (computed ones) left out, nullable and defaulted ones optional. Null for other types and when the
    /// two agree.
    /// </summary>
    public string? InputExpression(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum || !IsNamed(type) || Polymorphism(_options.GetTypeInfo(type)) is not null)
        {
            return null;
        }

        var info = _options.GetTypeInfo(type);
        var properties = info.Properties
            .Where(p => !p.IsExtensionData && (p.Set is not null || p.AssociatedParameter is not null))
            .Select(p =>
            {
                var schema = PropertySchema(p, owner: null, info.NumberHandling, p.IsSetNullable);
                var optional = !p.IsRequired && (p.IsSetNullable || p.AssociatedParameter is { HasDefaultValue: true });
                return $"{Key(p.Name)}: {schema}{(optional ? ".optional()" : "")}";
            })
            .ToList();
        var input = properties.Count == 0 ? "z.object({})" : $"z.object({{ {string.Join(", ", properties)} }})";
        // The written shape, with plain keys: back references need no getters once every declaration exists.
        var output = ObjectExpression(info, owner: null, inline: true);
        return input == output ? null : input;
    }

    /// <summary>The declarations of every named type, dependencies first.</summary>
    public string Declarations()
    {
        var output = new StringBuilder();
        foreach (var type in _order)
        {
            var name = Names()[type];
            output.Append("export const ").Append(name).Append(" = ").Append(Definition(type)).Append(";\n");
            output.Append("export type ").Append(name).Append(" = z.infer<typeof ").Append(name).Append(">;\n");
        }

        return output.ToString();
    }

    // Collection: walk the type graph once, recording named types in post-order and back references (cycles).

    /// <summary>Whether some schema uses <c>deferred()</c> (Page.Defer), which the generated file imports from netjes/zod.</summary>
    public bool UsesDeferred { get; private set; }

    private static Type? DeferredValueType(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Deferred<>) ? type.GetGenericArguments()[0] : null;

    private void Visit(Type type, Type? owner)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (DeferredValueType(type) is { } deferredValue)
        {
            UsesDeferred = true;
            Visit(deferredValue, owner);
            return;
        }

        if (Primitive(type) is not null || type == typeof(byte[]))
        {
            return;
        }

        if (IsNamed(type))
        {
            if (_visiting.Contains(type))
            {
                _backReferences.Add((owner ?? type, type));
                return;
            }

            if (!_named.Add(type))
            {
                return;
            }

            _visiting.Add(type);
            foreach (var child in Children(type))
            {
                Visit(child, type);
            }

            _visiting.Remove(type);
            _order.Add(type);
            return;
        }

        foreach (var child in Children(type))
        {
            Visit(child, owner);
        }
    }

    private IEnumerable<Type> Children(Type type)
    {
        if (type.IsEnum)
        {
            yield break;
        }

        var info = _options.GetTypeInfo(type);
        switch (info.Kind)
        {
            case JsonTypeInfoKind.Enumerable:
            case JsonTypeInfoKind.Dictionary:
                yield return info.ElementType!;
                break;
            case JsonTypeInfoKind.Object when Polymorphism(info) is { } polymorphism:
                foreach (var derived in polymorphism.DerivedTypes.Where(d => d.DerivedType != type))
                {
                    yield return derived.DerivedType;
                }

                // A concrete base is a member of its own union, written with its own properties.
                if (!type.IsAbstract)
                {
                    foreach (var property in Properties(info))
                    {
                        yield return property.PropertyType;
                    }
                }

                break;
            case JsonTypeInfoKind.Object:
                foreach (var property in Properties(info))
                {
                    yield return property.PropertyType;
                }

                break;
        }
    }

    /// <summary>The type's polymorphism, when it has derived types (a bare <c>[JsonPolymorphic]</c> writes a plain object).</summary>
    private static JsonPolymorphismOptions? Polymorphism(JsonTypeInfo info) =>
        info.PolymorphismOptions is { DerivedTypes.Count: > 0 } polymorphism ? polymorphism : null;

    private bool IsNamed(Type type) =>
        type.IsEnum || (DeferredValueType(type) is null && _options.GetTypeInfo(type).Kind == JsonTypeInfoKind.Object && !IsAnonymous(type));

    private static bool IsAnonymous(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) && type.Name.Contains("AnonymousType", StringComparison.Ordinal);

    private static IEnumerable<JsonPropertyInfo> Properties(JsonTypeInfo info) =>
        info.Properties.Where(p => p.Get is not null && !p.IsExtensionData);

    private static bool IsNumber(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) is var t
        && (t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint)
            || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double) || t == typeof(decimal)
            || t == typeof(Half));

    private static bool IsFloatingPoint(Type type) => type == typeof(float) || type == typeof(double) || type == typeof(Half);

    // Expressions.

    private string Expression(Type type, Type? owner) => Expression(type, owner, nullability: null);

    /// <param name="nullability">What the property's annotations say, for the nullability of list and dictionary items.</param>
    /// <param name="numberHandling">
    /// The number handling of the property this value is in, which also covers its collection items; null for the options'.
    /// </param>
    private string Expression(Type type, Type? owner, NullabilityInfo? nullability, JsonNumberHandling? numberHandling = null)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Expression(underlying, owner, null, numberHandling) + ".nullable()";
        }

        // Page.Defer: a thenable for use(), validated when the value arrives.
        if (DeferredValueType(type) is { } deferredValue)
        {
            // Deferred<Item?>: the annotation is on the type argument. The value is written with the options alone.
            var valueNullability = nullability?.GenericTypeArguments is [var argument] ? argument : null;
            return $"deferred({ItemExpression(deferredValue, owner, valueNullability, numberHandling: null)})";
        }

        if (HasOptionsConverter(type))
        {
            return "z.unknown()";
        }

        if (Primitive(type) is { } primitive)
        {
            var handling = numberHandling ?? _options.NumberHandling;
            var asStrings = IsNumber(type) && handling.HasFlag(JsonNumberHandling.WriteAsString);
            var schema = asStrings ? Coerced(primitive) : primitive;
            // "NaN", "Infinity" and "-Infinity" are strings in the JSON, numbers once parsed.
            const string NamedLiterals = "z.enum([\"NaN\", \"Infinity\", \"-Infinity\"]).transform(Number)";
            return IsFloatingPoint(type) && handling.HasFlag(JsonNumberHandling.AllowNamedFloatingPointLiterals)
                ? asStrings ? $"z.union([{NamedLiterals}, {schema}])" : $"z.union([{schema}, {NamedLiterals}])"
                : schema;
        }

        if (type == typeof(byte[]))
        {
            return "z.base64()";
        }

        if (IsNamed(type))
        {
            return Names()[type];
        }

        if (HasOptionsConverter(type))
        {
            return "z.unknown()";
        }

        var info = _options.GetTypeInfo(type);
        return info.Kind switch
        {
            JsonTypeInfoKind.Enumerable =>
                $"z.array({ItemExpression(info.ElementType!, owner, ItemNullability(nullability, dictionary: false), numberHandling)})",
            JsonTypeInfoKind.Dictionary =>
                $"z.record(z.string(), {ItemExpression(info.ElementType!, owner, ItemNullability(nullability, dictionary: true), numberHandling)})",
            JsonTypeInfoKind.Object => ObjectExpression(info, owner, inline: true),
            _ => "z.unknown()",
        };
    }

    /// <summary>The nullability of a collection's items (array element, the last generic argument), if annotated.</summary>
    private static NullabilityInfo? ItemNullability(NullabilityInfo? collection, bool dictionary)
    {
        if (collection is null)
        {
            return null;
        }

        if (collection.ElementType is { } element)
        {
            return element;
        }

        var arguments = collection.GenericTypeArguments;
        return arguments.Length == 0 ? null : dictionary ? arguments[^1] : arguments[0];
    }

    private string ItemExpression(Type type, Type? owner, NullabilityInfo? nullability, JsonNumberHandling? numberHandling)
    {
        var schema = Expression(type, owner, nullability, numberHandling);
        return nullability is { ReadState: NullabilityState.Nullable } && !nullability.Type.IsGenericParameter
            && !type.IsValueType && schema != "z.unknown()"
            ? schema + ".nullable()"
            : schema;
    }

    /// <summary>A converter in the options writes this type its own way, so its shape is unknown here.</summary>
    private bool HasOptionsConverter(Type type) =>
        !type.IsEnum && _options.Converters.Any(c => c is not JsonStringEnumConverter && c.CanConvert(type));

    private string Definition(Type type)
    {
        if (type.IsEnum)
        {
            return EnumExpression(type);
        }

        var info = _options.GetTypeInfo(type);
        return Polymorphism(info) is { } polymorphism
            ? UnionExpression(type, polymorphism)
            : ObjectExpression(info, type, inline: false);
    }

    private string ObjectExpression(JsonTypeInfo info, Type? owner, bool inline)
    {
        var properties = Properties(info).Select(p => PropertyExpression(p, owner, info.NumberHandling)).ToList();
        // [JsonExtensionData] writes its entries as extra keys of the object.
        var extra = info.Properties.Any(p => p.IsExtensionData) ? ".catchall(z.unknown())" : "";
        if (properties.Count == 0)
        {
            return "z.object({})" + extra;
        }

        return (inline
            ? $"z.object({{ {string.Join(", ", properties)} }})"
            : $"z.object({{\n{string.Concat(properties.Select(p => $"  {p},\n"))}}})") + extra;
    }

    // NullabilityInfoContext keeps unsynchronised dictionaries, so each generation run owns one rather than sharing it.
    private readonly NullabilityInfoContext _nullabilityContext = new();

    private NullabilityInfo? NullabilityOf(JsonPropertyInfo property)
    {
        try
        {
            // Read the annotations where they are written: on the generic definition for a constructed type, where
            // a T item says nothing about nullability (the generic parameter checks below skip those).
            return property.AttributeProvider switch
            {
                PropertyInfo p => _nullabilityContext.Create(Open(p)),
                FieldInfo f => _nullabilityContext.Create(f.DeclaringType is { IsConstructedGenericType: true } d
                    ? d.GetGenericTypeDefinition().GetField(f.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? f
                    : f),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static PropertyInfo Open(PropertyInfo property) =>
        property.DeclaringType is { IsConstructedGenericType: true } declaring
            ? declaring.GetGenericTypeDefinition().GetProperty(property.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? property
            : property;

    /// <param name="typeNumberHandling">The declaring type's <c>[JsonNumberHandling]</c>, which a property's own overrides.</param>
    private string PropertyExpression(JsonPropertyInfo property, Type? owner, JsonNumberHandling? typeNumberHandling)
    {
        var schema = PropertySchema(property, owner, typeNumberHandling, property.IsGetNullable);

        var omittable = property.ShouldSerialize is not null
            || _options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingDefault
            || (_options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull && property.IsGetNullable);
        if (omittable)
        {
            schema += ".optional()";
        }

        var key = Key(property.Name);
        // zod 4 resolves recursive references lazily through getters.
        return owner is not null && ReferencesBack(owner, property.PropertyType)
            ? $"get {key}() {{ return {schema}; }}"
            : $"{key}: {schema}";
    }

    /// <summary>A property's value: its converter, number handling and nullability (<paramref name="nullable"/>).</summary>
    private string PropertySchema(JsonPropertyInfo property, Type? owner, JsonNumberHandling? typeNumberHandling, bool nullable)
    {
        var numberHandling = property.NumberHandling ?? typeNumberHandling ?? _options.NumberHandling;
        var schema = property.CustomConverter is not null
            ? "z.unknown()"
            : Expression(property.PropertyType, owner, NullabilityOf(property), numberHandling);
        return nullable && Nullable.GetUnderlyingType(property.PropertyType) is null && schema != "z.unknown()"
            ? schema + ".nullable()"
            : schema;
    }

    /// <summary>A number schema that also accepts the number written as a string; anything else (a converter's unknown) as it is.</summary>
    private static string Coerced(string schema) =>
        schema.StartsWith("z.number()", StringComparison.Ordinal) ? "z.coerce." + schema[2..] : schema;

    private bool ReferencesBack(Type owner, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (_backReferences.Contains((owner, type)))
        {
            return true;
        }

        if (DeferredValueType(type) is { } deferredValue)
        {
            return ReferencesBack(owner, deferredValue);
        }

        if (Primitive(type) is not null || type == typeof(byte[]) || IsNamed(type))
        {
            return false;
        }

        return Children(type).Any(child => ReferencesBack(owner, child));
    }

    private string EnumExpression(Type type)
    {
        var values = Enum.GetValues(type).Cast<object>().Select(v => JsonSerializer.Serialize(v, type, _options)).Distinct().ToList();
        if (values.Count == 0)
        {
            return "z.never()";
        }

        if (type.IsDefined(typeof(FlagsAttribute)))
        {
            // Combinations are written as "Read, Write" (string converter) or as their sum.
            return values.All(v => v.StartsWith('"')) ? "z.string()" : "z.number().int()";
        }

        if (values.All(v => v.StartsWith('"')))
        {
            return $"z.enum([{string.Join(", ", values)}])";
        }

        return values.Count == 1 ? $"z.literal({values[0]})" : $"z.union([{string.Join(", ", values.Select(v => $"z.literal({v})"))}])";
    }

    private string UnionExpression(Type type, JsonPolymorphismOptions polymorphism)
    {
        var discriminator = polymorphism.TypeDiscriminatorPropertyName;
        var members = polymorphism.DerivedTypes
            .Select(d =>
            {
                // The base itself, or a derived type that is a union of its own, is written here with its own
                // properties and this union's discriminator: its declaration is not an object to extend.
                var member = d.DerivedType == type || Polymorphism(_options.GetTypeInfo(d.DerivedType)) is not null
                    ? ObjectExpression(_options.GetTypeInfo(d.DerivedType), d.DerivedType, inline: true)
                    : Names()[d.DerivedType];
                return d.TypeDiscriminator is { } value
                    ? $"{member}.extend({{ {JsonSerializer.Serialize(discriminator)}: z.literal({JsonSerializer.Serialize(value)}) }})"
                    : member;
            })
            .ToList();
        var listsItself = polymorphism.DerivedTypes.Any(d => d.DerivedType == type);
        var discriminated = polymorphism.DerivedTypes.All(d => d.TypeDiscriminator is not null) && (type.IsAbstract || listsItself);
        if (!type.IsAbstract && !listsItself)
        {
            // A plain instance of the base is written without a discriminator, with the base's own properties.
            var own = _options.GetTypeInfo(type);
            members.Add(ObjectExpression(own, type, inline: true));
        }

        return discriminated
            ? $"z.discriminatedUnion({JsonSerializer.Serialize(discriminator)}, [{string.Join(", ", members)}])"
            : $"z.union([{string.Join(", ", members)}])";
    }

    private static string? Primitive(Type type) => type switch
    {
        _ when type == typeof(string) || type == typeof(char) || type == typeof(Uri) || type == typeof(TimeSpan) || type == typeof(Version) => "z.string()",
        _ when type == typeof(bool) => "z.boolean()",
        _ when type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
            || type == typeof(int) || type == typeof(uint) => "z.number().int()",
        // zod's int() only accepts safe integers; 64-bit values beyond 2^53 still arrive as (rounded) numbers.
        _ when type == typeof(long) || type == typeof(ulong) => "z.number()",
        _ when type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(Half) => "z.number()",
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "z.iso.datetime({ offset: true, local: true })",
        _ when type == typeof(DateOnly) => "z.iso.date()",
        _ when type == typeof(TimeOnly) => "z.iso.time()",
        _ when type == typeof(Guid) => "z.guid()",
        _ when type == typeof(object) || type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type) || type == typeof(JsonDocument) => "z.unknown()",
        _ => null,
    };

    private static string Key(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] is '_' or '$') && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$')
            ? name
            : JsonSerializer.Serialize(name);

    // Names: the type's own name, generic arguments spelled out; prefixed with the declaring class, then the
    // namespace, when two types would otherwise share one.

    private Dictionary<Type, string> Names()
    {
        if (_names is not null)
        {
            return _names;
        }

        var names = _named.ToDictionary(t => t, t => SimpleName(t, qualify: 0));
        // The generated file's own exports (pages, Href, Route, ...) are taken too.
        bool Clashes(IGrouping<string, KeyValuePair<Type, string>> group) =>
            group.Count() > 1 || TypeScriptGenerator.ReservedExports.Contains(group.Key);
        for (var level = 1; level <= 2; level++)
        {
            var clashes = names.GroupBy(n => n.Value, StringComparer.Ordinal).Where(Clashes).SelectMany(g => g).ToList();
            foreach (var (type, _) in clashes)
            {
                names[type] = SimpleName(type, level);
            }
        }

        foreach (var (type, name) in names.Where(n => TypeScriptGenerator.ReservedExports.Contains(n.Value)).ToList())
        {
            names[type] = name + "Schema";
        }

        var remaining = names.GroupBy(n => n.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();
        foreach (var group in remaining)
        {
            var index = 1;
            foreach (var (type, name) in group.OrderBy(g => g.Key.FullName, StringComparer.Ordinal))
            {
                names[type] = $"{name}{index++}";
            }
        }

        return _names = names;
    }

    private static string SimpleName(Type type, int qualify)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick >= 0)
        {
            name = name[..tick];
        }

        if (type.IsGenericType)
        {
            name += "Of" + string.Join("And", type.GetGenericArguments().Select(a => SimpleName(a, 0)));
        }

        if (qualify >= 1 && type.DeclaringType is { } declaring)
        {
            name = SimpleName(declaring, 0) + name;
        }

        if (qualify >= 2 && type.Namespace is { } ns)
        {
            name = string.Concat(ns.Split('.').Select(part => part.Length > 0 ? char.ToUpperInvariant(part[0]) + part[1..] : part)) + name;
        }

        return new string([.. name.Where(c => char.IsLetterOrDigit(c) || c == '_')]);
    }
}
