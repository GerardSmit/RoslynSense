using System.Collections.Specialized;

// The Framework configuration surface the web.config pack binds against, stubbed so the fixture
// compiles offline on the SDK the tests run: a net10.0 project cannot reference
// System.Configuration.ConfigurationManager, and what the pack needs from it is the shape of two
// static collection properties.
namespace System.Configuration
{
    public static class ConfigurationManager
    {
        public static NameValueCollection AppSettings { get; } = new NameValueCollection();

        public static ConnectionStringSettingsCollection ConnectionStrings { get; } = new();
    }

    public sealed class ConnectionStringSettings
    {
        public string ConnectionString { get; set; } = string.Empty;

        public string ProviderName { get; set; } = string.Empty;
    }

    public sealed class ConnectionStringSettingsCollection
    {
        public ConnectionStringSettings? this[string name] => null;
    }
}

namespace System.Web.Configuration
{
    public static class WebConfigurationManager
    {
        public static NameValueCollection AppSettings { get; } = new NameValueCollection();
    }
}
