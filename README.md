# consumerFA

Azure Function App (.NET 10, isolated worker). One function:

`myFirstQueue` (Service Bus queue) → convert the body to JSON if it is XML → run it
through the row's JSONata `MappingConfig` from `InboundMappingConfig` where
`EventName = 'myFirstQueue'` → POST the result to the row's `DestinationUrl`.

If several rows share an `EventName`, the highest `Id` wins. Each POST carries an
`Idempotency-Key` header holding the Service Bus `MessageId`, so a destination can
dedupe the redeliveries that follow any failure.

Config (mapping + URL) is cached in memory for 5 minutes, so edits to the table go
live within 5 minutes without a restart.

Failures are sorted by whether a replay could succeed. 5xx, 408 and 429 throw, so
Service Bus redelivers. Every other non-2xx, and any mapping failure, dead-letters
immediately with the reason attached — replaying those would fail identically.
Config load failures (SQL down, row missing) leave the message unsettled, so it is
redelivered once the lock lapses. `host.json` therefore sets
`autoCompleteMessages: false`; the function settles every message itself.

## Install

| What | Why | How |
| --- | --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | builds the app | `winget install Microsoft.DotNet.SDK.10` |
| [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local) | runs the Functions host locally (`func start`) | `winget install Microsoft.Azure.FunctionsCoreTools` |
| [VS Code](https://code.visualstudio.com/) | editor | `winget install Microsoft.VisualStudioCode` |
| [Node.js](https://nodejs.org) | only needed for the standalone Azurite CLI | `winget install OpenJS.NodeJS.LTS` |
| Docker Desktop | optional — for the local Service Bus emulator | `winget install Docker.DockerDesktop` |

Verify:

```bash
dotnet --list-sdks && func --version && code --version
```

### VS Code extensions

```bash
code --install-extension ms-dotnettools.csdevkit --install-extension ms-azuretools.vscode-azurefunctions --install-extension Azurite.azurite --install-extension ms-mssql.mssql
```

- **C# Dev Kit** — IntelliSense, debugging, test running.
- **Azure Functions** — F5 debugging, deploy, stream logs.
- **Azurite** — local Azure Storage emulator; the Functions host needs it for `AzureWebJobsStorage`.
- **SQL Server (mssql)** — run the DDL below and inspect the config table.

## Set up

```bash
git clone <this repo> && cd consumerFA
dotnet restore
```

Create `local.settings.json` (git-ignored — copy this and fill in your own values):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "ServiceBusConnection": "Endpoint=sb://<namespace>.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<key>",
    "SqlConnectionString": "Server=localhost;Database=consumerfa;Trusted_Connection=True;TrustServerCertificate=True",
    "APPLICATIONINSIGHTS_CONNECTION_STRING": "InstrumentationKey=...;IngestionEndpoint=https://....applicationinsights.azure.com/"
  }
}
```

### Database

```sql
CREATE TABLE InboundMappingConfig (
    Id                   int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EventName            nvarchar(260)  NOT NULL UNIQUE,
    MappingConfig        nvarchar(max)  NOT NULL,
    LastUpdatedBy        nvarchar(25)   NULL,
    LastUpdatedTimestamp datetime2(3)   NULL,
    DestinationUrl       nvarchar(2048) NOT NULL
);

INSERT INTO InboundMappingConfig (EventName, MappingConfig, LastUpdatedBy, LastUpdatedTimestamp, DestinationUrl)
VALUES (
    N'myFirstQueue',
    N'{"id": orderId, "total": $sum(lines.amount)}',
    N'you',
    SYSUTCDATETIME(),
    N'https://webhook.site/<your-id>'
);
```

Use [webhook.site](https://webhook.site) as the destination while developing — it gives you
a throwaway URL and shows the mapped payload it receives.

### Service Bus

The trigger needs a real queue named `myFirstQueue`. Either:

- **Azure** — create a Service Bus namespace (Basic tier is enough) and a `myFirstQueue` queue, then paste its connection string into `local.settings.json`. Send test messages from the portal's Service Bus Explorer.
- **Local emulator** — `docker run` [mcr.microsoft.com/azure-messaging/servicebus-emulator](https://learn.microsoft.com/azure/service-bus-messaging/overview-emulator), declare `myFirstQueue` in its config file, and point `ServiceBusConnection` at `...;UseDevelopmentEmulator=true`.

Azurite does **not** emulate Service Bus — it only covers `AzureWebJobsStorage`.

## Run

1. Start Azurite: VS Code command palette → **Azurite: Start** (or `npx azurite`).
2. Press **F5**, or:

```bash
cd Core && func start
```

Drop a message on `myFirstQueue` and watch the log line naming the destination URL and
status code.

## Tests

```bash
dotnet test
```

`CoreUnitTest` covers the JSONata mapping step — rename/aggregate, missing fields, and the
two failure modes (malformed message, malformed mapping config) — plus `DestinationUrl`
validation, the retry/dead-letter split, XML conversion including the XXE and
entity-expansion guards, and which body fields reach Application Insights as dimensions. The SQL lookup and the outbound POST are not covered; they need a
database and a listener.

## XML messages

JSONata only evaluates JSON, so a body whose first meaningful character is `<` is converted
first (`Newtonsoft.Json`'s `SerializeXmlNode`). Anything else is passed through untouched.
Detection sniffs the body rather than reading `message.ContentType`, because producers
routinely leave that unset.

Three things to know when writing a mapping against XML:

| XML | Becomes | Mapping |
| --- | --- | --- |
| `<order><id>1</id></order>` | `{"order":{"id":"1"}}` | the root is kept, so `order.id` |
| `<order id="1"/>` | `{"order":{"@id":"1"}}` | attributes get an `@` prefix: `order.@id` |
| `<line>a</line><line>b</line>` | `{"line":["a","b"]}` | repeated elements become an array |
| `<line>a</line>` (alone) | `{"line":"a"}` | a single one stays scalar |
| `<?xml version="1.0"?>` | `{"?xml":{"@version":"1.0"},...}` | sits beside the root, so harmless |

**Every value arrives as a string**, because XML carries no types. A mapping doing arithmetic
needs `$number()`:

```jsonata
{"id": order.id, "total": $sum(order.lines.line.amount.$number($))}
```

The single-vs-repeated split in the table is the one to watch, though JSONata's sequence
semantics absorb most of it — `order.lines.line.amount` behaves the same for one line or ten.

DTDs are prohibited and external entities are not resolved, so a message cannot use an XML
doctype to read files off the host or to mount an entity-expansion attack. Both cases are
covered in `XmlConversionTests`.

## Tracing a message

Every message produces exactly one log line, whatever happens to it, with these properties:

| Property | Values |
| --- | --- |
| `Outcome` | `Succeeded`, `Retrying`, `DeadLettered`, `ConfigUnavailable` |
| `MessageId` | the Service Bus message id, also sent as the `Idempotency-Key` header |
| `Body` | the inbound payload, truncated at 4000 chars, with a link to the full copy when it was |
| `Detail` | destination URL and status code, or the failure reason |
| `ElapsedMs`, `DeliveryCount`, `EventName` | timing and redelivery context |

On top of those, **identifier fields from the body become custom dimensions of their own**,
so they stay searchable even when the body is truncated. A field is picked up when its name:

| Rule | Matches | Does not match |
| --- | --- | --- |
| is exactly `id` or `number` | `id`, `ID`, `number` | |
| ends with `Id`/`ID`/`Number`/`NUMBER` | `orderId`, `orderID`, `accountNumber` | `isPaid`, `isValid`, `unpaid`, `hybrid` |
| ends with `_id`/`-id`/`_number`/`-number` | `order_id`, `ORDER_ID`, `account-number` | |
| all-lowercase suffix | | `orderid`, `phonenumber` |

Requiring that boundary is what keeps `isPaid` and `isValid` out of the dimensions. The cost
is that an all-lowercase `orderid` is missed - if your payloads use that style, relax
`IsTraceField`.

Fields are found at any nesting depth, including inside arrays, and XML attributes come
through with their `@` prefix (`@OrderId`). Repeated names collapse into one dimension holding
the distinct values (`lineId = "L1,L2"`) rather than one dimension per array element. At most
10 fields are captured, each truncated at 200 chars, and a field is skipped if its name would
shadow one of the fixed properties above.

So support has two ways in. By a specific id, which survives truncation:

```kql
traces
| where customDimensions.orderId == "ORD-10044"
| project timestamp, Outcome = customDimensions.Outcome, Detail = customDimensions.Detail,
          MessageId = customDimensions.MessageId, DeliveryCount = customDimensions.DeliveryCount
| order by timestamp desc
```

Or by anything at all in the body, which does not:

```kql
traces
| where customDimensions.Body contains "ORD-10044"
| project timestamp, Outcome = customDimensions.Outcome, Detail = customDimensions.Detail
| order by timestamp desc
```

These dimensions travel as a log scope, so `Program.cs` must set `IncludeScopes = true`.
Without it they are dropped silently, which is why `CustomDimensionTests` asserts both halves
of that: that dynamic keys reach the record, and that they vanish when the flag is off.

### Large bodies

A body over 4000 chars is written whole to blob storage before it is logged, and the log line
carries the link after the truncated text:

```
Body=<first 4000 chars>...[truncated] [full body: https://<account>.blob.core.windows.net/message-bodies/2026/11/16/<MessageId>]
```

That is the only way to read a payload that failed, since the dead-letter queue is not
reachable without Service Bus access. Bodies at or under the limit are logged inline and never
touch storage.

The container is `message-bodies` on the Function App's own storage account, so there is no
second connection string to configure. Whichever form `AzureWebJobsStorage` takes is used:

| Setting | Auth |
| --- | --- |
| `AzureWebJobsStorage` | the connection string, as local Azurite runs use |
| `AzureWebJobsStorage__blobServiceUri` | the app's managed identity |
| `AzureWebJobsStorage__accountName` | the app's managed identity, host assumed to be `.blob.core.windows.net` |

A connection string wins if set, then `__blobServiceUri`, then `__accountName`. Prefer
`__blobServiceUri` outside the public cloud, since deriving the host from the account name
hardcodes that cloud's suffix. **The identity needs Storage Blob Data Contributor** - the
archive path creates the container on first use, which Reader cannot do. With none of the
three set, archiving is off and bodies are logged truncated as before.

Blobs are laid out as `yyyy/MM/dd/<MessageId>`, so a lifecycle rule can expire them by prefix;
nothing expires them today. A redelivery overwrites its own blob rather than adding a copy.

Archiving never costs a message: if storage is unreachable the line is logged with the
truncated body alone and a warning beside it, which `BodyArchiveTests` covers.

What each outcome means:

- **Succeeded** - the destination returned 2xx and the message was completed.
- **Retrying** - 5xx, 408 or 429. The message is redelivered; expect more lines with a higher `DeliveryCount`, then either `Succeeded` or a dead-letter once deliveries run out.
- **DeadLettered** - either the destination rejected the payload (any other non-2xx) or the mapping failed. It will not be retried; the message is in `myFirstQueue/$deadletterqueue` with the reason attached.
- **ConfigUnavailable** - the config row could not be loaded (SQL down, row missing, bad `DestinationUrl`). The message was left unsettled and comes back when the lock lapses, so this one is usually an infrastructure problem rather than a message problem.

To count yesterday's outcomes:

```kql
traces
| where timestamp > ago(1d) and isnotempty(customDimensions.Outcome)
| summarize count() by tostring(customDimensions.Outcome)
```

## Layout

| Path | What's in it |
| --- | --- |
| `Core/MyFirstQueueFunction.cs` | the whole pipeline: trigger, SQL lookup, cache, JSONata, POST |
| `Core/Program.cs` | host builder + DI |
| `Core/host.json` | Functions host config |
| `Core/Core.csproj` | the function app, and its package versions |
| `CoreUnitTest/MappingTests.cs` | tests for the mapping step |
| `CoreUnitTest/DestinationUrlTests.cs` | tests for DestinationUrl validation |
| `CoreUnitTest/RetryClassificationTests.cs` | tests for which statuses retry vs dead-letter |
| `CoreUnitTest/TruncateTests.cs` | tests for log/dead-letter truncation |
| `CoreUnitTest/XmlConversionTests.cs` | tests for XML detection, conversion shapes and XXE |
| `CoreUnitTest/TraceFieldTests.cs` | tests for which body fields become custom dimensions |
| `CoreUnitTest/CustomDimensionTests.cs` | tests that scope keys survive the OpenTelemetry export |
| `CoreUnitTest/BodyArchiveTests.cs` | tests for archiving large bodies to blob storage |
| `consumerFA.slnx` | both projects |

## Notes

- JSONata is [Jsonata.Net.Native](https://github.com/mikhail-barg/jsonata.net.native). Its `Eval` returns pretty-printed JSON; harmless over HTTP.
- `Newtonsoft.Json` is referenced only for `SerializeXmlNode`. The BCL has no XML-to-JSON converter, and hand-rolling one gets repeated-elements-to-arrays wrong in ways that corrupt mappings silently.
- No retry policy on the outbound POST — Service Bus redelivery covers it. Reach for Polly if the destination flaps.
- Cache is TTL-only, no invalidation. Restart the host if you need a config change immediately. A semaphore keeps a cold start from opening one SQL connection per concurrent message.
- Application Insights goes through OpenTelemetry: `UseFunctionsWorkerDefaults()` plus `UseAzureMonitorExporter()` in `Program.cs`, and `telemetryMode: OpenTelemetry` in `host.json`. It needs `APPLICATIONINSIGHTS_CONNECTION_STRING` set on the function app; without it the exporter is a no-op and logs still reach Application Insights through the host relay.
- `AddHttpClientInstrumentation()` records the outbound POST as a dependency, so a slow or failing destination is visible in the trace. SQL is not instrumented - it is one cached query per 5 minutes. Add `OpenTelemetry.Instrumentation.SqlClient` if that changes.
- Bodies are logged, and id/number fields are promoted to queryable dimensions. Anything sensitive in a message lands in Application Insights for the workspace retention period, and a field like `phoneNumber` or `taxId` becomes easy to query at scale - narrow `IsTraceField` if that matters more than searchability.
- `DestinationUrl` must be an absolute `http`/`https` URL; anything else fails the config load with an error naming the row. The scheme is all that's checked, so keep write access to `InboundMappingConfig` restricted — a valid URL can still point somewhere internal.
