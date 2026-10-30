using System.Text;

namespace Netjes.Rendering;

/// <summary>
/// Makes serialized JSON safe to embed inside an HTML <c>&lt;script&gt;</c> element, whatever encoder produced it.
/// These characters can only appear inside JSON strings, so replacing them with unicode escapes keeps the value intact.
/// </summary>
internal static class HtmlSafeJson
{
    public static string Escape(string json)
    {
        if (json.AsSpan().IndexOfAny("<>&\u2028\u2029") < 0)
        {
            return json;
        }

        var builder = new StringBuilder(json.Length + 32);
        foreach (var c in json)
        {
            builder.Append(c switch
            {
                '<' => "\\u003c",
                '>' => "\\u003e",
                '&' => "\\u0026",
                '\u2028' => "\\u2028",
                '\u2029' => "\\u2029",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
