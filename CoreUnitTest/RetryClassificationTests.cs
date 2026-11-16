using System.Net;
using Core;

namespace CoreUnitTest;

public class RetryClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]   // 408 - 4xx, but means "try again"
    [InlineData(HttpStatusCode.TooManyRequests)]  // 429 - ditto
    public void Retryable(HttpStatusCode status) =>
        Assert.True(MyFirstQueueFunction.IsRetryable(status));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public void NotRetryable(HttpStatusCode status) =>
        Assert.False(MyFirstQueueFunction.IsRetryable(status));
}
