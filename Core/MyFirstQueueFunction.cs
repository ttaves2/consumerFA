using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using Newtonsoft.Json;
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

    // Service Bus caps the dead-letter description at 4096 chars and rejects the settlement above it.
    private const int MaxReasonChars = 1000;

    // Enough to hold the body App Insights is searched by; its custom dimensions cap out at 8192.
    private const int MaxBodyChars = 4000;

    // A batch payload can carry hundreds of ids; keep the dimension count and each value bounded.
    private const int MaxTraceFields = 10;
    private const int MaxTraceValueChars = 200;

    /// <summary>Blob container holding bodies too large to log inline.</summary>
    private const string ArchiveContainer = "message-bodies";

    // ponytail: TTL, not invalidation. Config edits go live within 5 min; shorten if that's too slow.
    private static readonly TimeSpan ConfigTtl = TimeSpan.FromMinutes(5);

    // One loader at a time per instance, so a cold start doesn't open maxConcurrentCalls connections.
    private static readonly SemaphoreSlim ConfigLock = new(1, 1);

    // host.json sets autoCompleteMessages: false, so every path below must settle the message.
    // Every path also emits exactly one Log(...) line carrying Outcome and Body, which is what
    // support searches on. See the "Tracing a message" section of the README.
    [Function(nameof(MyFirstQueueFunction))]
    public async Task Run(
        [ServiceBusTrigger(QueueName, Connection = "ServiceBusConnection")] ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var raw = message.Body.ToString();
        var body = await BodyForLogAsync(Archive.Value, raw, message.MessageId, logger, cancellationToken);

        // Convert before anything else, so the id fields below are available to every outcome
        // and not just the ones that get as far as mapping.
        string json;
        try
        {
            json = ToJson(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(LogLevel.Error, ex, message, "DeadLettered", started, body,
                "Body is neither valid JSON nor valid XML: " + ex.Message);
            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "UnreadableBody",
                deadLetterErrorDescription: Truncate(ex.Message, MaxReasonChars),
                cancellationToken: cancellationToken);
            return;
        }

        // Every log line from here on carries the body's id/number fields as custom dimensions.
        // Program.cs must set IncludeScopes, or they are dropped silently.
        using var traceFields = logger.BeginScope(ExtractTraceFields(json));

        (JsonataQuery Query, Uri DestinationUrl) config;
        try
        {
            config = await GetConfigAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Transient (SQL down, row not deployed yet). Leave the message unsettled so it comes
            // back when the lock lapses, rather than dead-lettering a working payload.
            Log(LogLevel.Error, ex, message, "ConfigUnavailable", started, body, ex.Message);
            throw;
        }

        string mapped;
        try
        {
            mapped = Map(config.Query, json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The body or the mapping is wrong; replaying it will fail identically.
            Log(LogLevel.Error, ex, message, "DeadLettered", started, body, "Mapping failed: " + ex.Message);
            await messageActions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "MappingFailed",
                deadLetterErrorDescription: Truncate(ex.Message, MaxReasonChars),
                cancellationToken: cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, config.DestinationUrl)
        {
            Content = new StringContent(mapped, Encoding.UTF8, "application/json")
        };
        // Service Bus redelivers on any failure, so the destination needs a stable key to dedupe on.
        request.Headers.TryAddWithoutValidation("Idempotency-Key", message.MessageId);

        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
        {
            await messageActions.CompleteMessageAsync(message, cancellationToken);
            Log(LogLevel.Information, null, message, "Succeeded", started, body,
                $"POST {config.DestinationUrl} returned {status}");
            return;
        }

        var responseBody = Truncate(await response.Content.ReadAsStringAsync(cancellationToken), MaxReasonChars);

        if (IsRetryable(response.StatusCode))
        {
            Log(LogLevel.Warning, null, message, "Retrying", started, body,
                $"POST {config.DestinationUrl} returned {status}: {responseBody}");
            throw new HttpRequestException($"Destination returned {status}, will retry.");
        }

        Log(LogLevel.Error, null, message, "DeadLettered", started, body,
            $"POST {config.DestinationUrl} returned {status}: {responseBody}");
        await messageActions.DeadLetterMessageAsync(
            message,
            deadLetterReason: $"Destination returned {status}",
            deadLetterErrorDescription: responseBody,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// The one log line per message that support traces on. Outcome is one of Succeeded,
    /// Retrying, DeadLettered or ConfigUnavailable; Body carries the inbound payload so any id
    /// inside it is searchable without knowing the field name.
    /// </summary>
    private void Log(
        LogLevel level, Exception? ex, ServiceBusReceivedMessage message,
        string outcome, long started, string body, string detail) =>
        logger.Log(
            level, ex,
            "Message {MessageId} {Outcome} after {ElapsedMs}ms on delivery {DeliveryCount}. "
            + "{Detail} EventName={EventName} Body={Body}",
            message.MessageId, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            message.DeliveryCount, detail, QueueName, body);

    /// <summary>
    /// True when replaying the same request could plausibly succeed. 408 and 429 are 4xx but
    /// explicitly mean "try again"; every other 4xx is the destination rejecting this payload.
    /// </summary>
    public static bool IsRetryable(HttpStatusCode status) =>
        (int)status >= 500
        || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>Applies a compiled JSONata mapping to a message body.</summary>
    public static string Map(JsonataQuery query, string message) => query.Eval(message);

    /// <summary>
    /// JSONata only evaluates JSON, so an XML body is converted first. The root element is kept,
    /// so <c>&lt;order&gt;&lt;id&gt;1&lt;/id&gt;&lt;/order&gt;</c> maps as <c>order.id</c>, and every
    /// value arrives as a string because XML carries no types - a mapping doing arithmetic on one
    /// needs <c>$number()</c>. A body that doesn't start with '&lt;' is passed through untouched.
    /// </summary>
    // ponytail: sniffs the first character rather than trusting message.ContentType, which
    // producers routinely leave unset. Read ContentType instead if yours sets it reliably.
    public static string ToJson(string body)
    {
        // A BOM or leading whitespace would hide the '<' from the check below.
        var trimmed = body.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith('<')) return body;

        // Messages come from outside, so no DTDs and no external entity resolution: that closes
        // off both XXE file reads and billion-laughs entity expansion.
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(trimmed), settings);

        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);

        return JsonConvert.SerializeXmlNode(document, Newtonsoft.Json.Formatting.None)
            ?? throw new InvalidOperationException("XML body converted to null JSON.");
    }

    /// <summary>Rejects anything that isn't an absolute http(s) URL, naming the offending row.</summary>
    // ponytail: scheme check only. Add a host allowlist if anything less trusted can write this table,
    // since a valid-looking URL can still point at a link-local or internal address.
    public static Uri ParseDestination(string? url, string eventName) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new InvalidOperationException(
                $"InboundMappingConfig.DestinationUrl for EventName '{eventName}' is not an absolute http(s) URL: '{url}'.");

    /// <summary>
    /// The container large bodies are archived to, built from the Function App's own storage
    /// account so there is no second connection string to configure. Null when that setting is
    /// absent, which disables archiving rather than failing the message.
    /// </summary>
    // ponytail: reads the connection-string form of AzureWebJobsStorage. A deployment using an
    // identity-based connection sets AzureWebJobsStorage__accountName instead, and archiving
    // silently turns off; pass a DefaultAzureCredential here if you move to one.
    private static readonly Lazy<BlobContainerClient?> Archive = new(() =>
        Environment.GetEnvironmentVariable("AzureWebJobsStorage") is { Length: > 0 } connection
            ? new BlobContainerClient(connection, ArchiveContainer)
            : null);

    /// <summary>
    /// Returns the body as it should be logged. A body short enough to log whole is returned
    /// untouched; one that would be truncated is written to blob storage first and the link
    /// appended, so support can read the full payload without Service Bus or SQL access.
    /// Archiving never fails the message - on any storage error the truncated body is logged alone.
    /// </summary>
    public static async Task<string> BodyForLogAsync(
        BlobContainerClient? archive, string raw, string messageId, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (raw.Length <= MaxBodyChars) return raw;

        var truncated = Truncate(raw, MaxBodyChars);
        if (archive is null) return truncated;

        try
        {
            // Date-partitioned so a lifecycle rule can expire old payloads by prefix, and keyed by
            // MessageId so a redelivery overwrites its own blob instead of adding another copy.
            var name = $"{DateTime.UtcNow:yyyy/MM/dd}/"
                + (string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString() : messageId);

            // Archiving is rare, so paying for this check here beats a round trip at startup.
            await archive.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            var blob = archive.GetBlobClient(name);
            await blob.UploadAsync(BinaryData.FromString(raw), overwrite: true, cancellationToken);

            return $"{truncated} [full body: {blob.Uri}]";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diagnostics must never cost a message. Log why and fall back to the truncated body.
            logger.LogWarning(ex, "Could not archive the body of {MessageId}: {Reason}", messageId, ex.Message);
            return truncated;
        }
    }

    public static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "...[truncated]");

    /// <summary>Property names this class already logs; an extracted field never overwrites one.</summary>
    private static readonly HashSet<string> ReservedNames =
        new(StringComparer.OrdinalIgnoreCase)
        { "MessageId", "Outcome", "ElapsedMs", "DeliveryCount", "Detail", "EventName", "Body" };

    /// <summary>
    /// Pulls identifier-ish leaf fields out of a JSON body so each becomes its own custom
    /// dimension in Application Insights, searchable without knowing the payload shape and
    /// without depending on the body surviving truncation.
    /// <para>
    /// A field matches when its name is exactly <c>id</c>/<c>number</c>, ends with <c>Id</c>,
    /// <c>ID</c>, <c>Number</c> or <c>NUMBER</c>, or ends with <c>_id</c>/<c>-id</c>/
    /// <c>_number</c>/<c>-number</c>. Requiring that boundary is what keeps <c>isPaid</c> and
    /// <c>isValid</c> out; an all-lowercase <c>orderid</c> is not matched either.
    /// </para>
    /// </summary>
    public static List<KeyValuePair<string, object?>> ExtractTraceFields(
        string json, int maxFields = MaxTraceFields, int maxValueChars = MaxTraceValueChars)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Walk(JsonNode.Parse(json), found, maxFields);
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON, or not parseable. The body is logged regardless; nothing to extract.
        }

        return found
            .Select(pair => new KeyValuePair<string, object?>(
                pair.Key, Truncate(string.Join(",", pair.Value), maxValueChars)))
            .ToList();
    }

    private static void Walk(JsonNode? node, Dictionary<string, List<string>> found, int maxFields)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    if (child is JsonObject or JsonArray)
                    {
                        Walk(child, found, maxFields);
                    }
                    else if (child is JsonValue && IsTraceField(name) && !ReservedNames.Contains(name))
                    {
                        // Arrays of records repeat a name, so collect distinct values under one key
                        // rather than emitting lineId, lineId, lineId as separate dimensions.
                        var values = found.TryGetValue(name, out var existing) ? existing : null;
                        if (values is null)
                        {
                            if (found.Count >= maxFields) continue;
                            found[name] = values = [];
                        }

                        var value = child.ToString();
                        if (!values.Contains(value)) values.Add(value);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array) Walk(item, found, maxFields);
                break;
        }
    }

    private static bool IsTraceField(string name)
    {
        // XML attributes arrive as "@orderId"; the prefix must not hide the suffix below.
        var bare = name.TrimStart('@');

        return Matches(bare, "id") || Matches(bare, "number");

        static bool Matches(string name, string suffix) =>
            name.Equals(suffix, StringComparison.OrdinalIgnoreCase)
            // "Id"/"ID", never the "id" of isPaid or isValid.
            || name.EndsWith(char.ToUpperInvariant(suffix[0]) + suffix[1..], StringComparison.Ordinal)
            || name.EndsWith(suffix.ToUpperInvariant(), StringComparison.Ordinal)
            || name.EndsWith("_" + suffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase);
    }

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
