using RoslynMCP.Services;
using RoslynMCP.Services.Database;
using RoslynMCP.Tools;
using Xunit;

namespace RoslynMCP.Tests;

public sealed class DatabaseToolTests
{
    private static readonly IOutputFormatter s_fmt = new MarkdownFormatter();
    private static readonly ExecutionPlanStore s_planStore = new();

    // ---------------------------------------------------------------------
    // CLI parser
    // ---------------------------------------------------------------------

    [Fact]
    public void Parse_NoArgs_Empty()
    {
        var result = DbCliParser.Parse([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_Psql_WithAlias()
    {
        var providers = DbCliParser.Parse(["--db", "prod=psql:Host=db;Database=app"]);
        var p = Assert.Single(providers);
        Assert.Equal("prod", p.Alias);
        Assert.Equal("psql", p.ProviderName);
        Assert.IsType<PostgresDbProvider>(p);
    }

    [Fact]
    public void Parse_Mssql_WithAlias()
    {
        var providers = DbCliParser.Parse(["--db", "reports=mssql:Server=(local);Database=R"]);
        var p = Assert.Single(providers);
        Assert.Equal("reports", p.Alias);
        Assert.Equal("mssql", p.ProviderName);
        Assert.IsType<MssqlDbProvider>(p);
    }

    [Fact]
    public void Parse_Sqlite_WithAlias()
    {
        var providers = DbCliParser.Parse(["--db", "local=sqlite::memory:"]);
        var p = Assert.Single(providers);
        Assert.Equal("local", p.Alias);
        Assert.Equal("sqlite", p.ProviderName);
        Assert.IsType<SqliteDbProvider>(p);
    }

    [Fact]
    public void Parse_AliasOmitted_UsesProviderName()
    {
        var providers = DbCliParser.Parse(["--db", "psql:Host=h"]);
        var p = Assert.Single(providers);
        Assert.Equal("psql", p.Alias);
    }

    [Fact]
    public void Parse_SqliteWithWindowsPath_PreservesColon()
    {
        var providers = DbCliParser.Parse(["--db", @"local=sqlite:C:\foo.db"]);
        var p = Assert.Single(providers);
        Assert.Equal("local", p.Alias);
        Assert.IsType<SqliteDbProvider>(p);
    }

    [Fact]
    public void Parse_MultipleDbArgs_AllRegistered()
    {
        var providers = DbCliParser.Parse([
            "--db", "a=sqlite::memory:",
            "--other-flag", "x",
            "--db", "b=sqlite::memory:",
        ]);
        Assert.Equal(2, providers.Count);
        Assert.Equal("a", providers[0].Alias);
        Assert.Equal("b", providers[1].Alias);
    }

    [Fact]
    public void Parse_ProviderAliases_Postgres()
    {
        Assert.IsType<PostgresDbProvider>(DbCliParser.Parse(["--db", "postgres:Host=h"]).Single());
        Assert.IsType<PostgresDbProvider>(DbCliParser.Parse(["--db", "postgresql:Host=h"]).Single());
        Assert.IsType<MssqlDbProvider>(DbCliParser.Parse(["--db", "sqlserver:Server=s"]).Single());
        Assert.IsType<MssqlDbProvider>(DbCliParser.Parse(["--db", "sql:Server=s"]).Single());
    }

    [Fact]
    public void Parse_UnknownProvider_Throws()
    {
        Assert.Throws<ArgumentException>(() => DbCliParser.Parse(["--db", "nope:foo"]));
    }

    [Fact]
    public void Parse_MissingValue_Throws()
    {
        Assert.Throws<ArgumentException>(() => DbCliParser.Parse(["--db"]));
    }

    // ---------------------------------------------------------------------
    // ConnectionStringResolver
    // ---------------------------------------------------------------------

    [Fact]
    public void Resolve_RawString_Passthrough()
    {
        Assert.Equal("Host=x;Database=y", ConnectionStringResolver.Resolve("Host=x;Database=y"));
    }

    [Fact]
    public void Resolve_XmlShorthand_ReturnsConnectionString()
    {
        var path = WriteTempFile(".config", """
            <?xml version="1.0"?>
            <configuration>
              <connectionStrings>
                <add name="SiteSqlServer" connectionString="Server=foo;Database=bar" providerName="System.Data.SqlClient" />
              </connectionStrings>
            </configuration>
            """);
        try
        {
            var resolved = ConnectionStringResolver.Resolve($"xml:{path}#SiteSqlServer");
            Assert.Equal("Server=foo;Database=bar", resolved);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_XmlFullXPath_ReturnsValue()
    {
        var path = WriteTempFile(".config", """
            <?xml version="1.0"?>
            <configuration>
              <connectionStrings>
                <add name="SiteSqlServer" connectionString="Server=foo;Database=bar" />
              </connectionStrings>
            </configuration>
            """);
        try
        {
            var resolved = ConnectionStringResolver.Resolve(
                $"xml:{path}#//connectionStrings/add[@name='SiteSqlServer']/@connectionString");
            Assert.Equal("Server=foo;Database=bar", resolved);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_XmlMissingNode_Throws()
    {
        var path = WriteTempFile(".config", """
            <?xml version="1.0"?>
            <configuration><connectionStrings/></configuration>
            """);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ConnectionStringResolver.Resolve($"xml:{path}#Missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_JsonShorthand_ReturnsConnectionString()
    {
        var path = WriteTempFile(".json", """
            {
              "ConnectionStrings": {
                "MyProject": "Host=db;Database=app"
              }
            }
            """);
        try
        {
            var resolved = ConnectionStringResolver.Resolve($"json:{path}#MyProject");
            Assert.Equal("Host=db;Database=app", resolved);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_JsonFullPath_ReturnsValue()
    {
        var path = WriteTempFile(".json", """
            {
              "Databases": {
                "Primary": { "ConnStr": "Host=x" }
              }
            }
            """);
        try
        {
            var resolved = ConnectionStringResolver.Resolve($"json:{path}#$.Databases.Primary.ConnStr");
            Assert.Equal("Host=x", resolved);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_JsonMissingKey_Throws()
    {
        var path = WriteTempFile(".json", """{"ConnectionStrings":{}}""");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ConnectionStringResolver.Resolve($"json:{path}#Missing"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Resolve_MissingHash_Throws()
    {
        Assert.Throws<ArgumentException>(() => ConnectionStringResolver.Resolve("xml:foo.config"));
    }

    [Fact]
    public void Parse_DbFlag_WithXmlReference_ResolvesConnectionString()
    {
        var path = WriteTempFile(".config", """
            <?xml version="1.0"?>
            <configuration>
              <connectionStrings>
                <add name="X" connectionString="Server=(local);Database=Test" />
              </connectionStrings>
            </configuration>
            """);
        try
        {
            var providers = DbCliParser.Parse(["--db", $"prod=mssql:xml:{path}#X"]);
            var p = Assert.Single(providers);
            Assert.Equal("prod", p.Alias);
            Assert.IsType<MssqlDbProvider>(p);
        }
        finally { File.Delete(path); }
    }

    private static string WriteTempFile(string ext, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rmcp-{Guid.NewGuid():N}{ext}");
        File.WriteAllText(path, content);
        return path;
    }

    // ---------------------------------------------------------------------
    // PathVariableExpander
    // ---------------------------------------------------------------------

    [Fact]
    public void Expand_NoPlaceholders_Unchanged()
    {
        Assert.Equal("foo/bar.xml", PathVariableExpander.Expand("foo/bar.xml"));
    }

    [Fact]
    public void Expand_EnvVar_Substituted()
    {
        Environment.SetEnvironmentVariable("RMCP_TEST_VAR", "hello");
        try
        {
            Assert.Equal("hello/x.xml", PathVariableExpander.Expand("${env:RMCP_TEST_VAR}/x.xml"));
        }
        finally { Environment.SetEnvironmentVariable("RMCP_TEST_VAR", null); }
    }

    [Fact]
    public void Expand_MissingEnvVar_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PathVariableExpander.Expand("${env:RMCP_DEFINITELY_UNSET_XYZ}"));
    }

    [Fact]
    public void Expand_UnknownPlaceholder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PathVariableExpander.Expand("${bogus}"));
    }

    [Fact]
    public void Expand_GitRoot_FromSubdirectory()
    {
        var root = CreateTempRepoWithGit(out var subDir);
        try
        {
            var expanded = PathVariableExpander.Expand("${gitRoot}/app.config", subDir);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "app.config")), Path.GetFullPath(expanded));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Expand_SolutionRoot_FindsSln()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "My.sln"), "");
        var sub = Directory.CreateDirectory(Path.Combine(root, "src", "App")).FullName;
        try
        {
            var expanded = PathVariableExpander.Expand("${solutionRoot}/cfg.json", sub);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "cfg.json")), Path.GetFullPath(expanded));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Expand_SolutionRoot_FindsSlnx()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "My.slnx"), "<Solution/>");
        var sub = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        try
        {
            var expanded = PathVariableExpander.Expand("${solutionRoot}/cfg.json", sub);
            Assert.Equal(Path.GetFullPath(Path.Combine(root, "cfg.json")), Path.GetFullPath(expanded));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveFilePath_PlainRelative_FallsBackToSolutionRoot()
    {
        var root = CreateTempDir();
        File.WriteAllText(Path.Combine(root, "My.sln"), "");
        var cfgDir = Directory.CreateDirectory(Path.Combine(root, "src", "App")).FullName;
        var cfgPath = Path.Combine(cfgDir, "web.config");
        File.WriteAllText(cfgPath, "<x/>");
        var cwd = Directory.CreateDirectory(Path.Combine(root, "other", "place")).FullName;
        try
        {
            var resolved = PathVariableExpander.ResolveFilePath("src/App/web.config", cwd);
            Assert.Equal(Path.GetFullPath(cfgPath), Path.GetFullPath(resolved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveFilePath_PlainRelative_FallsBackToGitRoot()
    {
        var root = CreateTempRepoWithGit(out var subDir);
        var cfgPath = Path.Combine(root, "src", "WebApp", "web.config");
        File.WriteAllText(cfgPath, "<x/>");
        var cwd = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
        try
        {
            var resolved = PathVariableExpander.ResolveFilePath("src/WebApp/web.config", cwd);
            Assert.Equal(Path.GetFullPath(cfgPath), Path.GetFullPath(resolved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveFilePath_ExistsAtCwd_PrefersCwd()
    {
        var root = CreateTempRepoWithGit(out _);
        var cwdFile = Path.Combine(root, "cfg.json");
        File.WriteAllText(cwdFile, "{}");
        File.WriteAllText(Path.Combine(root, "x.json"), "{}"); // at git root too
        try
        {
            var resolved = PathVariableExpander.ResolveFilePath("cfg.json", root);
            Assert.Equal(Path.GetFullPath(cwdFile), Path.GetFullPath(resolved));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ResolveFilePath_PlaceholderDisablesFallback()
    {
        var root = CreateTempRepoWithGit(out _);
        // File lives at git root only, but placeholder uses solutionRoot which doesn't exist → throws
        var cwd = Directory.CreateDirectory(Path.Combine(root, "sub")).FullName;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                PathVariableExpander.ResolveFilePath("${solutionRoot}/web.config", cwd));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Expand_GitRoot_NoRepo_Throws()
    {
        var dir = CreateTempDir();
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                PathVariableExpander.Expand("${gitRoot}/x", dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rmcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string CreateTempRepoWithGit(out string subDir)
    {
        var root = CreateTempDir();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        subDir = Directory.CreateDirectory(Path.Combine(root, "src", "WebApp")).FullName;
        return root;
    }

    // ---------------------------------------------------------------------
    // SQLite in-memory end-to-end
    // ---------------------------------------------------------------------

    private static DbConnectionRegistry NewSqliteRegistry(string alias = "mem")
    {
        var cs = $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
        return new DbConnectionRegistry([new SqliteDbProvider(alias, cs)]);
    }

    private static async Task SeedAsync(DbConnectionRegistry reg, string alias = "mem")
    {
        var p = reg.Get(alias)!;
        await p.ExecuteNonQueryAsync("CREATE TABLE t(id INTEGER PRIMARY KEY, name TEXT, val INTEGER)", null, default);
        await p.ExecuteNonQueryAsync("INSERT INTO t(name, val) VALUES ('a', 1), ('b', 2), ('c', NULL)", null, default);
    }

    [Fact]
    public async Task DbQuery_ReturnsRows()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbQuery("mem", "SELECT name, val FROM t ORDER BY id", reg, s_fmt, s_planStore);

        Assert.Contains("a", output);
        Assert.Contains("b", output);
        Assert.Contains("c", output);
    }

    [Fact]
    public async Task DbQuery_NullValue_RenderedAsNullLiteral()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbQuery("mem", "SELECT val FROM t WHERE name='c'", reg, s_fmt, s_planStore);
        Assert.Contains("(null)", output);
    }

    [Fact]
    public async Task DbQuery_MaxRowsExceeded_ReportsTruncation()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbQuery("mem", "SELECT * FROM t", reg, s_fmt, s_planStore, maxRows: 2);
        Assert.Contains("maxRows", output);
    }

    [Fact]
    public async Task DbQuery_WithParameters_Bound()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbQuery(
            "mem", "SELECT name FROM t WHERE val = @v", reg, s_fmt, s_planStore, parameters: "{\"@v\": 2}");

        Assert.Contains("b", output);
        Assert.DoesNotContain("| a |", output);
    }

    [Fact]
    public async Task DbExecute_ReturnsAffectedRows()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbExecute(
            "mem", "UPDATE t SET name = 'zz' WHERE val = 1", reg, s_fmt);

        Assert.Contains("Rows affected", output);
        Assert.Contains("1", output);
    }

    [Fact]
    public async Task DbQuery_InvalidAlias_ReturnsError()
    {
        var reg = NewSqliteRegistry();
        var output = await DatabaseTool.DbQuery("bogus", "SELECT 1", reg, s_fmt, s_planStore);
        Assert.StartsWith("Error:", output);
    }

    [Fact]
    public async Task DbQuery_SyntaxError_ReturnsCleanError()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbQuery("mem", "SELECT !bogus", reg, s_fmt, s_planStore);
        Assert.StartsWith("SQL Error:", output);
    }

    [Fact]
    public async Task DbListTables_ReturnsTableNames()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbListTables("mem", reg, s_fmt);
        Assert.Contains("t", output);
    }

    [Fact]
    public async Task DbDescribeTable_ReturnsColumns()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);

        var output = await DatabaseTool.DbDescribeTable("mem", "t", reg, s_fmt);
        Assert.Contains("id", output);
        Assert.Contains("name", output);
        Assert.Contains("val", output);
    }

    [Fact]
    public async Task DbDescribeTable_InvalidName_ReturnsError()
    {
        var reg = NewSqliteRegistry();
        var output = await DatabaseTool.DbDescribeTable("mem", "t; DROP TABLE x", reg, s_fmt);
        Assert.StartsWith("SQL Error:", output);
    }

    [Fact]
    public void DbListConnections_EmptyRegistry_ReportsEmpty()
    {
        var reg = new DbConnectionRegistry([]);
        var output = DatabaseTool.DbListConnections(reg, s_fmt);
        Assert.Contains("No database connections", output);
    }

    [Fact]
    public void DbListConnections_WithConnections_ListsThem()
    {
        var reg = NewSqliteRegistry("foo");
        var output = DatabaseTool.DbListConnections(reg, s_fmt);
        Assert.Contains("foo", output);
        Assert.Contains("sqlite", output);
    }

    [Fact]
    public async Task DbQuery_InvalidParametersJson_ReturnsError()
    {
        var reg = NewSqliteRegistry();
        await SeedAsync(reg);
        var output = await DatabaseTool.DbQuery("mem", "SELECT 1", reg, s_fmt, s_planStore, parameters: "{bad json");
        Assert.StartsWith("Error:", output);
    }

    // ---------------------------------------------------------------------
    // Runtime add/remove
    // ---------------------------------------------------------------------

    private static string NewSharedMemoryConnStr() =>
        $"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared";

    [Fact]
    public async Task DbAddConnection_Sqlite_Registers()
    {
        var reg = new DbConnectionRegistry([]);
        var output = await DatabaseTool.DbAddConnection(
            "newdb", "sqlite", NewSharedMemoryConnStr(), reg, s_fmt);

        Assert.Contains("Added", output);
        Assert.NotNull(reg.Get("newdb"));
        Assert.IsType<SqliteDbProvider>(reg.Get("newdb"));
    }

    [Fact]
    public async Task DbAddConnection_ExistingAlias_WithoutReplace_Errors()
    {
        var reg = NewSqliteRegistry("mem");
        var output = await DatabaseTool.DbAddConnection(
            "mem", "sqlite", NewSharedMemoryConnStr(), reg, s_fmt);

        Assert.StartsWith("Error:", output);
        Assert.Contains("already exists", output);
    }

    [Fact]
    public async Task DbAddConnection_ExistingAlias_WithReplace_Replaces()
    {
        var reg = NewSqliteRegistry("mem");
        var first = reg.Get("mem");

        var newConnStr = NewSharedMemoryConnStr();
        var output = await DatabaseTool.DbAddConnection(
            "mem", "sqlite", newConnStr, reg, s_fmt, replaceExisting: true);

        Assert.Contains("Added", output);
        Assert.NotSame(first, reg.Get("mem"));
    }

    [Fact]
    public async Task DbAddConnection_UnknownProvider_Errors()
    {
        var reg = new DbConnectionRegistry([]);
        var output = await DatabaseTool.DbAddConnection("x", "bogus", "x=y", reg, s_fmt);
        Assert.StartsWith("Error:", output);
    }

    [Fact]
    public async Task DbAddConnection_BadConnectionString_Errors()
    {
        var reg = new DbConnectionRegistry([]);
        var output = await DatabaseTool.DbAddConnection(
            "bad", "sqlite", "Data Source=|?|?|:invalid/path/does/not/exist/\\/:", reg, s_fmt);

        Assert.StartsWith("Error:", output);
        Assert.Null(reg.Get("bad"));
    }

    [Fact]
    public async Task DbAddConnection_EmptyAlias_Errors()
    {
        var reg = new DbConnectionRegistry([]);
        var output = await DatabaseTool.DbAddConnection("", "sqlite", NewSharedMemoryConnStr(), reg, s_fmt);
        Assert.StartsWith("Error:", output);
    }

    [Fact]
    public void DbRemoveConnection_Existing_Removes()
    {
        var reg = NewSqliteRegistry("gone");
        var output = DatabaseTool.DbRemoveConnection("gone", reg, s_fmt);

        Assert.Contains("Removed", output);
        Assert.Null(reg.Get("gone"));
    }

    [Fact]
    public void DbRemoveConnection_Missing_Errors()
    {
        var reg = new DbConnectionRegistry([]);
        var output = DatabaseTool.DbRemoveConnection("nope", reg, s_fmt);
        Assert.StartsWith("Error:", output);
    }

    [Fact]
    public async Task DbAddConnection_XmlReference_ResolvesConnectionString()
    {
        var xmlPath = WriteTempFile(".config", """
            <?xml version="1.0"?>
            <configuration>
              <connectionStrings>
                <add name="Local" connectionString="Data Source=:memory:" />
              </connectionStrings>
            </configuration>
            """);
        try
        {
            var reg = new DbConnectionRegistry([]);
            var output = await DatabaseTool.DbAddConnection(
                "xref", "sqlite", $"xml:{xmlPath}#Local", reg, s_fmt);

            Assert.Contains("Added", output);
            Assert.NotNull(reg.Get("xref"));
        }
        finally { File.Delete(xmlPath); }
    }
}

public sealed class PostgresIntegrationTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _fx;
    private static readonly IOutputFormatter s_fmt = new MarkdownFormatter();
    private static readonly ExecutionPlanStore s_planStore = new();

    public PostgresIntegrationTests(PostgresContainerFixture fx) => _fx = fx;

    [RequiresPsqlFact]
    public async Task Postgres_SelectOne_ReturnsRow()
    {
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        var output = await DatabaseTool.DbQuery("pg", "SELECT 1 AS n", reg, s_fmt, s_planStore);
        Assert.Contains("1", output);
    }

    [RequiresPsqlFact]
    public async Task Postgres_CreateInsertSelect_RoundTrip()
    {
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await DatabaseTool.DbExecute("pg", "CREATE TABLE IF NOT EXISTS widgets(id serial primary key, name text)", reg, s_fmt);
        await DatabaseTool.DbExecute("pg", "INSERT INTO widgets(name) VALUES (@n)", reg, s_fmt, parameters: "{\"@n\": \"gizmo\"}");
        var output = await DatabaseTool.DbQuery("pg", "SELECT name FROM widgets", reg, s_fmt, s_planStore);
        Assert.Contains("gizmo", output);
    }

    [RequiresPsqlFact]
    public async Task Postgres_IncludeExecutionPlan_CapturesPlan()
    {
        await EnsurePlanFixture();
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        var output = await DatabaseTool.DbQuery(
            "pg", "SELECT id, name FROM plan_widgets WHERE id < 100", reg, s_fmt, store, includeExecutionPlan: true);
        Assert.Contains("Execution Plan ID", output);
        Assert.Contains("plan-", output);
    }

    [RequiresPsqlFact]
    public async Task Postgres_DbPlanSummary_ReturnsCost()
    {
        await EnsurePlanFixture();
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "pg", "SELECT id FROM plan_widgets", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var summary = DatabaseTool.DbPlanSummary(planId, store, s_fmt);
        Assert.Contains("Total estimated cost", summary);
        Assert.Contains("Operator count", summary);
        Assert.Contains("Format", summary);
        Assert.Contains("json", summary);
    }

    [RequiresPsqlFact]
    public async Task Postgres_DbPlanOperators_ListsNodes()
    {
        await EnsurePlanFixture();
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "pg", "SELECT id FROM plan_widgets ORDER BY id", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var ops = DatabaseTool.DbPlanOperators(planId, store, s_fmt);
        Assert.Contains("PhysicalOp", ops);
        Assert.Contains("Scan", ops);
    }

    [RequiresPsqlFact]
    public async Task Postgres_DbPlanQuery_JsonPath_FindsNodes()
    {
        await EnsurePlanFixture();
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "pg", "SELECT id FROM plan_widgets", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var result = DatabaseTool.DbPlanQuery(planId, "$..['Node Type']", store, s_fmt);
        Assert.DoesNotContain("No nodes matched", result);
    }

    [RequiresPsqlFact]
    public async Task Postgres_DbPlanSuggestIndexes_WithoutHypopg_ReturnsHeuristicAndHint()
    {
        await EnsurePlanFixture();
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await EnsureHypopgAbsentAsync(reg);
        var store = new ExecutionPlanStore();

        await DatabaseTool.DbQuery(
            "pg", "SELECT id FROM plan_widgets WHERE name = 'w42'",
            reg, s_fmt, store, includeExecutionPlan: true);
        var planId = store.List().First().Id;

        var output = await DatabaseTool.DbPlanSuggestIndexes(planId, store, reg, s_fmt);
        Assert.Contains("Heuristic candidates", output);
        Assert.Contains("plan_widgets", output);
        Assert.Contains("hypopg", output);
        Assert.Contains("not installed", output);
        Assert.Contains("CREATE EXTENSION hypopg", output);
    }

    [RequiresPsqlFact]
    public async Task Postgres_DbPlanSuggestIndexes_WithHypopg_Validates()
    {
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        if (!await EnsureHypopgInstalledAsync(reg))
            return; // hypopg not available in container; skip

        await EnsurePlanFixture();
        var store = new ExecutionPlanStore();
        await DatabaseTool.DbQuery(
            "pg", "SELECT id FROM plan_widgets WHERE name = 'w42'",
            reg, s_fmt, store, includeExecutionPlan: true);
        var planId = store.List().First().Id;

        var output = await DatabaseTool.DbPlanSuggestIndexes(planId, store, reg, s_fmt);
        Assert.Contains("Heuristic candidates", output);
        Assert.Contains("hypopg validated", output);
    }

    private async Task EnsureHypopgAbsentAsync(DbConnectionRegistry reg)
    {
        try
        {
            await DatabaseTool.DbExecute("pg", "DROP EXTENSION IF EXISTS hypopg", reg, s_fmt);
        }
        catch { }
        ClearHypopgCache(reg);
    }

    private async Task<bool> EnsureHypopgInstalledAsync(DbConnectionRegistry reg)
    {
        var output = await DatabaseTool.DbExecute("pg", "CREATE EXTENSION IF NOT EXISTS hypopg", reg, s_fmt);
        ClearHypopgCache(reg);
        if (output.StartsWith("SQL Error", StringComparison.Ordinal))
            return false;
        var provider = (PostgresDbProvider)reg.Get("pg")!;
        return await provider.IsHypopgInstalledAsync(default);
    }

    private static void ClearHypopgCache(DbConnectionRegistry reg)
    {
        var provider = reg.Get("pg");
        var field = provider?.GetType().GetField("_hypopgInstalled",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(provider, null);
    }

    private async Task EnsurePlanFixture()
    {
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await DatabaseTool.DbExecute("pg",
            "CREATE TABLE IF NOT EXISTS plan_widgets(id serial primary key, name text)", reg, s_fmt);
        await DatabaseTool.DbExecute("pg",
            "INSERT INTO plan_widgets(name) SELECT 'w'||g FROM generate_series(1,200) g " +
            "ON CONFLICT DO NOTHING", reg, s_fmt);
    }

    [RequiresPsqlFact]
    public async Task Postgres_DescribeTable_ReturnsColumns()
    {
        var reg = new DbConnectionRegistry([new PostgresDbProvider("pg", _fx.ConnectionString)]);
        await DatabaseTool.DbExecute("pg", "CREATE TABLE IF NOT EXISTS widgets_desc(id serial primary key, name text, amount integer)", reg, s_fmt);
        var output = await DatabaseTool.DbDescribeTable("pg", "widgets_desc", reg, s_fmt);
        Assert.Contains("name", output);
        Assert.Contains("amount", output);
    }
}

public sealed class MssqlIntegrationTests : IClassFixture<MssqlContainerFixture>
{
    private readonly MssqlContainerFixture _fx;
    private static readonly IOutputFormatter s_fmt = new MarkdownFormatter();
    private static readonly ExecutionPlanStore s_planStore = new();

    public MssqlIntegrationTests(MssqlContainerFixture fx) => _fx = fx;

    [RequiresMssqlFact]
    public async Task Mssql_SelectOne_ReturnsRow()
    {
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        var output = await DatabaseTool.DbQuery("sql", "SELECT 1 AS n", reg, s_fmt, s_planStore);
        Assert.Contains("1", output);
    }

    [RequiresMssqlFact]
    public async Task Mssql_CreateInsertSelect_RoundTrip()
    {
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        await DatabaseTool.DbExecute("sql", "CREATE TABLE widgets(id int identity primary key, name nvarchar(50))", reg, s_fmt);
        await DatabaseTool.DbExecute("sql", "INSERT INTO widgets(name) VALUES (@n)", reg, s_fmt, parameters: "{\"@n\": \"gizmo\"}");
        var output = await DatabaseTool.DbQuery("sql", "SELECT name FROM widgets", reg, s_fmt, s_planStore);
        Assert.Contains("gizmo", output);
    }

    [RequiresMssqlFact]
    public async Task Mssql_ListTables_IncludesCreated()
    {
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        await DatabaseTool.DbExecute("sql", "CREATE TABLE listed_widgets(id int)", reg, s_fmt);
        var output = await DatabaseTool.DbListTables("sql", reg, s_fmt, "dbo");
        Assert.Contains("listed_widgets", output);
    }

    [RequiresMssqlFact]
    public async Task Mssql_DbQuery_WithIncludeExecutionPlan_ReturnsPlanId()
    {
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        var output = await DatabaseTool.DbQuery(
            "sql", "SELECT TOP 3 name FROM sys.tables", reg, s_fmt, store, includeExecutionPlan: true);
        Assert.Contains("Execution Plan ID", output);
        Assert.Contains("plan-", output);
    }

    [RequiresMssqlFact]
    public async Task Mssql_DbPlanSummary_ReturnsCost()
    {
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "sql", "SELECT TOP 5 name FROM sys.tables", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var summary = DatabaseTool.DbPlanSummary(planId, store, s_fmt);
        Assert.Contains("Total estimated cost", summary);
        Assert.Contains("Operator count", summary);
    }

    [RequiresMssqlFact]
    public async Task Mssql_DbPlanOperators_ListsRelOps()
    {
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "sql", "SELECT TOP 10 * FROM INFORMATION_SCHEMA.TABLES", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var ops = DatabaseTool.DbPlanOperators(planId, store, s_fmt);
        Assert.Contains("PhysicalOp", ops);
    }

    [RequiresMssqlFact]
    public async Task Mssql_DbPlanWarnings_OnSimpleQuery_NoWarnings()
    {
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "sql", "SELECT TOP 1 name FROM sys.tables", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var w = DatabaseTool.DbPlanWarnings(planId, store, s_fmt);
        Assert.Contains("No warnings", w);
    }

    [RequiresMssqlFact]
    public async Task Mssql_DbPlanQuery_FindsRelOps()
    {
        var store = new ExecutionPlanStore();
        var reg = new DbConnectionRegistry([new MssqlDbProvider("sql", _fx.ConnectionString)]);
        await DatabaseTool.DbQuery(
            "sql", "SELECT TOP 5 name FROM sys.tables", reg, s_fmt, store, includeExecutionPlan: true);

        var planId = store.List().First().Id;
        var result = DatabaseTool.DbPlanQuery(planId, "//sp:RelOp", store, s_fmt);
        Assert.DoesNotContain("No nodes matched", result);
        Assert.Contains("RelOp", result);
    }

    [Fact]
    public void Mssql_DbPlanSummary_UnknownId_ReturnsNotFound()
    {
        var store = new ExecutionPlanStore();
        var output = DatabaseTool.DbPlanSummary("plan-nonexistent", store, s_fmt);
        Assert.Contains("Plan not found", output);
    }
}
