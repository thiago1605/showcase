namespace FellowCore.Infrastructure.Storage;

public class StorageOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = "documents";
    public string PublicUrl { get; set; } = string.Empty;
}
