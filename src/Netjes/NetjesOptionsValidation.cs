using Microsoft.Extensions.Options;

namespace Netjes;

/// <summary>
/// Refuses settings Netjes would otherwise read in a way the app did not mean, as the options are first read and as the
/// host starts (<c>ValidateOnStart</c>), with each refused value and the reason in the message.
/// </summary>
internal sealed class NetjesOptionsValidation : IValidateOptions<NetjesOptions>
{
    private const string NoReloadCookiesForms =
        "List cookie names, such as 'cart', or prefixes that end in '*', such as '.app.cart.*' for every cookie whose name starts with '.app.cart.'.";

    public ValidateOptionsResult Validate(string? name, NetjesOptions options)
    {
        var failures = NoReloadCookiesFailures(options.NoReloadCookies).ToList();
        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    // A name, or a prefix: text and one '*' at the end. A '*' alone (or after spaces, which browsers drop from a name)
    // would cover every cookie but the sign-in ones, a '*' inside a name looks like a pattern Netjes does not match, and
    // an empty name would cover a Set-Cookie without '=', which browsers store under an empty name.
    private static IEnumerable<string> NoReloadCookiesFailures(IReadOnlyList<string>? entries)
    {
        foreach (var entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                yield return $"Netjes:NoReloadCookies has an empty name. {NoReloadCookiesForms}";
                continue;
            }

            var star = entry.IndexOf('*');
            if (star >= 0 && star < entry.Length - 1)
            {
                yield return $"Netjes:NoReloadCookies has '{entry}': a '*' only ends a prefix. {NoReloadCookiesForms}";
            }
            else if (star == entry.Length - 1 && string.IsNullOrWhiteSpace(entry[..star]))
            {
                yield return $"Netjes:NoReloadCookies has '{entry}', a prefix with nothing before the '*', which would cover every cookie. {NoReloadCookiesForms}";
            }
        }
    }
}
