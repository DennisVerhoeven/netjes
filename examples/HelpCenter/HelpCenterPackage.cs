namespace Netjes.Examples.HelpCenter;

/// <summary>
/// The C# half of the help center page package. Its npm half, <c>@netjes-examples/help-center</c>, holds the pages, and
/// its version is this assembly's: an app that installs two versions of the halves stops at startup.
/// </summary>
public static class HelpCenterPackage
{
    /// <summary>The npm half's name, which starts the id of each of its files.</summary>
    public const string Name = "@netjes-examples/help-center";

    /// <summary>The package's descriptor, which <see cref="HelpCenterEndpoints.MapHelpCenter"/> mounts.</summary>
    public static PagePackage Package { get; } = new(Name, typeof(HelpCenterPackage).Assembly);
}
