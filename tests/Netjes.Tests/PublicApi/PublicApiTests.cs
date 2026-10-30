using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Netjes.Images.SkiaSharp;

namespace Netjes.Tests.PublicApi;

/// <summary>
/// Pins the public API of each shipped assembly: every public and protected type and member, in a stable order, as
/// readable signatures in tests/Netjes.Tests/PublicApi/&lt;assembly&gt;.txt. A change to what an app can call fails the
/// test until the file is rewritten (UPDATE_GOLDEN=1) and the diff reviewed. Types and members that are not visible
/// outside the assembly are not listed.
/// </summary>
public class PublicApiTests
{
    private static readonly string Fixtures = Path.Combine(RepoRoot(), "tests", "Netjes.Tests", "PublicApi");

    [Fact]
    public void TheNetjesAssembly_KeepsItsPublicApi() => AssertGolden("Netjes.txt", Describe(typeof(Page).Assembly));

    [Fact]
    public void TheSkiaSharpAssembly_KeepsItsPublicApi() =>
        AssertGolden("Netjes.Images.SkiaSharp.txt", Describe(typeof(SkiaImageProcessor).Assembly));

    [Fact]
    public void TheListing_NamesNothingThatIsNotPublic()
    {
        foreach (var assembly in new[] { typeof(Page).Assembly, typeof(SkiaImageProcessor).Assembly })
        {
            var listing = Describe(assembly);

            Assert.DoesNotContain("internal ", listing);
            Assert.DoesNotContain("private ", listing);
            Assert.DoesNotContain("<>", listing);
            Assert.DoesNotContain("ITypedPageResult", listing);
        }
    }

    private static string Describe(Assembly assembly)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(assembly.GetName().Name).Append('\n');
        var types = assembly.GetExportedTypes()
            .Where(t => !t.IsNested)
            .GroupBy(t => t.Namespace ?? "")
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var group in types)
        {
            text.Append("\nnamespace ").Append(group.Key).Append('\n');
            foreach (var type in group.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                Append(text, type, indent: "");
            }
        }

        return text.ToString();
    }

    private static void Append(StringBuilder text, Type type, string indent)
    {
        text.Append(indent).Append(Header(type)).Append('\n');
        var inner = indent + "  ";
        if (type.IsEnum)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                text.Append(inner).Append(field.Name).Append(" = ").Append(Convert.ToInt64(field.GetRawConstantValue())).Append('\n');
            }

            return;
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var lines = new List<(int Order, string Name, string Line)>();
        foreach (var member in type.GetMembers(flags))
        {
            if (member.GetCustomAttribute<CompilerGeneratedAttribute>() is not null || !Visible(member))
            {
                continue;
            }

            switch (member)
            {
                case ConstructorInfo c when !c.IsStatic:
                    lines.Add((0, "", $"{Access(c)}{type.Name.Split('`')[0]}({Parameters(c.GetParameters())})"));
                    break;
                case FieldInfo f:
                    lines.Add((1, f.Name, Field(f)));
                    break;
                case PropertyInfo p:
                    lines.Add((2, p.Name, Property(p)));
                    break;
                case EventInfo e:
                    lines.Add((3, e.Name, $"{Access(e.AddMethod!)}{Modifiers(e.AddMethod!)}event {Name(e.EventHandlerType!)} {e.Name}"));
                    break;
                case MethodInfo m when m.IsSpecialName && m.Name.StartsWith("op_", StringComparison.Ordinal) && !IsRecordPlumbing(type, m):
                    lines.Add((5, m.Name, Operator(m)));
                    break;
                case MethodInfo m when !m.IsSpecialName && !IsRecordPlumbing(type, m):
                    lines.Add((4, m.Name, Method(m)));
                    break;
            }
        }

        foreach (var line in lines.OrderBy(l => l.Order).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.Line, StringComparer.Ordinal))
        {
            text.Append(inner).Append(line.Line).Append('\n');
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(NestedVisible).OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            Append(text, nested, inner);
        }
    }

    private static bool NestedVisible(Type nested) => nested.IsNestedPublic || nested.IsNestedFamily || nested.IsNestedFamORAssem;

    private static bool Visible(MemberInfo member) => member switch
    {
        MethodBase m => m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly,
        FieldInfo f => f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly,
        PropertyInfo p => (p.GetMethod is { } g && Visible(g)) || (p.SetMethod is { } s && Visible(s)),
        EventInfo e => Visible(e.AddMethod!),
        _ => false,
    };

    // A record's compiler-written members are not API an author wrote: the copy and printing methods.
    private static bool IsRecordPlumbing(Type type, MethodInfo method) =>
        type.GetMethod("<Clone>$") is not null && method.Name is "PrintMembers" or "<Clone>$" or "op_Equality" or "op_Inequality";

    private static string Header(Type type)
    {
        var kind = type switch
        {
            { IsEnum: true } => "enum",
            { IsInterface: true } => "interface",
            { IsValueType: true } => type.GetCustomAttribute<IsReadOnlyAttribute>() is not null ? "readonly struct" : "struct",
            _ when typeof(Delegate).IsAssignableFrom(type) => "delegate",
            _ when type.IsAbstract && type.IsSealed => "static class",
            _ when type.IsAbstract => "abstract class",
            _ when type.IsSealed => "sealed class",
            _ => "class",
        };
        if (type.GetMethod("<Clone>$") is not null)
        {
            kind = kind.Replace("class", "record").Replace("struct", "record struct");
        }

        var access = type.IsNested && !type.IsNestedPublic ? "protected " : "public ";
        if (kind == "delegate")
        {
            var invoke = type.GetMethod("Invoke")!;
            return $"{access}delegate {Name(invoke.ReturnType, Flags.Of(invoke.ReturnParameter))} {Name(type)}({Parameters(invoke.GetParameters())})";
        }

        var bases = new List<string>();
        if (type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum))
        {
            bases.Add(Name(b));
        }

        var inherited = (type.BaseType?.GetInterfaces() ?? []).ToHashSet();
        // An interface that is not visible outside the assembly is not API.
        var all = type.GetInterfaces().Where(i => i.IsVisible).ToArray();
        foreach (var i in all.Where(i => !inherited.Contains(i) && !all.Any(o => o != i && o.GetInterfaces().Contains(i))))
        {
            bases.Add(Name(i));
        }

        return $"{access}{kind} {Name(type)}{(bases.Count == 0 ? "" : " : " + string.Join(", ", bases.OrderBy(x => x, StringComparer.Ordinal)))}{Constraints(type)}";
    }

    private static string Field(FieldInfo f)
    {
        var modifiers = f.IsLiteral ? "const " : (f.IsStatic ? "static " : "") + (f.IsInitOnly ? "readonly " : "");
        var value = f.IsLiteral ? $" = {Literal(f.GetRawConstantValue())}" : "";
        return $"{Access(f)}{modifiers}{Name(f.FieldType, Flags.Of(f))} {f.Name}{value}";
    }

    private static string Property(PropertyInfo p)
    {
        var accessors = new List<string>();
        var primary = p.GetMethod ?? p.SetMethod!;
        var access = Access(primary);
        foreach (var (accessor, method) in new[] { ("get", p.GetMethod), (IsInit(p.SetMethod) ? "init" : "set", p.SetMethod) })
        {
            if (method is null || !Visible(method))
            {
                continue;
            }

            accessors.Add(Access(method) == access ? accessor : Access(method) + accessor);
        }

        var indexes = p.GetIndexParameters();
        var name = indexes.Length == 0 ? p.Name : $"this[{Parameters(indexes)}]";
        return $"{access}{Modifiers(primary)}{Name(p.PropertyType, Flags.Of(p))} {name} {{ {string.Join("; ", accessors)}; }}";
    }

    private static bool IsInit(MethodInfo? set) =>
        set is not null && set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    private static string Method(MethodInfo m)
    {
        var generics = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
        var extension = m.GetCustomAttribute<ExtensionAttribute>() is not null;
        var parameters = Parameters(m.GetParameters(), extension);
        return $"{Access(m)}{Modifiers(m)}{Name(m.ReturnType, Flags.Of(m.ReturnParameter))} {m.Name}{generics}({parameters}){Constraints(m)}";
    }

    private static string Operator(MethodInfo m)
    {
        var parameters = Parameters(m.GetParameters());
        var returned = Name(m.ReturnType, Flags.Of(m.ReturnParameter));
        return m.Name switch
        {
            "op_Implicit" => $"{Access(m)}static implicit operator {returned}({parameters})",
            "op_Explicit" => $"{Access(m)}static explicit operator {returned}({parameters})",
            _ => $"{Access(m)}static {returned} operator {OperatorSymbol(m.Name)}({parameters})",
        };
    }

    private static string OperatorSymbol(string name) => name switch
    {
        "op_Addition" => "+",
        "op_Subtraction" => "-",
        "op_UnaryPlus" => "+",
        "op_UnaryNegation" => "-",
        "op_Multiply" => "*",
        "op_Division" => "/",
        "op_Modulus" => "%",
        "op_Equality" => "==",
        "op_Inequality" => "!=",
        "op_LessThan" => "<",
        "op_GreaterThan" => ">",
        "op_LessThanOrEqual" => "<=",
        "op_GreaterThanOrEqual" => ">=",
        "op_LogicalNot" => "!",
        "op_OnesComplement" => "~",
        "op_True" => "true",
        "op_False" => "false",
        _ => name,
    };

    private static string Constraints(MemberInfo member)
    {
        var arguments = member switch
        {
            MethodInfo { IsGenericMethodDefinition: true } m => m.GetGenericArguments(),
            // A nested type repeats its declaring type's parameters; its own come last.
            Type { IsGenericTypeDefinition: true } t => t.GetGenericArguments().Skip(t.DeclaringType?.GetGenericArguments().Length ?? 0).ToArray(),
            _ => [],
        };
        var text = new StringBuilder();
        foreach (var argument in arguments)
        {
            var parts = new List<string>();
            var attributes = argument.GenericParameterAttributes;
            var annotated = Flags.Of(argument, member).Peek() == 2;
            var constraints = argument.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).ToArray();
            if (attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("struct");
            }
            else if (attributes.HasFlag(GenericParameterAttributes.ReferenceTypeConstraint))
            {
                parts.Add(annotated ? "class?" : "class");
            }
            else if (constraints.Length == 0 && !annotated && Flags.Of(argument, member).Peek() == 1)
            {
                parts.Add("notnull");
            }

            parts.AddRange(constraints.Select(c => Name(c)));
            if (attributes.HasFlag(GenericParameterAttributes.DefaultConstructorConstraint) && !attributes.HasFlag(GenericParameterAttributes.NotNullableValueTypeConstraint))
            {
                parts.Add("new()");
            }

            if (parts.Count > 0)
            {
                text.Append(" where ").Append(argument.Name).Append(" : ").Append(string.Join(", ", parts));
            }
        }

        return text.ToString();
    }

    private static string Parameters(ParameterInfo[] parameters, bool extension = false) =>
        string.Join(", ", parameters.Select((p, i) =>
        {
            var type = p.ParameterType;
            var prefix = i == 0 && extension ? "this " : "";
            if (p.GetCustomAttribute<ParamArrayAttribute>() is not null)
            {
                prefix += "params ";
            }

            if (type.IsByRef)
            {
                prefix += p.IsOut ? "out " : p.IsIn ? "in " : "ref ";
                type = type.GetElementType()!;
            }

            var text = $"{prefix}{Name(type, Flags.Of(p))} {p.Name}";
            // A struct's default (a CancellationToken = default) reflects as null.
            return p.HasDefaultValue ? $"{text} = {(p.DefaultValue is null && type.IsValueType && Nullable.GetUnderlyingType(type) is null ? "default" : Literal(p.DefaultValue))}" : text;
        }));

    // An interface's members are public by definition, so the listing leaves that out.
    private static string Access(MethodBase m) => m.DeclaringType!.IsInterface ? "" : m.IsPublic ? "public " : "protected ";

    private static string Access(FieldInfo f) => f.IsPublic ? "public " : "protected ";

    private static string Modifiers(MethodBase m)
    {
        var text = new StringBuilder();
        if (m.DeclaringType!.IsInterface && !m.IsStatic)
        {
            return "";
        }

        if (m.IsStatic)
        {
            text.Append("static ");
        }
        else if (m.IsAbstract)
        {
            text.Append("abstract ");
        }
        else if (m.IsVirtual && !m.IsFinal)
        {
            text.Append(m is MethodInfo info && info.GetBaseDefinition() != info ? "override " : "virtual ");
        }

        return text.ToString();
    }

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\"", "\\\"") + "\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        Enum e => $"{e.GetType().Name}.{e}",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)!,
        _ => value.ToString()!,
    };

    private static string Name(Type type, Flags? flags = null)
    {
        // The compiler's annotations are read in the order it writes them: the type, then its arguments or element type.
        var nullable = flags is not null && flags.Next() == 2 && !type.IsValueType ? "?" : "";
        if (type.IsArray)
        {
            return Name(type.GetElementType()!, flags) + "[]" + nullable;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Name(underlying) + "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name + nullable;
        }

        var keyword = type switch
        {
            _ when type == typeof(void) => "void",
            _ when type == typeof(string) => "string",
            _ when type == typeof(int) => "int",
            _ when type == typeof(long) => "long",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(object) => "object",
            _ when type == typeof(double) => "double",
            _ when type == typeof(float) => "float",
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(decimal) => "decimal",
            _ => null,
        };
        if (keyword is not null)
        {
            return keyword + nullable;
        }

        var name = type.IsNested ? Name(type.DeclaringType!) + "." + type.Name : type.Name;
        if (type.IsGenericType)
        {
            var arguments = type.GetGenericArguments();
            var skipped = type.IsNested ? type.DeclaringType!.GetGenericArguments().Length : 0;
            name = name.Split('`')[0];
            var written = arguments.Select((a, i) => (Text: Name(a, flags), Own: i >= skipped)).Where(a => a.Own).Select(a => a.Text).ToArray();
            if (written.Length > 0)
            {
                name += "<" + string.Join(", ", written) + ">";
            }
        }

        // Types of other namespaces are written in full, so the listing shows what a signature depends on.
        var ns = type.IsNested ? null : type.Namespace;
        return (ns is null || ns == "System" ? "" : ns + ".") + name + nullable;
    }

    /// <summary>
    /// The compiler's nullable annotations of one signature position (0 oblivious, 1 not null, 2 may be null), read from
    /// NullableAttribute and, where that is absent, NullableContextAttribute. NullabilityInfoContext reports an unconstrained
    /// type parameter as nullable whether the source says T or T?, so the attributes are read directly.
    /// </summary>
    private sealed class Flags
    {
        private readonly byte[] _values;
        private readonly byte _context;
        private int _next;

        private Flags(byte[] values, byte context)
        {
            _values = values;
            _context = context;
        }

        public static Flags Of(ParameterInfo parameter) => For(parameter.CustomAttributes, parameter.Member);

        public static Flags Of(FieldInfo field) => For(field.CustomAttributes, field);

        public static Flags Of(PropertyInfo property) => For(property.CustomAttributes, property);

        public static Flags Of(Type parameter, MemberInfo owner) => For(parameter.CustomAttributes, owner);

        public byte Peek() => _values.Length == 0 ? _context : _values.Length == 1 ? _values[0] : _next < _values.Length ? _values[_next] : _context;

        public byte Next()
        {
            var value = Peek();
            _next++;
            return value;
        }

        private static Flags For(IEnumerable<CustomAttributeData> attributes, MemberInfo owner)
        {
            var values = Array.Empty<byte>();
            if (attributes.FirstOrDefault(a => a.AttributeType.Name == "NullableAttribute") is { } data)
            {
                var argument = data.ConstructorArguments[0].Value;
                values = argument is byte single ? [single] : ((IEnumerable<CustomAttributeTypedArgument>)argument!).Select(a => (byte)a.Value!).ToArray();
            }

            byte context = 0;
            for (MemberInfo? scope = owner; scope is not null; scope = scope.DeclaringType)
            {
                if (scope.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name == "NullableContextAttribute") is { } found)
                {
                    context = (byte)found.ConstructorArguments[0].Value!;
                    break;
                }
            }

            return new Flags(values, context);
        }
    }

    private static void AssertGolden(string name, string actual)
    {
        var path = Path.Combine(Fixtures, name);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Fixtures);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"{path} is missing; run with UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Netjes.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
