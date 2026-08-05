namespace LayeredApp.Warehouse;

/// <summary>Stands in for whatever registry the extension method extends — an IServiceCollection
/// in real code. Its own interface so the fixture needs no package.</summary>
public interface IModuleRegistry
{
    void Register(string moduleName);
}

public static class WarehouseModule
{
    /// <summary>
    /// The symbol under test: declared here, called only from the project that references this
    /// one. Every use of it therefore lives outside this project's own dependency closure.
    /// </summary>
    public static void AddWarehouse(this IModuleRegistry registry) =>
        registry.Register("warehouse");
}
