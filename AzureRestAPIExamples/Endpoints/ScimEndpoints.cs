using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Storage.Blobs;
using AzureRestAPIExamples.Models;
using AzureRestAPIExamples.Services;

namespace AzureRestAPIExamples.Endpoints;

public static class ScimEndpoints
{
    public static void MapScimEndpoints(this WebApplication app, BlobContainerClient containerClient, ConcurrentDictionary<string, ScimUser> scimUsers)
    {
        // POST /scim/Users – Create a new user
        app.MapPost("/scim/Users", async (ScimUser newUser, HttpContext context) =>
        {
            var logLine = "";

            if (string.IsNullOrWhiteSpace(newUser.UserName))
            {
                logLine = "userName is required.";
                Console.WriteLine(logLine);
                await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

                return Results.BadRequest(new { detail = logLine, status = 400 });
            }

            if (scimUsers.Values.Any(u => string.Equals(u.UserName, newUser.UserName, StringComparison.OrdinalIgnoreCase)))
            {
                logLine = $"User with userName '{newUser.UserName}' already exists.";
                Console.WriteLine(logLine);
                await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

                return Results.Conflict(new { detail = logLine, status = 409 });
            }

            var id = Guid.NewGuid().ToString();
            var user = new ScimUser(id, newUser.UserName, true);
            scimUsers[id] = user;

            logLine = $"[{DateTime.UtcNow:O}] SCIM user created: {user.UserName} ({user.Id})";
            Console.WriteLine(logLine);
            await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

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
            await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");
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
            await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

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
                await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

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
            await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

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
                await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");
                return Results.NotFound(new { detail = logLine, status = 404 });
            }

            logLine = $"[{DateTime.UtcNow:O}] SCIM user deleted: {removed.UserName} ({removed.Id})";
            Console.WriteLine(logLine);
            await BlobLogger.AppendToBlobAsync(containerClient, logLine, "scim");

            return Results.NoContent();
        });
    }
}
