using System.Text.Json;
using Netjes.Rendering;

namespace Netjes.Tests.Rendering;

public class HtmlSafeJsonTests
{
    [Fact]
    public void Escape_ScriptCloseTagInString_CannotTerminateTheScriptElement()
    {
        var json = JsonSerializer.Serialize(new { bio = "</script><script>alert(1)</script>" },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var escaped = HtmlSafeJson.Escape(json);

        Assert.DoesNotContain("<", escaped);
        Assert.DoesNotContain(">", escaped);
    }

    [Theory]
    [InlineData("a < b && c > d")]
    [InlineData("<!-- comment -->")]
    [InlineData("line\u2028separator\u2029paragraph")]
    public void Escape_RoundTripsToTheSameValue(string value)
    {
        var json = JsonSerializer.Serialize(new { value },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var escaped = HtmlSafeJson.Escape(json);

        Assert.DoesNotContain("\u2028", escaped);
        Assert.DoesNotContain("&", escaped);
        Assert.Equal(value, JsonDocument.Parse(escaped).RootElement.GetProperty("value").GetString());
    }
}
