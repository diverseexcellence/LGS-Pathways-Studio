using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;

namespace LgsImpact.Api.Services;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default);
    Task<string> GetSasUrlAsync(string blobName, int expiryMinutes = 60);
}

public class BlobStorageService(IConfiguration config) : IBlobStorageService
{
    private BlobContainerClient GetContainer()
    {
        var connStr = config["Azure:BlobConnectionString"];
        var container = config["Azure:BlobContainerName"] ?? "lgs-uploads";

        if (string.IsNullOrEmpty(connStr))
        {
            // Local dev: use Azurite or skip blob
            throw new InvalidOperationException("Azure Blob connection string not configured. Set Azure:BlobConnectionString in appsettings.");
        }

        var client = new BlobContainerClient(connStr, container);
        client.CreateIfNotExists(PublicAccessType.None);
        return client;
    }

    public async Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken ct = default)
    {
        var container = GetContainer();
        var blobName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{fileName}";
        var blob = container.GetBlobClient(blobName);
        await blob.UploadAsync(stream, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        return blobName;
    }

    public async Task<string> GetSasUrlAsync(string blobName, int expiryMinutes = 60)
    {
        var container = GetContainer();
        var blob = container.GetBlobClient(blobName);
        var sasUri = blob.GenerateSasUri(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddMinutes(expiryMinutes));
        return await Task.FromResult(sasUri.ToString());
    }
}
