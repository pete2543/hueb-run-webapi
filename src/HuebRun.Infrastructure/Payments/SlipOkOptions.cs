using System.ComponentModel.DataAnnotations;

namespace HuebRun.Infrastructure.Payments;

/// config `SlipOk` — BranchId/ApiKey มาจาก user-secrets หรือ env var เท่านั้น
public sealed class SlipOkOptions
{
    [Required] public string BaseUrl { get; set; } = "https://api.slipok.com";
    [Required] public string BranchId { get; set; } = "";
    [Required] public string ApiKey { get; set; } = "";
    [Range(1, 120)] public int TimeoutSeconds { get; set; } = 15;
}
