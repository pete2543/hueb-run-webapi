using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Line;

public enum LineSendOutcome { Sent, Retry, Permanent }
public sealed record LineSendResult(LineSendOutcome Outcome, string? Error = null);

public interface ILineMessenger
{
    Task<LineSendResult> PushTextAsync(string lineUserId, string text, CancellationToken ct);
}

/// LINE Messaging API: POST /v2/bot/message/push (ข้อความตัวอักษร ≤ 5000)
public sealed class LineMessenger(HttpClient http, IOptions<LineOptions> options, ILogger<LineMessenger> logger) : ILineMessenger
{
    public async Task<LineSendResult> PushTextAsync(string lineUserId, string text, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { to = lineUserId, messages = new[] { new { type = "text", text } } });
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/bot/message/push")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ChannelAccessToken);
        // กันส่งซ้ำเมื่อ retry: LINE ถือว่า key เดียวกันภายใน 24 ชม. คือคำขอเดียวกัน
        request.Headers.TryAddWithoutValidation("X-Line-Retry-Key", Guid.NewGuid().ToString());
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return new LineSendResult(LineSendOutcome.Sent);
            var detail = $"HTTP {(int)response.StatusCode}: {Trim(await response.Content.ReadAsStringAsync(ct))}";
            // 429 / 5xx ลองใหม่ได้; 4xx อื่น (ผู้ใช้บล็อก/ไม่ใช่เพื่อน/ข้อความไม่ถูกต้อง) ลองซ้ำก็ไม่ผ่าน
            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            return new LineSendResult(retry ? LineSendOutcome.Retry : LineSendOutcome.Permanent, detail);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "LINE push failed");
            return new LineSendResult(LineSendOutcome.Retry, ex.Message);
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}
