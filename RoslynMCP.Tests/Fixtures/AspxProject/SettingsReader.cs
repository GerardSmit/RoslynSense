using System.Configuration;
using System.Web.Configuration;

namespace AspxProject
{
    /// <summary>Every read shape the web.config usage index recognizes.</summary>
    public class SettingsReader
    {
        public string? Cdn() => ConfigurationManager.AppSettings["CdnRoot"];

        public string? CdnAgain() => WebConfigurationManager.AppSettings["CdnRoot"];

        public string? Retries() => ConfigurationManager.AppSettings.Get("RetryCount");

        public string? Main() => ConfigurationManager.ConnectionStrings["Main"]?.ConnectionString;

        /// <summary>A key the code reads and the file never declares — what completion offers.</summary>
        public string? Missing() => ConfigurationManager.AppSettings["PageSize"];

        /// <summary>Not a configuration read: the name matches, the declaring type does not.</summary>
        public string? Decoy() => Local.AppSettings["CdnRoot"];
    }

    /// <summary>
    /// The read wrapped once and then called everywhere — where a scan that knows only
    /// ConfigurationManager's own shapes stops, and every call below it goes uncounted.
    /// </summary>
    public static class Config
    {
        public static string? GetSetting(string setting) => ConfigurationManager.AppSettings[setting];

        public static string? GetConnection(string name) =>
            ConfigurationManager.ConnectionStrings[name]?.ConnectionString;

        /// <summary>The same shape over another collection, which reads no setting at all.</summary>
        public static string? GetLocal(string key) => Local.AppSettings[key];
    }

    /// <summary>The call sites, which name a setting and nothing else.</summary>
    public class WrapperCallers
    {
        public string? Wrapped() => Config.GetSetting("WrappedSetting");

        public string? Connection() => Config.GetConnection("WrappedConnection");

        public string? Decoy() => Config.GetLocal("DeadSetting");
    }

    public static class Local
    {
        public static System.Collections.Specialized.NameValueCollection AppSettings { get; } = new();
    }
}
