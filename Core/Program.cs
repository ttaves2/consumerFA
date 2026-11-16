using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Trace;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// Needs APPLICATIONINSIGHTS_CONNECTION_STRING; without it the exporter is a no-op and logs
// still reach Application Insights through the Functions host relay.
builder.Services.AddOpenTelemetry()
    .UseFunctionsWorkerDefaults()
    .WithTracing(tracing => tracing.AddHttpClientInstrumentation()) // the POST, as a dependency
    .UseAzureMonitorExporter();

builder.Build().Run();
