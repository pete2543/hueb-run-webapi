using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Line;

public sealed record LineIdentity(string UserId, string? DisplayName, string? PictureUrl);

public interface ILineIdentityVerifier
{
    /// ตรวจ ID token จาก LIFF/LINE Login กับ LINE; null = ไม่ผ่าน
    Task<LineIdentity?> VerifyIdTokenAsync(string idToken, CancellationToken ct);
}

public sealed class LineIdentityVerifier(HttpClient http, IOptions<LineOptions> options, ILogger<LineIdentityVerifier> logger) : ILineIdentityVerifier
{
    public async Task<LineIdentity?> VerifyIdTokenAsync(string idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Value.LoginChannelId)) return null;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id_token"] = idToken, ["client_id"] = options.Value.LoginChannelId!,
        });
        try
        {
            using var response = await http.PostAsync("oauth2/v2.1/verify", form, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            string? Str(string n) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return Str("sub") is { Length: > 0 } sub ? new LineIdentity(sub, Str("name"), Str("picture")) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "LINE ID token verification failed");
            return null;
        }
    }
}
