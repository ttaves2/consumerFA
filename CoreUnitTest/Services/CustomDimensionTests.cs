using Core.Services;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace CoreUnitTest.Services;

/// <summary>
/// Guards the mechanism the id extraction relies on: that scope entries with runtime-determined
/// keys reach the exported LogRecord, which is what Azure Monitor turns into customDimensions.
/// OpenTelemetry's docs state IncludeScopes exists but never promise dynamic keys work, so this
/// fails loudly if a package upgrade changes it.
/// </summary>
public class CustomDimensionTests
{
    private static (List<LogRecord> Records, ILogger Logger, IDisposable Provider) BuildLogger()
    {
        var records = new List<LogRecord>();
        var factory = LoggerFactory.Create(builder =>
            builder.AddOpenTelemetry(options =>
            {
                options.IncludeScopes = true;
                options.AddInMemoryExporter(records);
            }));

        return (records, factory.CreateLogger("test"), factory);
    }

    private static Dictionary<string, object?> ScopeValues(LogRecord record)
    {
        var values = new Dictionary<string, object?>();
        record.ForEachScope(
            (scope, state) =>
            {
                foreach (var pair in scope) state[pair.Key] = pair.Value;
            },
            values);

        return values;
    }

    [Fact]
    public void DynamicScopeKeysReachTheLogRecord()
    {
        var (records, logger, provider) = BuildLogger();
        using (provider)
        {
            var extracted = MappingService.ExtractTraceFields(
                """{"orderId":"ORD-10044","accountNumber":"AC-7"}""");

            using (logger.BeginScope(extracted))
            {
                logger.LogInformation("Message {MessageId} {Outcome}", "sb-1", "Succeeded");
            }
        }

        var record = Assert.Single(records);
        var scope = ScopeValues(record);

        Assert.Equal("ORD-10044", scope["orderId"]);
        Assert.Equal("AC-7", scope["accountNumber"]);

        // The template parameters stay separate from the scope and are what the message renders.
        var attributes = record.Attributes!.ToDictionary(a => a.Key, a => a.Value);
        Assert.Equal("sb-1", attributes["MessageId"]);
        Assert.Equal("Succeeded", attributes["Outcome"]);
    }

    [Fact]
    public void ScopesAreAbsentWhenIncludeScopesIsOff()
    {
        // Documents why Program.cs must set IncludeScopes: without it the ids silently vanish.
        var records = new List<LogRecord>();
        using var factory = LoggerFactory.Create(builder =>
            builder.AddOpenTelemetry(options => options.AddInMemoryExporter(records)));

        var logger = factory.CreateLogger("test");
        using (logger.BeginScope(
            MappingService.ExtractTraceFields("""{"orderId":"ORD-10044"}""")))
        {
            logger.LogInformation("hello");
        }

        Assert.Empty(ScopeValues(Assert.Single(records)));
    }
}
