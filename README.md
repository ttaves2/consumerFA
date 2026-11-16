# consumerFA

Azure Function App (.NET 10, isolated worker). One function:

`myFirstQueue` (Service Bus queue) → look up the `InboundMappingConfig` row where
`EventName = 'myFirstQueue'` → run the message through the row's JSONata
`MappingConfig` → POST the result to the row's `DestinationUrl`.

Config (mapping + URL) is cached in memory for 5 minutes, so edits to the table go
live within 5 minutes without a restart. A non-2xx response throws, which lets
Service Bus retry and eventually dead-letter the message.

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
    "SqlConnectionString": "Server=localhost;Database=consumerfa;Trusted_Connection=True;TrustServerCertificate=True"
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
two failure modes (malformed message, malformed mapping config). The SQL lookup and the
outbound POST are not covered; they need a database and a listener.

## Layout

| Path | What's in it |
| --- | --- |
| `Core/MyFirstQueueFunction.cs` | the whole pipeline: trigger, SQL lookup, cache, JSONata, POST |
| `Core/Program.cs` | host builder + DI |
| `Core/host.json` | Functions host config |
| `Core/Core.csproj` | the function app, and its package versions |
| `CoreUnitTest/MappingTests.cs` | tests for the mapping step |
| `consumerFA.slnx` | both projects |

## Notes

- JSONata is [Jsonata.Net.Native](https://github.com/mikhail-barg/jsonata.net.native). Its `Eval` returns pretty-printed JSON; harmless over HTTP.
- No retry policy on the outbound POST — Service Bus redelivery covers it. Reach for Polly if the destination flaps.
- Cache is TTL-only, no invalidation. Restart the host if you need a config change immediately.
