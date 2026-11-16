using System.Net;
using Core.Requestors;

namespace CoreUnitTest.Requestors;

public class HttpRequestorTests
{
    /// <summary>Records what was sent and answers with a canned response.</summary>
    private sealed class StubHandler(HttpStatusCode status, string responseBody = "") : HttpMessageHandler
    {
        public HttpMethod? Method;
        public Uri? Url;
        public string? Body;
        public string? MediaType;
        public string[] IdempotencyKeys = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Captured here because the requestor disposes the request once it returns.
            Method = request.Method;
            Url = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            MediaType = request.Content.Headers.ContentType?.MediaType;
            IdempotencyKeys = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? [.. keys] : [];

            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static Task<HttpResult> Post(StubHandler handler) =>
        new HttpRequestor(new StubFactory(handler)).PostJsonAsync(
            new Uri("https://dest.example/orders"), """{"id":"A1"}""", "sb-1", default);

    [Fact]
    public async Task PostsTheJsonToTheUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        await Post(handler);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://dest.example/orders", handler.Url!.ToString());
        Assert.Equal("""{"id":"A1"}""", handler.Body);
        Assert.Equal("application/json", handler.MediaType);
    }

    [Fact]
    public async Task SendsTheIdempotencyKey()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        await Post(handler);

        Assert.Equal("sb-1", Assert.Single(handler.IdempotencyKeys));
    }

    [Fact]
    public async Task ReportsASuccessWithoutReadingItsBody()
    {
        var result = await Post(new StubHandler(HttpStatusCode.Created, "a large response nobody uses"));

        Assert.True(result.IsSuccess);
        Assert.Equal(HttpStatusCode.Created, result.Status);
        Assert.Equal("", result.Body);
    }

    [Fact]
    public async Task ReturnsTheBodyOfAFailure()
    {
        var result = await Post(new StubHandler(HttpStatusCode.BadRequest, "orderId is required"));

        Assert.False(result.IsSuccess);
        Assert.Equal(HttpStatusCode.BadRequest, result.Status);
        Assert.Equal("orderId is required", result.Body);
    }
}
