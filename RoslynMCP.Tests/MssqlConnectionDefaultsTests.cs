using Microsoft.Data.SqlClient;
using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// Covers the TrustServerCertificate default.
/// </summary>
/// <remarks>
/// Microsoft.Data.SqlClient defaults to <c>Encrypt=true</c>, while the
/// <c>System.Data.SqlClient</c> a .NET Framework app uses defaults to <c>Encrypt=false</c>. A
/// connection string lifted from a working web.config therefore fails certificate validation here
/// unless the certificate is trusted.
/// </remarks>
public class MssqlConnectionDefaultsTests
{
    [Theory]
    [InlineData("Server=.;Database=App;Integrated Security=true")]
    [InlineData("Data Source=localhost;Initial Catalog=App;User ID=sa;Password=p")]
    public void WhenNotSpecifiedThenTheServerCertificateIsTrusted(string connectionString)
    {
        var result = new SqlConnectionStringBuilder(MssqlDbProvider.ApplyDefaults(connectionString));

        Assert.True(result.TrustServerCertificate);
    }

    [Theory]
    // Explicit opt-out, in each spelling SqlClient accepts.
    [InlineData("Server=.;Database=App;TrustServerCertificate=False")]
    [InlineData("Server=.;Database=App;Trust Server Certificate=False")]
    [InlineData("Server=.;Database=App;trustservercertificate=false")]
    public void WhenExplicitlyDisabledThenItIsRespected(string connectionString)
    {
        // Someone who turned validation on for a production server must keep it.
        var result = new SqlConnectionStringBuilder(MssqlDbProvider.ApplyDefaults(connectionString));

        Assert.False(result.TrustServerCertificate);
    }

    [Theory]
    [InlineData("Server=.;Database=App;TrustServerCertificate=True")]
    [InlineData("Server=.;Database=App;Trust Server Certificate=True")]
    public void WhenAlreadyEnabledThenItStaysEnabled(string connectionString)
    {
        var result = new SqlConnectionStringBuilder(MssqlDbProvider.ApplyDefaults(connectionString));

        Assert.True(result.TrustServerCertificate);
    }

    [Fact]
    public void WhenDefaultsAppliedThenTheRestOfTheConnectionStringSurvives()
    {
        var result = new SqlConnectionStringBuilder(MssqlDbProvider.ApplyDefaults(
            "Server=db.example.com,1433;Initial Catalog=Reporting;User ID=app;Password=s3cr3t;Application Name=Thing"));

        Assert.Equal("db.example.com,1433", result.DataSource);
        Assert.Equal("Reporting", result.InitialCatalog);
        Assert.Equal("app", result.UserID);
        Assert.Equal("s3cr3t", result.Password);
        Assert.Equal("Thing", result.ApplicationName);
        Assert.True(result.TrustServerCertificate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("this is not a connection string===")]
    [InlineData("Server=.;NotARealKeyword=1")]
    [InlineData("Server='unterminated")]
    public void WhenConnectionStringIsMalformedThenItIsNotRejectedHere(string connectionString)
    {
        // Whatever is wrong with it, SqlClient should be the one to report it when the connection
        // is opened — this must not throw and turn a clear connection error into a startup crash.
        var result = Record.Exception(() => MssqlDbProvider.ApplyDefaults(connectionString));

        Assert.Null(result);
    }
}
