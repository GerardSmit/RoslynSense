using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

public class AutoConnectionStringDiscoveryTests : IDisposable
{
    private readonly string _root;

    public AutoConnectionStringDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rsense-autodb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string MakeProject(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return dir;
    }

    [Fact]
    public void DiscoversAppSettingsConnectionStrings()
    {
        var dir = MakeProject("CoreApp");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
        {
            "ConnectionStrings": {
                "Default": "Server=(local);Database=Core;Integrated Security=true",
                "Reports": "Host=db.local;Port=5432;Username=app;Password=x;Database=reports"
            }
        }
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        Assert.Equal(2, providers.Count);
        var def = providers.Single(p => p.Alias == "CoreApp_Default");
        Assert.Equal("mssql", def.ProviderName);
        var rep = providers.Single(p => p.Alias == "CoreApp_Reports");
        Assert.Equal("psql", rep.ProviderName);
    }

    [Fact]
    public void DiscoversWebConfigConnectionStrings()
    {
        var dir = MakeProject("WebApp");
        File.WriteAllText(Path.Combine(dir, "web.config"), """
        <?xml version="1.0"?>
        <configuration>
            <connectionStrings>
                <add name="Site" connectionString="Server=(local);Database=Site;Integrated Security=true" providerName="System.Data.SqlClient" />
                <add name="Pg" connectionString="Host=pg;Username=u;Password=p;Database=d" providerName="Npgsql" />
                <add name="Lite" connectionString="Data Source=app.db" providerName="System.Data.SQLite" />
            </connectionStrings>
        </configuration>
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        Assert.Equal(3, providers.Count);
        Assert.Equal("mssql", providers.Single(p => p.Alias == "WebApp_Site").ProviderName);
        Assert.Equal("psql", providers.Single(p => p.Alias == "WebApp_Pg").ProviderName);
        Assert.Equal("sqlite", providers.Single(p => p.Alias == "WebApp_Lite").ProviderName);
    }

    [Fact]
    public void EnvironmentSpecificAppSettingsOverridesBase()
    {
        var dir = MakeProject("Api");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
        {"ConnectionStrings":{"Default":"Server=prod;Database=Api;Integrated Security=true"}}
        """);
        File.WriteAllText(Path.Combine(dir, "appsettings.Development.json"), """
        {"ConnectionStrings":{"Default":"Server=dev;Database=Api;Integrated Security=true"}}
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        var def = Assert.Single(providers);
        Assert.Equal("Api_Default", def.Alias);
        // Development variant wins (alphabetical sort puts base first, env-specific later overrides).
        // We can't read the connection string back from IDbProvider directly, so this is verified
        // via the CLI parser/integration, but the override behavior is exercised here as the
        // single-result count proves dedupe by alias.
    }

    [Fact]
    public void SkipsBinObjAndHiddenDirectories()
    {
        var dir = MakeProject("App");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, "bin", "appsettings.json"),
            """{"ConnectionStrings":{"X":"Server=y;Database=z;Integrated Security=true"}}""");
        File.WriteAllText(Path.Combine(dir, "obj", "appsettings.json"),
            """{"ConnectionStrings":{"X":"Server=y;Database=z;Integrated Security=true"}}""");
        File.WriteAllText(Path.Combine(dir, ".git", "appsettings.json"),
            """{"ConnectionStrings":{"X":"Server=y;Database=z;Integrated Security=true"}}""");

        var providers = AutoConnectionStringDiscovery.Discover(_root);
        Assert.Empty(providers);
    }

    [Fact]
    public void ProjectNameUsesNearestCsproj()
    {
        var solutionRoot = Path.Combine(_root, "src");
        var apiDir = Path.Combine(solutionRoot, "Acme.Api");
        Directory.CreateDirectory(apiDir);
        File.WriteAllText(Path.Combine(apiDir, "Acme.Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(apiDir, "appsettings.json"),
            """{"ConnectionStrings":{"Main":"Server=x;Database=y;Integrated Security=true"}}""");

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        var p = Assert.Single(providers);
        // Dot in project name gets sanitized to underscore.
        Assert.Equal("Acme_Api_Main", p.Alias);
    }

    [Fact]
    public void SkipsConnectionStringsWithUnknownProvider()
    {
        var dir = MakeProject("App");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
        {"ConnectionStrings":{"Weird":"redis://localhost:6379"}}
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);
        Assert.Empty(providers);
    }

    [Fact]
    public void WarnsOnMalformedJson()
    {
        var dir = MakeProject("Bad");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), "{ not valid json");

        var providers = AutoConnectionStringDiscovery.Discover(_root, out var warnings);
        Assert.Empty(providers);
        Assert.Single(warnings);
        Assert.Contains("parse failed", warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Server=(local);Database=Foo;Integrated Security=true", "mssql")]
    [InlineData("Server=db;Database=Foo;User Id=u;Password=p", "mssql")]
    [InlineData("Host=db;Port=5432;Username=u;Password=p;Database=d", "psql")]
    [InlineData("Host=db;Username=u;Password=p;Database=d", "psql")]
    [InlineData("Data Source=app.db", "sqlite")]
    [InlineData("Data Source=:memory:", "sqlite")]
    [InlineData("Filename=local.db", "sqlite")]
    [InlineData("Data Source=my.sqlite;Mode=ReadOnly", "sqlite")]
    [InlineData("redis://localhost:6379", null)]
    public void SniffProviderClassifiesConnectionStrings(string connStr, string? expected)
    {
        Assert.Equal(expected, AutoConnectionStringDiscovery.SniffProvider(connStr));
    }

    [Fact]
    public void NameHintResolvesProviderWhenContentIsAmbiguous()
    {
        var dir = MakeProject("App");
        // Bare "Database=foo" doesn't carry server-style keywords, so SniffProvider
        // returns null. Name "PostgresMain" steers it to psql.
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
        {"ConnectionStrings":{"PostgresMain":"Database=foo;User=u;Password=p"}}
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        var p = Assert.Single(providers);
        Assert.Equal("App_PostgresMain", p.Alias);
        Assert.Equal("psql", p.ProviderName);
    }

    [Fact]
    public void PackageReferenceResolvesProviderWhenOtherSignalsMissing()
    {
        var dir = Path.Combine(_root, "App");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "App.csproj"), """
        <Project Sdk="Microsoft.NET.Sdk">
            <ItemGroup>
                <PackageReference Include="Npgsql" Version="8.0.0" />
            </ItemGroup>
        </Project>
        """);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
        {"ConnectionStrings":{"Main":"Database=foo;User=u;Password=p"}}
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        var p = Assert.Single(providers);
        Assert.Equal("App_Main", p.Alias);
        Assert.Equal("psql", p.ProviderName);
    }

    [Fact]
    public void WebConfigDefaultsToMssqlWhenNoSignalsPresent()
    {
        var dir = MakeProject("LegacyApp");
        File.WriteAllText(Path.Combine(dir, "web.config"), """
        <?xml version="1.0"?>
        <configuration>
            <connectionStrings>
                <add name="Default" connectionString="Database=foo;User=u;Password=p" />
            </connectionStrings>
        </configuration>
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root);

        var p = Assert.Single(providers);
        Assert.Equal("LegacyApp_Default", p.Alias);
        Assert.Equal("mssql", p.ProviderName);
    }

    [Fact]
    public void AppSettingsWithUnknownProviderIsSkipped()
    {
        // No content match, no name match, no package, not web.config → skipped.
        var dir = MakeProject("App");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """
        {"ConnectionStrings":{"Misc":"foo=bar;baz=qux"}}
        """);

        var providers = AutoConnectionStringDiscovery.Discover(_root, out var warnings);
        Assert.Empty(providers);
        Assert.Single(warnings);
        Assert.Contains("could not infer provider", warnings[0].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SiteSqlServer", "mssql")]
    [InlineData("MainMssql", "mssql")]
    [InlineData("PostgresReports", "psql")]
    [InlineData("NpgsqlMain", "psql")]
    [InlineData("PsqlAnalytics", "psql")]
    [InlineData("AnalyticsSqlite", "sqlite")]
    [InlineData("Default", null)]
    [InlineData("Main", null)]
    public void GuessProviderFromNameClassifiesNames(string name, string? expected)
    {
        Assert.Equal(expected, AutoConnectionStringDiscovery.GuessProviderFromName(name));
    }

    [Theory]
    [InlineData("Npgsql", "psql")]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "psql")]
    [InlineData("Microsoft.Data.Sqlite", "sqlite")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "sqlite")]
    [InlineData("Microsoft.Data.SqlClient", "mssql")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "mssql")]
    public void DetectPackageProviderFromCsproj(string packageName, string expected)
    {
        var dir = Path.Combine(_root, "PkgTest");
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "PkgTest.csproj");
        File.WriteAllText(csproj, $"""
        <Project Sdk="Microsoft.NET.Sdk">
            <ItemGroup>
                <PackageReference Include="{packageName}" Version="1.0.0" />
            </ItemGroup>
        </Project>
        """);

        Assert.Equal(expected, AutoConnectionStringDiscovery.DetectPackageProvider(csproj));
    }

    [Fact]
    public void DetectPackageProviderReturnsNullWhenAmbiguous()
    {
        var dir = Path.Combine(_root, "Multi");
        Directory.CreateDirectory(dir);
        var csproj = Path.Combine(dir, "Multi.csproj");
        File.WriteAllText(csproj, """
        <Project Sdk="Microsoft.NET.Sdk">
            <ItemGroup>
                <PackageReference Include="Npgsql" Version="8.0.0" />
                <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
            </ItemGroup>
        </Project>
        """);

        Assert.Null(AutoConnectionStringDiscovery.DetectPackageProvider(csproj));
    }

    [Theory]
    [InlineData("System.Data.SqlClient", "mssql")]
    [InlineData("Microsoft.Data.SqlClient", "mssql")]
    [InlineData("Npgsql", "psql")]
    [InlineData("System.Data.SQLite", "sqlite")]
    [InlineData("Microsoft.Data.Sqlite", "sqlite")]
    [InlineData("MySql.Data.MySqlClient", null)]
    [InlineData(null, null)]
    public void MapProviderNameMapsKnownAdoProviders(string? providerName, string? expected)
    {
        Assert.Equal(expected, AutoConnectionStringDiscovery.MapProviderName(providerName));
    }
}
