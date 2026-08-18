using RoslynMCP.Languages.Dbml.Core;
using RoslynMCP.Services.Database;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// SQL type → the two attributes a <c>&lt;Column&gt;</c> carries.
/// </summary>
/// <remarks>
/// The mapping is compared against elements SqlMetal wrote, so being merely correct is not enough —
/// it has to be spelled the same. A map that produced <c>nvarchar(50) not null</c> would describe the
/// same column and would make every refresh report every column as changed.
/// </remarks>
public class DbmlTypeMapTests
{
    private static DbColumnSchema Column(
        string sqlType,
        bool nullable = true,
        bool identity = false,
        bool computed = false,
        bool rowVersion = false,
        int? maxLength = null,
        byte? precision = null,
        byte? scale = null) =>
        new("C", sqlType, nullable, IsPrimaryKey: false, identity, computed, rowVersion,
            Ordinal: 1, maxLength, precision, scale);

    [Theory]
    [InlineData("int", "System.Int32")]
    [InlineData("bigint", "System.Int64")]
    [InlineData("bit", "System.Boolean")]
    [InlineData("nvarchar", "System.String")]
    [InlineData("datetime2", "System.DateTime")]
    [InlineData("uniqueidentifier", "System.Guid")]
    [InlineData("varbinary", "System.Data.Linq.Binary")]
    public void TheClrTypeIsTheNonNullableName(string sqlType, string expected)
    {
        // Never System.Int32? — LINQ to SQL carries nullability in CanBeNull and makes the property
        // a Nullable<T> from that, so writing it twice matches nothing SqlMetal produced.
        Assert.Equal(expected, DbmlTypeMap.ClrTypeFor(sqlType));
    }

    [Fact]
    public void AnUnknownTypeFallsBackToObject()
    {
        Assert.Equal("System.Object", DbmlTypeMap.ClrTypeFor("geography"));
    }

    [Fact]
    public void TheDbTypeCarriesLengthNullabilityAndIdentity()
    {
        Assert.Equal(
            "Int NOT NULL IDENTITY",
            DbmlTypeMap.DbTypeFor(Column("int", nullable: false, identity: true)));

        Assert.Equal(
            "NVarChar(50) NOT NULL",
            DbmlTypeMap.DbTypeFor(Column("nvarchar", nullable: false, maxLength: 50)));

        Assert.Equal("NVarChar(MAX)", DbmlTypeMap.DbTypeFor(Column("nvarchar", maxLength: -1)));
    }

    [Fact]
    public void ADecimalStatesItsPrecisionAndScale()
    {
        Assert.Equal(
            "Decimal(18,2) NOT NULL",
            DbmlTypeMap.DbTypeFor(Column("decimal", nullable: false, precision: 18, scale: 2)));
    }

    [Fact]
    public void ATypeWithNoSizeStatesNone()
    {
        // A length written where SqlMetal writes none reads as a change on every refresh of a table
        // nobody touched.
        Assert.Equal("DateTime", DbmlTypeMap.DbTypeFor(Column("datetime")));
        Assert.Equal("Bit NOT NULL", DbmlTypeMap.DbTypeFor(Column("bit", nullable: false)));
    }

    [Fact]
    public void EverythingTheDatabaseFillsInIsGenerated()
    {
        // Three separate things arrive at the same answer, and getting any of them wrong produces
        // the same failure: an insert that sends a value for a column the server owns.
        Assert.True(DbmlTypeMap.IsDbGenerated(Column("int", identity: true)));
        Assert.True(DbmlTypeMap.IsDbGenerated(Column("int", computed: true)));
        Assert.True(DbmlTypeMap.IsDbGenerated(Column("timestamp", rowVersion: true)));
        Assert.False(DbmlTypeMap.IsDbGenerated(Column("int")));
    }
}
