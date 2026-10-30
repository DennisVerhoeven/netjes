namespace Netjes;

/// <summary>The props type a page endpoint renders with, read from its delegate's <see cref="PageResult{TProps}"/> return type.</summary>
internal sealed record PagePropsMetadata(Type PropsType)
{
    /// <summary>The props type of a loader or action delegate, unwrapping Task and ValueTask; null when untyped.</summary>
    public static PagePropsMetadata? For(Delegate handler)
    {
        var type = UnwrapAwaitable(handler.Method.ReturnType);
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PageResult<>)
            ? new PagePropsMetadata(type.GetGenericArguments()[0])
            : null;
    }

    internal static Type UnwrapAwaitable(Type type) =>
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GetGenericArguments()[0]
            : type;
}
