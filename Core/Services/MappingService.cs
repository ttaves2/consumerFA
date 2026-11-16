using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml;
using Azure.Identity;
using Azure.Storage.Blobs;
using Core.Daos;
using Core.Requestors;
using Jsonata.Net.Native;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Core.Services;

/// <summary>How to settle a message. Failures worth retrying throw instead of returning one.</summary>
public sealed record ProcessResult(string? DeadLetterReason = null, string? DeadLetterDescription = null)
{
    public static readonly ProcessResult Delivered = new();

    public bool IsDelivered => DeadLetterReason is null;
}

/// <summary>
/// The pipeline, independent of what triggered it: convert the body to JSON, load the event's
/// mapping, apply it, and POST the result. Any trigger can call it with its own event name.
/// </summary>
public class MappingService(
    MySqlDao dao,
    HttpRequestor requestor,
    IMemoryCache cache,
    ILogger<MappingService> logger)
{
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
    // ponytail: one lock across every event name. Key it per event if many queues cold-start at once.
    private static readonly SemaphoreSlim ConfigLock = new(1, 1);

    /// <summary>
    /// Runs one message through the pipeline. Returns how to settle it, or throws when it
    /// should come back later (config unavailable, or a destination status worth retrying).
    /// <para>
    /// Every call emits exactly one outcome line carrying Outcome and Body, which is what
    /// support searches on. See the "Tracing a message" section of the README.
    /// </para>
    /// </summary>
    public async Task<ProcessResult> ProcessAsync(
        string eventName, string messageId, int deliveryCount, string raw,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var body = await BodyForLogAsync(Archive.Value, raw, messageId, logger, cancellationToken);

        // Outcome is one of Succeeded, Retrying, DeadLettered or ConfigUnavailable.
        void Log(LogLevel level, Exception? ex, string outcome, string detail) =>
            logger.Log(
                level, ex,
                "Message {MessageId} {Outcome} after {ElapsedMs}ms on delivery {DeliveryCount}. "
                + "{Detail} EventName={EventName} Body={Body}",
                messageId, outcome, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                deliveryCount, detail, eventName, body);

        // Convert before anything else, so the id fields below are available to every outcome
        // and not just the ones that get as far as mapping.
        string json;
        try
        {
            json = ToJson(raw);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log(LogLevel.Error, ex, "DeadLettered", "Body is neither valid JSON nor valid XML: " + ex.Message);
            return new("UnreadableBody", Truncate(ex.Message, MaxReasonChars));
        }

        // Every log line from here on carries the body's id/number fields as custom dimensions.
        // Program.cs must set IncludeScopes, or they are dropped silently.
        using var traceFields = logger.BeginScope(ExtractTraceFields(json));

        (JsonataQuery Query, Uri DestinationUrl) config;
        try
        {
            config = await GetConfigAsync(eventName, cancellationToken);
        }
        catch (Exception ex)
        {
            // Transient (SQL down, row not deployed yet). Throwing leaves the message unsettled so
            // it comes back when the lock lapses, rather than dead-lettering a working payload.
            Log(LogLevel.Error, ex, "ConfigUnavailable", ex.Message);
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
            Log(LogLevel.Error, ex, "DeadLettered", "Mapping failed: " + ex.Message);
            return new("MappingFailed", Truncate(ex.Message, MaxReasonChars));
        }

        var response = await requestor.PostJsonAsync(config.DestinationUrl, mapped, messageId, cancellationToken);
        var status = (int)response.Status;

        if (response.IsSuccess)
        {
            Log(LogLevel.Information, null, "Succeeded", $"POST {config.DestinationUrl} returned {status}");
            return ProcessResult.Delivered;
        }

        var responseBody = Truncate(response.Body, MaxReasonChars);

        if (HttpRequestor.IsRetryable(response.Status))
        {
            Log(LogLevel.Warning, null, "Retrying",
                $"POST {config.DestinationUrl} returned {status}: {responseBody}");
            throw new HttpRequestException($"Destination returned {status}, will retry.");
        }

        Log(LogLevel.Error, null, "DeadLettered",
            $"POST {config.DestinationUrl} returned {status}: {responseBody}");
        return new($"Destination returned {status}", responseBody);
    }

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
    /// account so there is no second connection string to configure. Null when none of the
    /// settings are present, which disables archiving rather than failing the message.
    /// </summary>
    private static readonly Lazy<BlobContainerClient?> Archive = new(() => ArchiveClient(
        Environment.GetEnvironmentVariable("AzureWebJobsStorage"),
        Environment.GetEnvironmentVariable("AzureWebJobsStorage__blobServiceUri"),
        Environment.GetEnvironmentVariable("AzureWebJobsStorage__accountName")));

    /// <summary>
    /// Resolves the archive container from however AzureWebJobsStorage is configured: a
    /// connection string (local Azurite, and older deployments), or the identity-based form,
    /// which sets <c>__blobServiceUri</c> or <c>__accountName</c> instead and is authenticated
    /// with the app's managed identity. Returns null when none of them is set.
    /// </summary>
    public static BlobContainerClient? ArchiveClient(
        string? connection, string? blobServiceUri, string? accountName)
    {
        if (connection is { Length: > 0 }) return new BlobContainerClient(connection, ArchiveContainer);

        // blobServiceUri is preferred over accountName: deriving the host from the account name
        // hardcodes the public cloud's suffix, which is wrong in Gov and China.
        var serviceUri = blobServiceUri is { Length: > 0 } uri ? uri
            : accountName is { Length: > 0 } account ? $"https://{account}.blob.core.windows.net"
            : null;

        // The identity needs Storage Blob Data Contributor, not just Reader: the archive path
        // creates the container on first use as well as writing to it.
        return serviceUri is null
            ? null
            : new BlobContainerClient(
                new Uri($"{serviceUri.TrimEnd('/')}/{ArchiveContainer}"), new DefaultAzureCredential());
    }

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

    private async Task<(JsonataQuery Query, Uri DestinationUrl)> GetConfigAsync(
        string eventName, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(eventName, out (JsonataQuery, Uri) config)) return config;

        await ConfigLock.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(eventName, out config)) return config;

            logger.LogInformation("Loading mapping config for {EventName}", eventName);
            var row = await dao.GetInboundMappingConfigAsync(eventName, cancellationToken);

            // Compile and validate once per load, not per message.
            config = (new JsonataQuery(row.MappingConfig), ParseDestination(row.DestinationUrl, eventName));
            cache.Set(eventName, config, ConfigTtl);
            return config;
        }
        finally
        {
            ConfigLock.Release();
        }
    }
}
