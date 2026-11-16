using System.Text;
using Azure.Messaging.ServiceBus;
using Jsonata.Net.Native;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Core;

public class MyFirstQueueFunction(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    ILogger<MyFirstQueueFunction> logger)
{
    private const string QueueName = "myFirstQueue";

    // ponytail: TTL, not invalidation. Config edits go live within 5 min; shorten if that's too slow.
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromMinutes(5);

    [Function(nameof(MyFirstQueueFunction))]
    public async Task Run(
        [ServiceBusTrigger(QueueName, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        var (query, destinationUrl) = await cache.GetOrCreateAsync(QueueName, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ConfigTtl;
            logger.LogInformation("Loading mapping config for {EventName}", QueueName);
            return await LoadConfigAsync(QueueName, cancellationToken);
        })!;

        var mapped = Map(query, message.Body.ToString());

        using var request = new HttpRequestMessage(HttpMethod.Post, destinationUrl)
        {
            Content = new StringContent(mapped, Encoding.UTF8, "application/json")
        };
        // Service Bus redelivers on any failure, so the destination needs a stable key to dedupe on.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", message.MessageId);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);

        logger.LogInformation(
            "Posted {MessageId} to {Url}: {Status}", message.MessageId, destinationUrl, (int)response.StatusCode);
        response.EnsureSuccessStatusCode(); // non-2xx -> throw -> Service Bus retry/DLQ
    }

    /// <summary>Applies a compiled JSONata mapping to a message body.</summary>
    public static string Map(JsonataQuery query, string message) => query.Eval(message);

    /// <summary>Rejects anything that isn't an absolute http(s) URL, naming the offending row.</summary>
    // ponytail: scheme check only. Add a host allowlist if anything less trusted can write this table,
    // since a valid-looking URL can still point at a link-local or internal address.
    public static Uri ParseDestination(string? url, string eventName) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new InvalidOperationException(
                $"InboundMappingConfig.DestinationUrl for EventName '{eventName}' is not an absolute http(s) URL: '{url}'.");

    private static async Task<(JsonataQuery Query, Uri DestinationUrl)> LoadConfigAsync(
        string eventName, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(
            Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString is not configured."));
        await connection.OpenAsync(cancellationToken);

        // TOP 1 + ORDER BY: EventName has no unique constraint, so without this the row is arbitrary.
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 MappingConfig, DestinationUrl
            FROM InboundMappingConfig
            WHERE EventName = @eventName
            ORDER BY Id DESC
            """,
            connection);
        command.Parameters.Add("@eventName", System.Data.SqlDbType.NVarChar, 260).Value = eventName;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"No InboundMappingConfig row for EventName '{eventName}'.");

        return (
            new JsonataQuery(Required(reader, 0, "MappingConfig", eventName)),
            ParseDestination(Required(reader, 1, "DestinationUrl", eventName), eventName));
    }

    private static string Required(SqlDataReader reader, int ordinal, string column, string eventName) =>
        reader.IsDBNull(ordinal)
            ? throw new InvalidOperationException(
                $"InboundMappingConfig.{column} is NULL for EventName '{eventName}'.")
            : reader.GetString(ordinal);
}
