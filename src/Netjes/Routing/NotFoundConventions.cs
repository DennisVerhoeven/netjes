using Microsoft.AspNetCore.Builder;

namespace Netjes.Routing;

/// <summary>
/// The conventions an app gives the not-found below a page package's mount (<see cref="PagePackageBuilder.ConfigureNotFound"/>):
/// the endpoint that answers the unknown URLs below the mount carries them, and nothing else.
/// </summary>
internal sealed class NotFoundConventions : IEndpointConventionBuilder
{
    public List<Action<EndpointBuilder>> Conventions { get; } = [];

    public List<Action<EndpointBuilder>> FinallyConventions { get; } = [];

    public void Add(Action<EndpointBuilder> convention) => Conventions.Add(convention);

    public void Finally(Action<EndpointBuilder> finallyConvention) => FinallyConventions.Add(finallyConvention);
}
