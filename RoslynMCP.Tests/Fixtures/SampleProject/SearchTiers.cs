namespace SampleProject;

/// <summary>
/// Shapes for the Search Everywhere tier order: a type carrying a house prefix whose tail someone
/// would type verbatim, a property spelled exactly that tail, and a method and a property of a
/// third name that collide.
/// </summary>
public class VendorCatalogGateway
{
    public string CatalogGateway { get; set; } = string.Empty;
}

public class CatalogSnapshot
{
    public int SweepCatalog { get; set; }
}

public class CatalogSweeper
{
    public void SweepCatalog()
    {
    }
}
