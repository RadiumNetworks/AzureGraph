using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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

var scimUsers = new ConcurrentDictionary<string, ScimUser>();

// Graph subscription notification endpoint
app.MapPost("/api/notifications", async (HttpContext context) =>
{
    // ── Validation handshake ──
    // When creating a subscription, Graph sends a POST with ?validationToken=<token>.
    // We must respond with the token as plain text within 10 seconds.
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

                await AppendToBlobAsync(containerClient, logLine, "graph");
            }
        }
        else
        {
            var logLine = $"[{timestamp}] Raw notification: {body}";
            await AppendToBlobAsync(containerClient, logLine, "graph");
        }
    }
    catch (JsonException ex)
    {
        var logLine = $"[{timestamp}] Failed to parse notification: {ex.Message} | Body: {body}";
        Console.WriteLine(logLine);
        await AppendToBlobAsync(containerClient, logLine, "graph");
    }

    // Graph expects 2xx within 3 seconds; otherwise it retries
    context.Response.StatusCode = 202;
});

// Health check
app.MapGet("/", () => "Graph Notification API is running.");

// ── SCIM 2.0 User Provisioning (minimal mockup) ──

// POST /scim/Users – Create a new user
app.MapPost("/scim/Users", async (ScimUser newUser, HttpContext context) =>
{
    var logLine = "";

    if (string.IsNullOrWhiteSpace(newUser.UserName))
    {
        logLine = "userName is required.";
        Console.WriteLine(logLine);
        await AppendToBlobAsync(containerClient, logLine, "scim");

        return Results.BadRequest(new { detail = logLine, status = 400 });
    }
        

    // Check for duplicate userName
    if (scimUsers.Values.Any(u => string.Equals(u.UserName, newUser.UserName, StringComparison.OrdinalIgnoreCase)))
    {
        logLine = $"User with userName '{newUser.UserName}' already exists.";
        Console.WriteLine(logLine);
        await AppendToBlobAsync(containerClient, logLine, "scim");

        return Results.Conflict(new { detail = logLine, status = 409 });
    }
        

    var id = Guid.NewGuid().ToString();
    var user = new ScimUser(id, newUser.UserName, true);
    scimUsers[id] = user;

    logLine = $"[{DateTime.UtcNow:O}] SCIM user created: {user.UserName} ({user.Id})";
    Console.WriteLine(logLine);
    await AppendToBlobAsync(containerClient, logLine, "scim");

    return Results.Created($"/scim/Users/{id}", user);
});

// GET /scim/Users – List users (supports filter=userName eq "value")
app.MapGet("/scim/Users", async (HttpContext context) =>
{
    var filter = context.Request.Query["filter"].ToString();
    var users = scimUsers.Values.AsEnumerable();

    if (!string.IsNullOrEmpty(filter) && filter.Contains("userName eq", StringComparison.OrdinalIgnoreCase))
    {
        var value = filter[(filter.IndexOf("\"") + 1)..].TrimEnd('"');
        users = users.Where(u => string.Equals(u.UserName, value, StringComparison.OrdinalIgnoreCase));
    }
    var logLine = $"Get Users";
    Console.WriteLine(logLine);
    await AppendToBlobAsync(containerClient, logLine, "scim");
    var list = users.ToList();

    return Results.Ok(new
    {
        schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:ListResponse" },
        totalResults = list.Count,
        Resources = list
    });
});

// GET /scim/Users/{id} – Get a specific user
app.MapGet("/scim/Users/{id}", async (string id) =>
{
    var logLine = $"Get User {id}";
    Console.WriteLine(logLine);
    await AppendToBlobAsync(containerClient, logLine, "scim");

    return scimUsers.TryGetValue(id, out var user)
        ? Results.Ok(user)
        : Results.NotFound(new { detail = $"User {id} not found.", status = 404 });
});

// PATCH /scim/Users/{id} – Update user (e.g., deactivate)
app.MapPatch("/scim/Users/{id}", async (string id, HttpContext context) =>
{
    var logLine = "";
    if (!scimUsers.TryGetValue(id, out var existing))
    {
        logLine = $"User {id} not found.";
        Console.WriteLine(logLine);
        await AppendToBlobAsync(containerClient, logLine, "scim");

        return Results.NotFound(new { detail = logLine, status = 404 });
    }
        

    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var patch = JsonSerializer.Deserialize<JsonElement>(body);

    var active = existing.Active;
    if (patch.TryGetProperty("Operations", out var ops))
    {
        foreach (var op in ops.EnumerateArray())
        {
            var path = op.TryGetProperty("path", out var p) ? p.GetString() : null;
            if (string.Equals(path, "active", StringComparison.OrdinalIgnoreCase)
                && op.TryGetProperty("value", out var val))
            {
                active = val.ValueKind == JsonValueKind.String
                    ? bool.Parse(val.GetString()!)
                    : val.GetBoolean();
            }
        }
    }

    var updated = new ScimUser(existing.Id, existing.UserName, active);
    scimUsers[id] = updated;

    logLine = $"[{DateTime.UtcNow:O}] SCIM user updated: {updated.UserName} active={updated.Active}";
    Console.WriteLine(logLine);
    await AppendToBlobAsync(containerClient, logLine, "scim");

    return Results.Ok(updated);
});

// DELETE /scim/Users/{id}
app.MapDelete("/scim/Users/{id}", async (string id) =>
{
    var logLine = "";
    if (!scimUsers.TryRemove(id, out var removed))
    {
        logLine = $"User {id} not found.";
        Console.WriteLine(logLine);
        await AppendToBlobAsync(containerClient, logLine, "scim");
        return Results.NotFound(new { detail = logLine, status = 404 });
    }
        

    logLine = $"[{DateTime.UtcNow:O}] SCIM user deleted: {removed.UserName} ({removed.Id})";
    Console.WriteLine(logLine);
    await AppendToBlobAsync(containerClient, logLine, "scim");

    return Results.NoContent();
});

// Append a line to today's append blob (one blob per day)
async Task AppendToBlobAsync(BlobContainerClient container, string line, string source)
{
    var blobName = $"{source}-notifications-{DateTime.UtcNow:yyyy-MM-dd}.log";
    var appendBlobClient = container.GetAppendBlobClient(blobName);
    await appendBlobClient.CreateIfNotExistsAsync();

    var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
    using var stream = new MemoryStream(bytes);
    await appendBlobClient.AppendBlockAsync(stream);
}

app.Run();

// Minimal SCIM user record – only userName is required
public record ScimUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("userName")] string UserName,
    [property: JsonPropertyName("active")] bool Active)
{
    [JsonPropertyName("schemas")]
    public string[] Schemas { get; init; } = ["urn:ietf:params:scim:schemas:core:2.0:User"];
}
