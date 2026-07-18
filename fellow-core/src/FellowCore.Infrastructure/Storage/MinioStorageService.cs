using Amazon.S3;
using Amazon.S3.Model;
using FellowCore.Application.Common.Interfaces;
using Microsoft.Extensions.Options;

namespace FellowCore.Infrastructure.Storage;

public class MinioStorageService(IAmazonS3 s3Client, IOptions<StorageOptions> options) : IStorageService
{
    private readonly StorageOptions _options = options.Value;

    public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType)
    {
        string key = $"{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid()}/{fileName}";

        var putRequest = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = key,
            InputStream = fileStream,
            ContentType = contentType
        };

        await s3Client.PutObjectAsync(putRequest);

        return $"{_options.PublicUrl}/{_options.BucketName}/{key}";
    }
}
