using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var blobServiceUri = app.Configuration["BlobStorage:ServiceUri"];
var sasToken = app.Configuration["BlobStorage:SasToken"];
var containerName = app.Configuration["BlobStorage:ContainerName"] ?? "graph-notifications";

var blobServiceClient = new BlobServiceClient(new Uri($"{blobServiceUri}?{sasToken}"));
var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
await containerClient.CreateIfNotExistsAsync();

// Listen to posts to /api/notifications endpoint
app.MapPost("/api/notifications", async (HttpContext context) =>
{
    // verify content of ?validationToken=<token>.
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

                await AppendToBlobAsync(containerClient, logLine);
            }
        }
        else
        {
            var logLine = $"[{timestamp}] Raw notification: {body}";
            await AppendToBlobAsync(containerClient, logLine);
        }
    }
    catch (JsonException ex)
    {
        var logLine = $"[{timestamp}] Failed to parse notification: {ex.Message} | Body: {body}";
        Console.WriteLine(logLine);
        await AppendToBlobAsync(containerClient, logLine);
    }

    // Graph expects 2xx within 3 seconds; otherwise it retries
    context.Response.StatusCode = 202;
});

// Selfcheck if website responds
app.MapGet("/", () => "Graph Notification API is running.");

// Append a line to today's append blob (one blob per day)
async Task AppendToBlobAsync(BlobContainerClient container, string line)
{
    var blobName = $"notifications-{DateTime.UtcNow:yyyy-MM-dd_hh-mm}.log";
    var appendBlobClient = container.GetAppendBlobClient(blobName);
    await appendBlobClient.CreateIfNotExistsAsync();

    var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
    using var stream = new MemoryStream(bytes);
    await appendBlobClient.AppendBlockAsync(stream);
}

app.Run();
