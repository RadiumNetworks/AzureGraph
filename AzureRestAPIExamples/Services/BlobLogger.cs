using System.Text;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Blobs;

namespace AzureRestAPIExamples.Services;

public static class BlobLogger
{
    public static async Task AppendToBlobAsync(BlobContainerClient container, string line, string source)
    {
        var blobName = $"{source}-notifications-{DateTime.UtcNow:yyyy-MM-dd}.log";
        var appendBlobClient = container.GetAppendBlobClient(blobName);
        await appendBlobClient.CreateIfNotExistsAsync();

        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
        using var stream = new MemoryStream(bytes);
        await appendBlobClient.AppendBlockAsync(stream);
    }
}
