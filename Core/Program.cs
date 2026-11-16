using Azure.Monitor.OpenTelemetry.Exporter;
using Core.Daos;
using Core.Requestors;
using Core.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<MySqlDao>();
builder.Services.AddSingleton<HttpRequestor>();
builder.Services.AddSingleton<MappingService>();

// Needs APPLICATIONINSIGHTS_CONNECTION_STRING; without it the exporter is a no-op and logs
// still reach Application Insights through the Functions host relay.
// Without this the id/number fields the function attaches per message never leave the worker.
// CustomDimensionTests covers both halves of that.
builder.Logging.AddOpenTelemetry(logging => logging.IncludeScopes = true);

builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .WithTracing(tracing => tracing.AddHttpClientInstrumentation()) // the POST, as a dependency
    .UseAzureMonitorExporter();

builder.Build().Run();
