// Minimal shapes of Microsoft.Extensions.Configuration / DependencyInjection / Options, so the
// fixture compiles offline without a restore — the SystemWebStubs pattern. Only what the
// configuration usage index binds against exists.

#pragma warning disable

namespace Microsoft.Extensions.Configuration
{
    public interface IConfiguration
    {
        string? this[string key] { get; set; }

        IConfigurationSection GetSection(string key);
    }

    public interface IConfigurationRoot : IConfiguration
    {
    }

    public interface IConfigurationSection : IConfiguration
    {
        string Key { get; }

        string? Value { get; }
    }

    public static class ConfigurationExtensions
    {
        public static string? GetConnectionString(this IConfiguration configuration, string name)
            => null;

        public static IConfigurationSection GetRequiredSection(
            this IConfiguration configuration, string key) => configuration.GetSection(key);
    }

    public static class ConfigurationBinder
    {
        public static T? GetValue<T>(this IConfiguration configuration, string key) => default;

        public static object? GetValue(this IConfiguration configuration, Type type, string key)
            => null;

        public static void Bind(this IConfiguration configuration, object instance)
        {
        }

        public static T? Get<T>(this IConfiguration configuration) => default;
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    using Microsoft.Extensions.Configuration;
    using Microsoft.Extensions.Options;

    public interface IServiceCollection
    {
    }

    public class ServiceCollection : IServiceCollection
    {
    }

    public static class OptionsConfigurationServiceCollectionExtensions
    {
        public static IServiceCollection Configure<TOptions>(
            this IServiceCollection services, IConfiguration config)
            where TOptions : class => services;

        public static IServiceCollection Configure<TOptions>(
            this IServiceCollection services, string? name, IConfiguration config)
            where TOptions : class => services;
    }

    public static class OptionsServiceCollectionExtensions
    {
        public static OptionsBuilder<TOptions> AddOptions<TOptions>(this IServiceCollection services)
            where TOptions : class => new();
    }
}

namespace Microsoft.Extensions.Options
{
    using Microsoft.Extensions.Configuration;

    public class OptionsBuilder<TOptions>
        where TOptions : class
    {
        public OptionsBuilder<TOptions> Bind(IConfiguration config) => this;

        public OptionsBuilder<TOptions> BindConfiguration(string configSectionPath) => this;
    }
}
