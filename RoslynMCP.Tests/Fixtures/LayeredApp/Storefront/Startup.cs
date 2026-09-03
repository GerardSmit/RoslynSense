using LayeredApp.Warehouse;

namespace LayeredApp.Storefront;

public sealed class Startup
{
    public void Configure(IModuleRegistry registry)
    {
        registry.AddWarehouse();
    }
}
