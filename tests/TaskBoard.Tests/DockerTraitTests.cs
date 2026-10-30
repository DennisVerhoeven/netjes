using System.Reflection;
using DotNet.Testcontainers.Containers;

namespace TaskBoard.Tests;

/// <summary>
/// CI on macOS and Windows, which have no Linux containers, leaves out the tests with
/// <c>[Trait("Category", "Docker")]</c>. A test class that needs Docker without the trait would fail only there; this
/// finds it on every system.
/// </summary>
public class DockerTraitTests
{
    [Fact]
    public void TestClassesThatNeedDocker_CarryTheDockerTrait()
    {
        var missing = NeedingDocker().Where(type => !HasDockerTrait(type)).Select(type => type.Name);

        Assert.Empty(missing);
    }

    [Fact]
    public void TheCheck_FindsTheClassesOnTheAppAndThoseWithAPostgresOfTheirOwn()
    {
        var needing = NeedingDocker().ToList();

        Assert.Contains(typeof(ProxyTests), needing);
        Assert.Contains(typeof(SidecarCrashTests), needing);
        Assert.DoesNotContain(typeof(GeneratedTypesTests), needing);
        Assert.DoesNotContain(typeof(DockerTraitTests), needing);
    }

    private static IEnumerable<Type> NeedingDocker() =>
        typeof(DockerTraitTests).Assembly.GetTypes().Where(IsTestClass).Where(NeedsDocker);

    private static bool IsTestClass(Type type) =>
        type.GetMethods().Any(method => method.IsDefined(typeof(FactAttribute), inherit: true));

    /// <summary>In the TaskBoard collection, given a <see cref="TaskBoardApp"/>, or holding a container of its own.</summary>
    private static bool NeedsDocker(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(CollectionAttribute)
            && attribute.ConstructorArguments.Any(argument =>
                Equals(argument.Value, TaskBoardCollection.Name) || Equals(argument.Value, typeof(TaskBoardCollection))))
        || type.GetConstructors().SelectMany(constructor => constructor.GetParameters()).Any(parameter => parameter.ParameterType == typeof(TaskBoardApp))
        || type.GetInterfaces().Any(contract => contract.IsGenericType && contract.GetGenericArguments().Contains(typeof(TaskBoardApp)))
        || type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Any(field => typeof(IContainer).IsAssignableFrom(field.FieldType));

    private static bool HasDockerTrait(Type type) =>
        type.GetCustomAttributesData().Any(attribute =>
            attribute.AttributeType == typeof(TraitAttribute)
            && attribute.ConstructorArguments.Select(argument => argument.Value).SequenceEqual(["Category", "Docker"]));
}
