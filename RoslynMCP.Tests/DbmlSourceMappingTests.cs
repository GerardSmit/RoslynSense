using RoslynMCP.Languages.Dbml.Core;
using Xunit;

namespace RoslynMCP.Tests;

/// <summary>
/// The path rule between a model and its designer, and the reason the rule alone is never allowed to
/// decide anything.
/// </summary>
/// <remarks>
/// LINQ to SQL <em>replaces</em> the extension where WebForms appends to it, so <c>Shop.designer.cs</c>
/// is spelled exactly like the designer of a <c>.resx</c>, a <c>.settings</c> or a WinForms form.
/// Withdrawing an unrelated designer from F12 would be the worst thing this pack could do, so the path
/// only ever proposes and the binder confirms.
/// </remarks>
public class DbmlSourceMappingTests
{
    public DbmlSourceMappingTests() => DbmlSourceMappingService.Clear();

    [Fact]
    public void TheDesignerPathReplacesTheExtension()
    {
        Assert.Equal(
            Path.Combine("C:", "src", "Shop.designer.cs"),
            DbmlSourceMappingService.DesignerPathFor(Path.Combine("C:", "src", "Shop.dbml")));
    }

    [Fact]
    public void TheModelPathIsDerivedBackFromTheDesigner()
    {
        Assert.Equal(
            Path.Combine("C:", "src", "Shop.dbml"),
            DbmlSourceMappingService.ModelPathFor(Path.Combine("C:", "src", "Shop.designer.cs")));
    }

    [Fact]
    public void AFileThatIsNotADesignerProposesNoModel()
    {
        Assert.Null(DbmlSourceMappingService.ModelPathFor(Path.Combine("C:", "src", "Shop.cs")));
    }

    [Fact]
    public void ADesignerIsOnlyClaimedOnceSomethingBoundToIt()
    {
        string settings = Path.Combine("C:", "src", "Settings.designer.cs");
        string shop = Path.Combine("C:", "src", "Shop.designer.cs");

        // Both derive a plausible .dbml path, and only one of them is one. Nothing is claimed until
        // the binder says so, which is what keeps Settings.Designer.cs out of the withdrawal.
        Assert.False(DbmlSourceMappingService.IsBoundDesignerPath(settings));
        Assert.False(DbmlSourceMappingService.IsBoundDesignerPath(shop));

        DbmlSourceMappingService.NoteBound(shop);

        Assert.True(DbmlSourceMappingService.IsBoundDesignerPath(shop));
        Assert.False(DbmlSourceMappingService.IsBoundDesignerPath(settings));
    }

    [Fact]
    public void ForgettingADesignerSaysWhetherThereWasOne()
    {
        // The watched-file handler answers with this: a .resx designer being deleted must not be
        // reported as a change this pack knows anything about.
        string shop = Path.Combine("C:", "src", "Shop.designer.cs");

        DbmlSourceMappingService.NoteBound(shop);

        Assert.True(DbmlSourceMappingService.Forget(shop));
        Assert.False(DbmlSourceMappingService.Forget(shop));
        Assert.False(DbmlSourceMappingService.IsBoundDesignerPath(shop));
    }
}
