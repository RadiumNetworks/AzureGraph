using System.Collections.Concurrent;
using Azure.Storage.Blobs;
using AzureRestAPIExamples.Endpoints;
using AzureRestAPIExamples.Models;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var blobServiceUri = app.Configuration["BlobStorage:ServiceUri"];
var sasToken = app.Configuration["BlobStorage:SasToken"];
var containerName = app.Configuration["BlobStorage:ContainerName"] ?? "graph-notifications";

var blobServiceClient = new BlobServiceClient(new Uri($"{blobServiceUri}?{sasToken}"));
var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
await containerClient.CreateIfNotExistsAsync();

var scimUsers = new ConcurrentDictionary<string, ScimUser>();

// Health check
app.MapGet("/", () => "Graph Notification API is running.");

// Map endpoint groups
app.MapNotificationEndpoints(containerClient);
app.MapScimEndpoints(containerClient, scimUsers);
app.MapAlertEndpoints(containerClient);

app.Run();
