using RoslynMCP.Lsp;
using Xunit;

namespace RoslynMCP.Tests;

public class WeakGenerationTests
{
    [Fact]
    public void TwoWrappersOfOneObjectAreEqualAndHashAlike()
    {
        object target = new();
        var first = new WeakGeneration(target);
        var second = new WeakGeneration(target);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, new WeakGeneration(new object()));
        GC.KeepAlive(target);
    }

    [Fact]
    public void ACollectedTargetEqualsNothingSoTheMemoRecomputes()
    {
        var generation = Wrap();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(generation.IsAlive);
        Assert.False(generation.Equals(generation));
        Assert.Equal(generation.GetHashCode(), generation.GetHashCode());
    }

    // Separate frame, not inlined: the JIT would otherwise be free to keep the object alive.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakGeneration Wrap() => new(new object());
}
