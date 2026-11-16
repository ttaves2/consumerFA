using Core;

namespace CoreUnitTest;

public class TruncateTests
{
    [Fact]
    public void Truncate_LeavesShortValuesAlone() =>
        Assert.Equal("abc", MyFirstQueueFunction.Truncate("abc", 10));

    [Fact]
    public void Truncate_LeavesExactLengthAlone() =>
        Assert.Equal("abcde", MyFirstQueueFunction.Truncate("abcde", 5));

    [Fact]
    public void Truncate_MarksWhatItCut()
    {
        var result = MyFirstQueueFunction.Truncate(new string('x', 50), 5);

        Assert.Equal("xxxxx...[truncated]", result);
    }
}
