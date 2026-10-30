using Microsoft.AspNetCore.Mvc;

namespace __NAME__.Features.Home;

/// <summary>
/// The home page's loader (the C# counterpart of getServerSideProps) and a server action. Their types are generated
/// as zod schemas and typed references in generated/netjes.ts.
/// </summary>
public static class HomePage
{
    public sealed record HomeProps(string Message, DateTimeOffset RenderedAt);

    // Form inputs have init properties: a field the post leaves out, such as an unchecked checkbox, keeps its default,
    // where form binding would refuse the post for a positional record.
    public sealed record GreetInput
    {
        public string? Name { get; init; }
    }

    public sealed record Greeting(string Text);

    public static void MapHomePage(this IEndpointRouteBuilder app)
    {
        app.MapPageLoader("/", (TimeProvider time) => Page.Props(new HomeProps("Hello from C#", time.GetUtcNow())));

        // Called from components/HelloForm.tsx through serverActions['home.greet'].
        app.MapAction("home.greet", ([FromForm] GreetInput input) =>
            string.IsNullOrWhiteSpace(input.Name)
                ? ServerAction.Invalid("name", "Tell us your name.")
                : ServerAction.Ok(new Greeting($"Hello, {input.Name.Trim()}!")));
    }
}
