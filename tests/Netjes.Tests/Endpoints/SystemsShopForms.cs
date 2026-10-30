namespace SystemsShop.Forms;

/// <summary>An app's form input in a namespace that starts with "System" but is not one of .NET's.</summary>
public sealed class TwoConstructorsOrder
{
    public TwoConstructorsOrder()
    {
    }

    public TwoConstructorsOrder(string product) => Product = product;

    public string? Product { get; init; }
}
