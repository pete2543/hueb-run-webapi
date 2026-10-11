using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HuebRun.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HuebRun.Infrastructure.Payments;

/// ตรวจสลิปกับ SlipOK (API guide v1.13) ส่ง log=true ให้ SlipOK กันสลิปซ้ำและเช็กบัญชีผู้รับที่ผูกใน LIFF
public sealed class SlipOkVerifier(HttpClient http, IOptions<SlipOkOptions> options, ILogger<SlipOkVerifier> logger) : ISlipVerifier
{
    public async Task<SlipCheckResult> VerifyAsync(SlipImage image, decimal expectedAmount, CancellationToken ct)
    {
        var o = options.Value;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image.Content);
        file.Headers.ContentType = new MediaTypeHeaderValue(image.ContentType);
        form.Add(file, "files", "slip" + image.Extension);
        form.Add(new StringContent("true"), "log");
        form.Add(new StringContent(expectedAmount.ToString("0.00", CultureInfo.InvariantCulture)), "amount");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/line/apikey/{o.BranchId}") { Content = form };
        request.Headers.TryAddWithoutValidation("x-authorization", o.ApiKey);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            return Map(response.StatusCode, raw);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "SlipOK request failed");
            return new SlipCheckResult.Unavailable(null);
        }
    }

    private SlipCheckResult Map(HttpStatusCode status, string raw)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            logger.LogError("SlipOK returned non-JSON response (HTTP {Status})", (int)status);
            return new SlipCheckResult.Unavailable(null);
        }

        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : (JsonElement?)null;
        if (status == HttpStatusCode.OK && IsTrue(root, "success") && data is { } ok)
        {
            if (!IsTrue(ok, "success")) return new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, raw);
            if (Str(ok, "transRef") is { } transRef && ok.TryGetProperty("amount", out var a) && a.TryGetDecimal(out var amount))
                return new SlipCheckResult.Verified(transRef, amount, Timestamp(ok), raw);
        }

        var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
        switch (code)
        {
            case 1012:
                return new SlipCheckResult.Duplicate(data is { } dup ? Str(dup, "transRef") : null, raw);
            case 1013:
                return new SlipCheckResult.Rejected(PaymentRejectReason.AmountMismatch, raw);
            case 1014:
                return new SlipCheckResult.Rejected(PaymentRejectReason.WrongReceiver, raw);
            case 1000 or 1005 or 1006 or 1007 or 1008 or 1011:
                return new SlipCheckResult.Rejected(PaymentRejectReason.InvalidSlip, raw);
            case 1010:
                var delay = data is { } wait && wait.TryGetProperty("delay", out var dl) && dl.TryGetInt32(out var m) ? m : 5;
                return new SlipCheckResult.Retry(TimeSpan.FromMinutes(Math.Max(1, delay)), raw);
            case 1009:
                return new SlipCheckResult.Retry(TimeSpan.FromMinutes(15), raw);
            default:
                // 1001–1004, 1015, 5xx: config หรือแพ็กเกจผิด ต้องมีคนดู
                logger.LogError("SlipOK unavailable: HTTP {Status}, code {Code}", (int)status, code);
                return new SlipCheckResult.Unavailable(raw);
        }
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static DateTime? Timestamp(JsonElement e) =>
        Str(e, "transTimestamp") is { } s && DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
