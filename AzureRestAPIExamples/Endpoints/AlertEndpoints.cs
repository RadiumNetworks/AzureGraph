using System.Text.Json;
using Azure.Storage.Blobs;
using AzureRestAPIExamples.Services;

namespace AzureRestAPIExamples.Endpoints;

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this WebApplication app, BlobContainerClient containerClient)
    {
        app.MapPost("/api/alerts", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var raw = await reader.ReadToEndAsync();
            var timestamp = DateTime.UtcNow.ToString("O");

            Console.WriteLine($"[{timestamp}] Azure alert received.");

            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(raw);

                var source = payload.TryGetProperty("source", out var q1) ? q1.ToString() : "{}";

                JsonElement alertinfo = payload.TryGetProperty("alert", out var q2) ? q2 : new JsonElement();
                var alertrule = alertinfo.TryGetProperty("rule", out var q3) ? q3.ToString() : "{}";
                var configurationItems = alertinfo.TryGetProperty("configurationItems", out var q4) ? q4.ToString() : "{}";

                JsonElement alertContext = payload.TryGetProperty("context", out var q5) ? q5 : new JsonElement();
                var caller = alertContext.TryGetProperty("caller", out var q6) ? q6.ToString() : "{}";

                await BlobLogger.AppendToBlobAsync(containerClient, $"[{timestamp}] [{source}] [{alertrule} on {configurationItems}] by [{caller}]", "alerts");
            }
            catch (JsonException ex)
            {
                var logLine = $"[{timestamp}] Failed to parse alert: {ex.Message} | Raw: {raw}";
                Console.WriteLine(logLine);
                await BlobLogger.AppendToBlobAsync(containerClient, logLine, "alerts");
            }

            return Results.Accepted();
        });
    }
}
