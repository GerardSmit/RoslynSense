using Xunit;

namespace DebugTestProject;

public class CalculatorTests
{
    [Fact]
    public void Add_ReturnsSum()
    {
        var a = 3;
        var b = 5;
        var result = Calculator.Add(a, b);
        Assert.Equal(8, result);
    }

    [Fact]
    public void Multiply_ReturnsProduct()
    {
        var a = 4;
        var b = 6;
        var result = Calculator.Multiply(a, b);
        Assert.Equal(24, result);
    }
}
