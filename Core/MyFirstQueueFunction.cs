using System.Net;
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

    // One loader at a time per instance, so a cold start doesn't open maxConcurrentCalls connections.
    private static readonly SemaphoreSlim ConfigLock = new(1, 1);

    // host.json sets autoCompleteMessages: false, so every path below must settle the message.
    [Function(nameof(MyFirstQueueFunction))]
    public async Task Run(
        [ServiceBusTrigger(QueueName, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        // Config failures are transient (SQL down, row not deployed yet): throw, leaving the message
        // unsettled so it is redelivered once the lock lapses.
        var (query, destinationUrl) = await GetConfigAsync(cancellationToken);

        string mapped;
        try
        {
            mapped = Map(query, message.Body.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The body or the mapping is wrong; replaying it will fail identically.
            logger.LogError(ex, "Mapping failed for {MessageId}", message.MessageId);
            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "MappingFailed",
                deadLetterErrorDescription: Truncate(ex.Message),
                cancellationToken: cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, destinationUrl)
        {
            Content = new StringContent(mapped, Encoding.UTF8, "application/json")
        };
        // Service Bus redelivers on any failure, so the destination needs a stable key to dedupe on.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", message.MessageId);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);

        logger.LogInformation(
            "Posted {MessageId} to {Url}: {Status}", message.MessageId, destinationUrl, (int)response.StatusCode);

        if (response.IsSuccessStatusCode)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            return;
        }

        if (IsRetryable(response.StatusCode))
            throw new HttpRequestException($"Destination returned {(int)response.StatusCode}, will retry.");

        var body = Truncate(await response.Content.ReadAsStringAsync(cancellationToken));
        logger.LogError(
            "Dead-lettering {MessageId}: destination returned {Status}. {Body}",
            message.MessageId, (int)response.StatusCode, body);
        await messageActions.DeadLetterMessageAsync(
            message,
            deadLetterReason: $"Destination returned {(int)response.StatusCode}",
            deadLetterErrorDescription: body,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// True when replaying the same request could plausibly succeed. 408 and 429 are 4xx but
    /// explicitly mean "try again"; every other 4xx is the destination rejecting this payload.
    /// </summary>
    public static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

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

    // Service Bus caps dead-letter reason/description at 4096 chars and rejects the settlement above it.
    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];

    private async Task<(JsonataQuery Query, Uri DestinationUrl)> GetConfigAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(QueueName, out (JsonataQuery, Uri) config)) return config;

        await ConfigLock.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(QueueName, out config)) return config;

            logger.LogInformation("Loading mapping config for {EventName}", QueueName);
            config = await LoadConfigAsync(QueueName, cancellationToken);
            cache.Set(QueueName, config, ConfigTtl);
            return config;
        }
        finally
        {
            ConfigLock.Release();
        }
    }

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
