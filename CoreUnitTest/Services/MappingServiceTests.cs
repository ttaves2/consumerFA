using System.Net;
using System.Text.Json.Nodes;
using Core.Daos;
using Core.Requestors;
using Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CoreUnitTest.Services;

/// <summary>
/// The pipeline end to end, with SQL and HTTP faked: which outcome each failure produces, how
/// the message is to be settled, and that every call writes exactly one outcome line.
/// </summary>
public class MappingServiceTests
{
    private const string EventName = "myFirstQueue";
    private const string Destination = "https://dest.example/orders";
    private const string Mapping = """{"id": orderId}""";

    private sealed class FakeDao(Func<string, InboundMappingConfig> row) : IMySqlDao
    {
        public List<string> Lookups = [];

        public Task<InboundMappingConfig> GetInboundMappingConfigAsync(
            string eventName, CancellationToken cancellationToken)
        {
            Lookups.Add(eventName);
            return Task.FromResult(row(eventName));
        }
    }

    private sealed class FakeRequestor(HttpStatusCode status, string responseBody) : IHttpRequestor
    {
        public List<(Uri Url, string Json, string IdempotencyKey)> Posts = [];

        public Task<HttpResult> PostJsonAsync(
            Uri url, string json, string idempotencyKey, CancellationToken cancellationToken)
        {
            Posts.Add((url, json, idempotencyKey));
            return Task.FromResult(new HttpResult(status, responseBody));
        }
    }

    /// <summary>Keeps each entry as its structured properties, which is what App Insights indexes.</summary>
    private sealed class ListLogger : ILogger<MappingService>
    {
        public List<(LogLevel Level, Exception? Error, Dictionary<string, object?> Values)> Entries = [];

        public IReadOnlyList<Dictionary<string, object?>> Outcomes =>
            Entries.Where(e => e.Values.ContainsKey("Outcome")).Select(e => e.Values).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((
                logLevel,
                exception,
                (state as IEnumerable<KeyValuePair<string, object?>> ?? [])
                    .ToDictionary(p => p.Key, p => p.Value)));
    }

    private sealed record Harness(MappingService Service, FakeDao Dao, FakeRequestor Requestor, ListLogger Logger)
    {
        public Task<ProcessResult> Process(string body, string eventName = EventName) =>
            Service.ProcessAsync(eventName, "sb-1", 2, body, default);

        public string Outcome => (string)Assert.Single(Logger.Outcomes)["Outcome"]!;
    }

    private static Harness Build(
        HttpStatusCode status = HttpStatusCode.OK,
        string responseBody = "",
        Func<string, InboundMappingConfig>? row = null)
    {
        var dao = new FakeDao(row ?? (_ => new(Mapping, Destination)));
        var requestor = new FakeRequestor(status, responseBody);
        var logger = new ListLogger();
        var service = new MappingService(dao, requestor, new MemoryCache(new MemoryCacheOptions()), logger);

        return new(service, dao, requestor, logger);
    }

    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"expected {expected}, got {actual}");

    [Fact]
    public async Task DeliversTheMappedBody()
    {
        var h = Build();

        var result = await h.Process("""{"orderId":"A1"}""");

        Assert.True(result.IsDelivered);
        var post = Assert.Single(h.Requestor.Posts);
        Assert.Equal(Destination, post.Url.ToString());
        AssertJsonEqual("""{"id":"A1"}""", post.Json);
        Assert.Equal("sb-1", post.IdempotencyKey);
        Assert.Equal("Succeeded", h.Outcome);
    }

    [Fact]
    public async Task TheOutcomeLineCarriesWhatSupportSearchesOn()
    {
        var h = Build();

        await h.Process("""{"orderId":"A1"}""");

        var line = Assert.Single(h.Logger.Outcomes);
        Assert.Equal("sb-1", line["MessageId"]);
        Assert.Equal(2, line["DeliveryCount"]);
        Assert.Equal(EventName, line["EventName"]);
        Assert.Equal("""{"orderId":"A1"}""", line["Body"]);
    }

    [Fact]
    public async Task ConvertsXmlBeforeMapping()
    {
        var h = Build(row: _ => new("""{"id": order.id}""", Destination));

        await h.Process("<order><id>A1</id></order>");

        AssertJsonEqual("""{"id":"A1"}""", Assert.Single(h.Requestor.Posts).Json);
    }

    [Fact]
    public async Task DeadLettersMalformedXmlWithoutLoadingConfig()
    {
        var h = Build();

        var result = await h.Process("<order><id>A1</id>");

        Assert.Equal("UnreadableBody", result.DeadLetterReason);
        Assert.Empty(h.Dao.Lookups);
        Assert.Empty(h.Requestor.Posts);
        Assert.Equal("DeadLettered", h.Outcome);
    }

    [Fact]
    public async Task DeadLettersWhenTheMappingFails()
    {
        var h = Build();

        var result = await h.Process("not json");

        Assert.Equal("MappingFailed", result.DeadLetterReason);
        Assert.Empty(h.Requestor.Posts);
        Assert.Equal("DeadLettered", h.Outcome);
    }

    [Fact]
    public async Task ThrowsWhenConfigCannotBeLoaded()
    {
        // Throwing is what leaves the message unsettled, so it comes back once SQL recovers.
        var h = Build(row: _ => throw new InvalidOperationException("SQL is down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Process("""{"orderId":"A1"}"""));

        Assert.Empty(h.Requestor.Posts);
        Assert.Equal("ConfigUnavailable", h.Outcome);
    }

    [Fact]
    public async Task ThrowsWhenTheDestinationUrlIsInvalid()
    {
        var h = Build(row: _ => new(Mapping, "ftp://dest.example"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Process("""{"orderId":"A1"}"""));

        Assert.Equal("ConfigUnavailable", h.Outcome);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ThrowsOnRetryableStatuses(HttpStatusCode status)
    {
        var h = Build(status);

        await Assert.ThrowsAsync<HttpRequestException>(() => h.Process("""{"orderId":"A1"}"""));

        Assert.Equal("Retrying", h.Outcome);
    }

    [Fact]
    public async Task DeadLettersAPayloadTheDestinationRejects()
    {
        var h = Build(HttpStatusCode.BadRequest, "orderId is required");

        var result = await h.Process("""{"orderId":"A1"}""");

        Assert.Equal("Destination returned 400", result.DeadLetterReason);
        Assert.Equal("orderId is required", result.DeadLetterDescription);
        Assert.Equal("DeadLettered", h.Outcome);
    }

    [Fact]
    public async Task LoadsConfigOncePerEventName()
    {
        var h = Build();

        await h.Process("""{"orderId":"A1"}""");
        await h.Process("""{"orderId":"A2"}""");
        await h.Process("""{"orderId":"A3"}""", eventName: "otherQueue");

        Assert.Equal([EventName, "otherQueue"], h.Dao.Lookups);
    }
}
