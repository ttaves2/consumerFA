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
        [ServiceBusTrigger(QueueName, Connection = "ServiceBusConnection")] string message,
        CancellationToken cancellationToken)
    {
        var (query, destinationUrl) = await cache.GetOrCreateAsync(QueueName, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ConfigTtl;
            logger.LogInformation("Loading mapping config for {EventName}", QueueName);
            return await LoadConfigAsync(QueueName, cancellationToken);
        })!;

        var mapped = Map(query, message);

        var response = await httpClientFactory.CreateClient().PostAsync(
            destinationUrl,
            new StringContent(mapped, System.Text.Encoding.UTF8, "application/json"),
            cancellationToken);

        logger.LogInformation("Posted to {Url}: {Status}", destinationUrl, (int)response.StatusCode);
        response.EnsureSuccessStatusCode(); // non-2xx -> throw -> Service Bus retry/DLQ
    }

    /// <summary>Applies a compiled JSONata mapping to a message body.</summary>
    public static string Map(JsonataQuery query, string message) => query.Eval(message);

    private static async Task<(JsonataQuery Query, string DestinationUrl)> LoadConfigAsync(
        string eventName, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(
            Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString is not configured."));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(
            "SELECT MappingConfig, DestinationUrl FROM InboundMappingConfig WHERE EventName = @eventName",
            connection);
        command.Parameters.Add("@eventName", System.Data.SqlDbType.NVarChar, 260).Value = eventName;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException($"No InboundMappingConfig row for EventName '{eventName}'.");

        return (new JsonataQuery(reader.GetString(0)), reader.GetString(1));
    }
}
