using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `Minio` = object storage แบบ S3 (MinIO, Railway Bucket ฯลฯ) — AccessKey/SecretKey มาจาก user-secrets หรือ env var เท่านั้น; bucket ต้องเป็น private
public sealed class MinioOptions
{
    [Required] public string Endpoint { get; set; } = "";
    [Required] public string AccessKey { get; set; } = "";
    [Required] public string SecretKey { get; set; } = "";
    [Required] public string Bucket { get; set; } = "";
    public bool UseSsl { get; set; }
    /// MinIO = "us-east-1" (ค่าเริ่ม), Railway Bucket = "auto"
    [Required] public string Region { get; set; } = "us-east-1";
    /// true = https://host/bucket/key (MinIO); false = https://bucket.host/key (Railway/Tigris บังคับแบบนี้กับ bucket ใหม่)
    public bool ForcePathStyle { get; set; } = true;
    [Range(1, long.MaxValue)] public long MaxFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    /// ว่าง = ทุกไฟล์ถูกปฏิเสธหมด จึงให้แอปไม่ขึ้นแทนที่จะพังตอนนักวิ่งอัปสลิป
    [MinLength(1)] public string[] AllowedExtensions { get; set; } = [];
}
