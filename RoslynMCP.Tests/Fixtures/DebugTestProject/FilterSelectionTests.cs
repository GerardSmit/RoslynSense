using Xunit;

namespace DebugTestProject;

public class FilterSelectionTests
{
    [Fact]
    public void ChoosesOne() { }

    // Selecting ChoosesOne must not run this similarly named method.
    [Fact]
    public void ChoosesOneMore() { }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheoryWithRows(int value) => Assert.InRange(value, 1, 2);
}
