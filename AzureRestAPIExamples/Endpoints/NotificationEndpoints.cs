using System.Text.Json;
using Azure.Storage.Blobs;
using AzureRestAPIExamples.Services;

namespace AzureRestAPIExamples.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this WebApplication app, BlobContainerClient containerClient)
    {
        app.MapPost("/api/notifications", async (HttpContext context) =>
        {
            // ── Validation handshake ──
            if (context.Request.Query.ContainsKey("validationToken"))
            {
                var validationToken = context.Request.Query["validationToken"].ToString();
                Console.WriteLine($"[{DateTime.UtcNow:O}] Validation request received. Responding with token.");
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync(validationToken);
                return;
            }

            // ── Process change notifications ──
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            var timestamp = DateTime.UtcNow.ToString("O");
            Console.WriteLine($"[{timestamp}] Notification received.");

            try
            {
                var json = JsonSerializer.Deserialize<JsonElement>(body);

                if (json.TryGetProperty("value", out var notifications))
                {
                    foreach (var notification in notifications.EnumerateArray())
                    {
                        var changeType = notification.TryGetProperty("changeType", out var ct) ? ct.GetString() : "unknown";
                        var resource = notification.TryGetProperty("resource", out var res) ? res.GetString() : "unknown";
                        var clientState = notification.TryGetProperty("clientState", out var cs) ? cs.GetString() : "";
                        var tenantId = notification.TryGetProperty("tenantId", out var tid) ? tid.GetString() : "";

                        var logLine = $"[{timestamp}] ChangeType={changeType} | Resource={resource} | TenantId={tenantId} | ClientState={clientState}";
                        Console.WriteLine(logLine);

                        await BlobLogger.AppendToBlobAsync(containerClient, logLine, "graph");
                    }
                }
                else
                {
                    var logLine = $"[{timestamp}] Raw notification: {body}";
                    await BlobLogger.AppendToBlobAsync(containerClient, logLine, "graph");
                }
            }
            catch (JsonException ex)
            {
                var logLine = $"[{timestamp}] Failed to parse notification: {ex.Message} | Body: {body}";
                Console.WriteLine(logLine);
                await BlobLogger.AppendToBlobAsync(containerClient, logLine, "graph");
            }

            context.Response.StatusCode = 202;
        });
    }
}
