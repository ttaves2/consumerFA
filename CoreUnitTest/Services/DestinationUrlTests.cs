using Core.Services;

namespace CoreUnitTest.Services;

public class DestinationUrlTests
{
    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("http://example.com:8080/hook?x=1")]
    public void ParseDestination_AcceptsAbsoluteHttpUrls(string url) =>
        Assert.Equal(url, MappingService.ParseDestination(url, "myFirstQueue").ToString());

    [Theory]
    [InlineData(null)]              // NULL column
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/relative/path")]  // no scheme — HttpClient has no BaseAddress
    [InlineData("example.com/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/x")]
    public void ParseDestination_RejectsAnythingElse(string? url)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => MappingService.ParseDestination(url, "myFirstQueue"));

        Assert.Contains("myFirstQueue", ex.Message);
        Assert.Contains("DestinationUrl", ex.Message);
    }
}
