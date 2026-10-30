using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;

namespace Netjes.Rendering;

/// <summary>
/// The form fields a server action declares, the only ones a failed post hands back: its <c>[FromForm]</c> parameters
/// by name, and the fields form binding reads into a <c>[FromForm]</c> type: its properties by their C# and JSON names
/// (generated form types use the JSON names; form binding ignores case), nested ones (<c>address.street</c>), and the
/// items of its lists and dictionaries (<c>tags[0]</c>, <c>lines[0].product</c>, <c>notes[gift]</c>). The
/// <c>[FromForm]</c> members of an <c>[AsParameters]</c> type count too. Endpoint metadata of <c>MapAction</c> and <c>MapPageAction</c>.
/// </summary>
internal sealed class ActionFormFields(MethodInfo method)
{
    private Declared? _declared;

    /// <summary>Whether the action declares a submitted field; names are compared ignoring case, like form binding does.</summary>
    public bool Declares(string field, JsonSerializerOptions json) => (_declared ??= new Declared(method, json)).Declares(field);

    private sealed class Declared
    {
        private readonly JsonSerializerOptions _json;

        // [FromForm] parameters read from one field (repeated for a list).
        private readonly HashSet<string> _values = new(StringComparer.OrdinalIgnoreCase);

        // [FromForm] types: form binding reads their fields without a prefix (email, address.street, [0].product).
        private readonly List<Type> _types = [];

        private readonly ConcurrentDictionary<Type, Dictionary<string, Type>> _members = new();

        public Declared(MethodInfo method, JsonSerializerOptions json)
        {
            _json = json;
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.GetCustomAttribute<AsParametersAttribute>() is not null)
                {
                    var type = parameter.ParameterType;
                    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        Add(property, property.PropertyType, property.Name);
                    }

                    foreach (var argument in type.GetConstructors().SelectMany(c => c.GetParameters()))
                    {
                        Add(argument, argument.ParameterType, argument.Name);
                    }
                }
                else
                {
                    Add(parameter, parameter.ParameterType, parameter.Name);
                }
            }
        }

        public bool Declares(string field) =>
            _values.Contains(field) || _types.Any(type => Resolve(type, field) is { } member && IsValue(member));

        private void Add(ICustomAttributeProvider member, Type type, string? name)
        {
            if (member.GetCustomAttributes(inherit: true).OfType<IFromFormMetadata>().FirstOrDefault() is not { } form
                || typeof(IFormFile).IsAssignableFrom(type) || typeof(IFormFileCollection).IsAssignableFrom(type)
                || typeof(IFormCollection).IsAssignableFrom(type))
            {
                return;
            }

            if (!IsValue(type))
            {
                _types.Add(type);
            }
            else if ((string.IsNullOrEmpty(form.Name) ? name : form.Name) is { } field)
            {
                _values.Add(field);
            }
        }

        /// <summary>
        /// The type form binding reads a field into, following its path from <paramref name="type"/>: a property as
        /// <c>name</c> (<c>.name</c> after the first), an item of a list or dictionary as <c>[key]</c>. Null if the
        /// path names nothing there.
        /// </summary>
        private Type? Resolve(Type type, string field)
        {
            Type? current = type;
            var i = 0;
            while (current is not null && i < field.Length)
            {
                if (field[i] == '[')
                {
                    var end = field.IndexOf(']', i);
                    if (end < 0)
                    {
                        return null;
                    }

                    current = ItemOf(current);
                    i = end + 1;
                    continue;
                }

                if (i > 0)
                {
                    if (field[i] != '.')
                    {
                        return null;
                    }

                    i++;
                }

                var nameEnd = field.IndexOfAny(['.', '['], i);
                nameEnd = nameEnd < 0 ? field.Length : nameEnd;
                current = nameEnd > i ? MemberOf(current, field[i..nameEnd]) : null;
                i = nameEnd;
            }

            return current;
        }

        private Type? MemberOf(Type type, string name) =>
            IsValue(type) || ItemOf(type) is not null
                ? null
                : _members.GetOrAdd(type, static (type, json) => MembersOf(type, json), _json).GetValueOrDefault(name);

        private static Dictionary<string, Type> MembersOf(Type type, JsonSerializerOptions json)
        {
            var members = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
            {
                members.TryAdd(property.Name, property.PropertyType);
                members.TryAdd(
                    property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? json.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name,
                    property.PropertyType);
            }

            return members;
        }
    }

    /// <summary>The type of an item of a list (its element) or a dictionary (its value); null for anything else.</summary>
    internal static Type? ItemOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var generic = (type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces()).Where(i => i.IsGenericType).ToList();
        return generic.FirstOrDefault(i => i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
                ?.GetGenericArguments()[1]
            ?? generic.FirstOrDefault(i => i.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
    }

    /// <summary>
    /// A type form binding reads from one field (repeated for a list), as opposed to one with properties: a string, an
    /// enum, a type with a public static <c>TryParse</c>, or one that implements <see cref="IParsable{TSelf}"/>, also
    /// explicitly, as form binding's parsable converter and minimal APIs' own <c>TryParse</c> lookup find it.
    /// </summary>
    internal static bool IsValue(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsEnum || type == typeof(StringValues))
        {
            return true;
        }

        if (type.IsArray)
        {
            return IsValue(type.GetElementType()!);
        }

        if (type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } list)
        {
            return IsValue(list.GetGenericArguments()[0]);
        }

        return type.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, [typeof(string), type.MakeByRefType()]) is not null
            || type.GetMethod("TryParse", BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IFormatProvider), type.MakeByRefType()]) is not null
            || type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IParsable<>));
    }
}
