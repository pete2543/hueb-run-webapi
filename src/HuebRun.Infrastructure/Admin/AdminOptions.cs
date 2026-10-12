using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Admin;

/// config `Admin` — JwtSecret/Bootstrap* มาจาก user-secrets หรือ env var เท่านั้น
public sealed class AdminOptions
{
    [Required, MinLength(32)] public string JwtSecret { get; set; } = "";
    [Required] public string Issuer { get; set; } = "hueb-run-api";
    [Range(1, 72)] public int TokenLifetimeHours { get; set; } = 8;
    [Range(1, 100)] public int MaxFailedAttempts { get; set; } = 5;
    [Range(1, 1440)] public int LockoutMinutes { get; set; } = 15;
    [Range(1, 10_000)] public int LoginRateLimitPerMinute { get; set; } = 10;

    /// ถ้ายังไม่มี SuperAdmin เลย ตอนแอปเริ่มจะสร้างจากค่าสองตัวนี้ (ลบทิ้งได้หลัง login ครั้งแรก)
    public string? BootstrapEmail { get; set; }
    public string? BootstrapPassword { get; set; }
}
