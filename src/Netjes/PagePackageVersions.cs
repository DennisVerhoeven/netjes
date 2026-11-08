using System.Text.RegularExpressions;
using Netjes.Routing;

namespace Netjes;

/// <summary>The versions of a mounted page package, which have to fit each other and the running Netjes.</summary>
internal static partial class PagePackageVersions
{
    /// <summary>
    /// Stops the app when the package's halves are two versions. Its npm half is <paramref name="install"/>, read from
    /// <c>node_modules</c> or from the client build's record.
    /// </summary>
    public static void Check(PagePackage package, PagePackageInstall install)
    {
        var nuget = install.Nuget ?? package.AssemblyName;
        if (!SameVersion(install.Version, package.Version))
        {
            throw new NetjesStartupException(
                $"{package.Name} is {install.Version} on npm and {nuget} is {package.Version} on NuGet: install one version of both");
        }
    }

    /// <summary>Whether two versions are one: by their parts when both are versions, build metadata left out.</summary>
    private static bool SameVersion(string a, string b) =>
        Parse(a) is { } x && Parse(b) is { } y ? Compare(x, y) == 0 : string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// Whether <paramref name="version"/> is in <paramref name="range"/>, as npm reads ranges, and as <c>satisfies</c> in
    /// the netjes CLI does in strict mode: <c>^</c>, <c>~</c>, <c>x</c> and partial versions, <c>&lt;</c>, <c>&lt;=</c>,
    /// <c>&gt;</c>, <c>&gt;=</c> and <c>=</c>, comparators separated by spaces, hyphen ranges and <c>||</c>.
    /// <c>^0.0.4</c> is <c>&gt;=0.0.4 &lt;0.0.5-0</c>, and a prerelease is in a range only when a comparator names a
    /// prerelease of the same major.minor.patch. Build metadata (<c>+sha</c>) is left out. A range or a version that is
    /// none satisfies nothing.
    /// </summary>
    public static bool Satisfies(string version, string range)
    {
        if (Parse(version) is not { } have)
        {
            return false;
        }

        var sets = range.Split("||").Select(part => ComparatorSet(part.Trim())).ToList();
        if (sets.Any(set => set is null))
        {
            return false;
        }

        return sets.Any(set =>
            set!.All(c => Passes(have, c)) &&
            (have.Pre.Length == 0 || set!.Any(c => c.Version.Pre.Length > 0 && CompareMain(c.Version, have) == 0)));
    }

    private sealed record SemVer(long Major, long Minor, long Patch, string[] Pre);

    private sealed record Comparator(string Op, SemVer Version);

    // Numbers have no leading zeros, in the prerelease too.
    [GeneratedRegex(@"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex Full();

    /// <summary>A version in a range, which may leave out its minor and patch, or write them as x, X or *.</summary>
    [GeneratedRegex(@"^(<=|>=|<|>|=|\^|~>|~)?v?(0|[1-9]\d*|[xX*])(?:\.(0|[1-9]\d*|[xX*])(?:\.(0|[1-9]\d*|[xX*])(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?)?)?$")]
    private static partial Regex Partial();

    [GeneratedRegex(@"^(\S+)\s+-\s+(\S+)$")]
    private static partial Regex Hyphen();

    [GeneratedRegex(@"(<=|>=|<|>|=|\^|~>|~)\s+")]
    private static partial Regex OperatorSpace();

    private static SemVer? Parse(string version)
    {
        var match = Full().Match(version.Trim());
        return match.Success && long.TryParse(match.Groups[1].Value, out var major) && long.TryParse(match.Groups[2].Value, out var minor) &&
            long.TryParse(match.Groups[3].Value, out var patch)
            ? new(major, minor, patch, Identifiers(match.Groups[4].Value))
            : null;
    }

    private static string[] Identifiers(string pre) => pre.Length == 0 ? [] : pre.Split('.');

    private static SemVer V(long major, long minor, long patch) => new(major, minor, patch, []);

    /// <summary>The lowest version above every prerelease of a release below this one: what npm's <c>&lt;1.3.0-0</c> bounds are.</summary>
    private static SemVer Before(long major, long minor, long patch) => new(major, minor, patch, ["0"]);

    /// <summary>The comparators one comparator of a range stands for, as npm reads it: <c>^1.2</c> is <c>&gt;=1.2.0 &lt;2.0.0-0</c>.</summary>
    private static Comparator[]? Comparators(string token)
    {
        var match = Partial().Match(token);
        if (!match.Success)
        {
            return null;
        }

        var op = match.Groups[1].Value;
        // Null for a part that is left out or written as x; a number too large for one makes the comparator none.
        var parts = new long?[3];
        for (var i = 0; i < 3; i++)
        {
            var part = match.Groups[i + 2].Value;
            if (part.Length > 0 && char.IsAsciiDigit(part[0]))
            {
                if (!long.TryParse(part, out var number))
                {
                    return null;
                }

                parts[i] = number;
            }
        }

        var (major, minor, patch) = (parts[0], parts[1], parts[2]);
        if (major is not { } m)
        {
            return op is "<" or ">" ? [new("<", Before(0, 0, 0))] : [];
        }

        if (minor is not { } n)
        {
            return op switch
            {
                ">" => [new(">=", V(m + 1, 0, 0))],
                ">=" => [new(">=", V(m, 0, 0))],
                "<" => [new("<", Before(m, 0, 0))],
                "<=" => [new("<", Before(m + 1, 0, 0))],
                _ => [new(">=", V(m, 0, 0)), new("<", Before(m + 1, 0, 0))],
            };
        }

        if (patch is not { } p)
        {
            return op switch
            {
                ">" => [new(">=", V(m, n + 1, 0))],
                ">=" => [new(">=", V(m, n, 0))],
                "<" => [new("<", Before(m, n, 0))],
                "<=" => [new("<", Before(m, n + 1, 0))],
                "^" => [new(">=", V(m, n, 0)), new("<", m > 0 ? Before(m + 1, 0, 0) : Before(0, n + 1, 0))],
                _ => [new(">=", V(m, n, 0)), new("<", Before(m, n + 1, 0))],
            };
        }

        var version = new SemVer(m, n, p, Identifiers(match.Groups[5].Value));
        return op switch
        {
            // npm's caret allows the changes that keep the first non-zero part: ^0.0.4 is 0.0.4 alone, and its prereleases.
            "^" => [new(">=", version), new("<", m > 0 ? Before(m + 1, 0, 0) : n > 0 ? Before(0, n + 1, 0) : Before(0, 0, p + 1))],
            "~" or "~>" => [new(">=", version), new("<", Before(m, n + 1, 0))],
            "" => [new("=", version)],
            _ => [new(op, version)],
        };
    }

    /// <summary>The comparators of one part of a range, between <c>||</c>s: a hyphen range, or comparators separated by spaces.</summary>
    private static List<Comparator>? ComparatorSet(string part)
    {
        if (Hyphen().Match(part) is { Success: true } hyphen)
        {
            // From the first version, up to and with the second: <=2.3 is below 2.4.0, as npm reads a partial version there.
            return Comparators(">=" + hyphen.Groups[1].Value) is { } from && Comparators("<=" + hyphen.Groups[2].Value) is { } to
                ? [.. from, .. to]
                : null;
        }

        var set = new List<Comparator>();
        foreach (var token in OperatorSpace().Replace(part, "$1").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Comparators(token) is not { } parsed)
            {
                return null;
            }

            set.AddRange(parsed);
        }

        return set;
    }

    private static bool Passes(SemVer version, Comparator comparator)
    {
        var order = Compare(version, comparator.Version);
        return comparator.Op switch
        {
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            ">=" => order >= 0,
            _ => order == 0,
        };
    }

    private static int CompareMain(SemVer a, SemVer b) =>
        a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor != b.Minor ? a.Minor.CompareTo(b.Minor) : a.Patch.CompareTo(b.Patch);

    private static int Compare(SemVer a, SemVer b)
    {
        if (CompareMain(a, b) is var main and not 0)
        {
            return main;
        }

        // A version with a prerelease comes before the version itself, and identifiers compare one by one: numbers by
        // value, and before words.
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
        {
            return b.Pre.Length - a.Pre.Length;
        }

        for (var i = 0; i < Math.Max(a.Pre.Length, b.Pre.Length); i++)
        {
            if (i >= a.Pre.Length || i >= b.Pre.Length)
            {
                return i >= a.Pre.Length ? -1 : 1;
            }

            var (x, y) = (a.Pre[i], b.Pre[i]);
            var (xNumber, yNumber) = (ulong.TryParse(x, out var xn) && x.All(char.IsAsciiDigit), ulong.TryParse(y, out var yn) && y.All(char.IsAsciiDigit));
            var order = xNumber && yNumber ? xn.CompareTo(yn) : xNumber != yNumber ? (xNumber ? -1 : 1) : string.CompareOrdinal(x, y);
            if (order != 0)
            {
                return order;
            }
        }

        return 0;
    }
}
