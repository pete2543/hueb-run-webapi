using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `Minio` — AccessKey/SecretKey มาจาก user-secrets หรือ env var เท่านั้น; bucket ต้องเป็น private
public sealed class MinioOptions
{
    [Required] public string Endpoint { get; set; } = "";
    [Required] public string AccessKey { get; set; } = "";
    [Required] public string SecretKey { get; set; } = "";
    [Required] public string Bucket { get; set; } = "";
    public bool UseSsl { get; set; }
    [Range(1, long.MaxValue)] public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public string[] AllowedExtensions { get; set; } = [];
}
