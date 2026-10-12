using System.Net;
using System.Text;
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HuebRun.Tests.Data;

public class SlipOkVerifierTests
{
    private sealed class StubHandler(HttpStatusCode status, string body, string contentType = "application/json") : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public Exception? Throw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        }
    }

    private static readonly SlipImage Image = new([0x89, 0x50, 0x4E, 0x47, 1, 2, 3], ".png", "image/png");

    private static async Task<(SlipCheckResult Result, StubHandler Handler)> Verify(StubHandler handler, decimal amount = 400.01m)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.slipok.test/") };
        var options = Options.Create(new SlipOkOptions { BaseUrl = "https://api.slipok.test", BranchId = "78336", ApiKey = "KEY" });
        var result = await new SlipOkVerifier(http, options, NullLogger<SlipOkVerifier>.Instance).VerifyAsync(Image, amount, default);
        return (result, handler);
    }

    private const string OkBody = """
        { "success": true, "data": { "success": true, "message": "✅", "transRef": "0100921015076",
          "transTimestamp": "2026-11-15T00:10:07.000Z", "amount": 400.01 } }
        """;

    [Fact]
    public async Task Sends_file_log_amount_and_api_key()
    {
        var (_, h) = await Verify(new StubHandler(HttpStatusCode.OK, OkBody));
        Assert.Equal("https://api.slipok.test/api/line/apikey/78336", h.Request!.RequestUri!.ToString());
        Assert.Equal("KEY", h.Request.Headers.GetValues("x-authorization").Single());
        Assert.Contains("name=files", h.RequestBody);
        Assert.Contains("name=log", h.RequestBody);
        Assert.Contains("true", h.RequestBody);
        Assert.Contains("400.01", h.RequestBody);
    }

    [Fact]
    public async Task Success_is_verified_with_slip_data()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.OK, OkBody));
        var v = Assert.IsType<SlipCheckResult.Verified>(r);
        Assert.Equal(("0100921015076", 400.01m), (v.TransRef, v.Amount));
        Assert.Equal(new DateTime(2026, 11, 15, 0, 10, 7, DateTimeKind.Utc), v.TransferredAt);
        Assert.Equal(DateTimeKind.Utc, v.TransferredAt!.Value.Kind);
        Assert.Contains("0100921015076", v.RawJson);
    }

    [Theory]
    [InlineData(1013, PaymentRejectReason.AmountMismatch)]
    [InlineData(1014, PaymentRejectReason.WrongReceiver)]
    [InlineData(1000, PaymentRejectReason.InvalidSlip)]
    [InlineData(1005, PaymentRejectReason.InvalidSlip)]
    [InlineData(1006, PaymentRejectReason.InvalidSlip)]
    [InlineData(1007, PaymentRejectReason.InvalidSlip)]
    [InlineData(1008, PaymentRejectReason.InvalidSlip)]
    [InlineData(1011, PaymentRejectReason.InvalidSlip)]
    public async Task Slip_errors_are_rejected(int code, string reason)
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest, $$"""{ "code": {{code}}, "message": "x" }"""));
        Assert.Equal(reason, Assert.IsType<SlipCheckResult.Rejected>(r).Reason);
    }

    [Fact]
    public async Task Duplicate_carries_trans_ref_from_data()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest,
            """{ "code": 1012, "message": "สลิปซ้ำ", "data": { "success": true, "transRef": "T-1" } }"""));
        Assert.Equal("T-1", Assert.IsType<SlipCheckResult.Duplicate>(r).TransRef);
    }

    [Fact]
    public async Task Bank_delay_retries_after_given_minutes()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest,
            """{ "code": 1010, "message": "รอ", "data": { "bankCode": "002", "delay": 8 } }"""));
        Assert.Equal(TimeSpan.FromMinutes(8), Assert.IsType<SlipCheckResult.Retry>(r).After);
    }

    [Fact]
    public async Task Bank_outage_retries_after_15_minutes()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadRequest, """{ "code": 1009, "message": "x" }"""));
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.IsType<SlipCheckResult.Retry>(r).After);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{ "code": 1002, "message": "Authorization Header ไม่ถูกต้อง" }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": 1003, "message": "หมดอายุ" }""")]
    [InlineData(HttpStatusCode.BadRequest, """{ "code": 1004, "message": "เกินโควตา" }""")]
    [InlineData(HttpStatusCode.InternalServerError, """{ "message": "boom" }""")]
    public async Task Configuration_or_server_errors_are_unavailable(HttpStatusCode status, string body)
    {
        var (r, _) = await Verify(new StubHandler(status, body));
        Assert.IsType<SlipCheckResult.Unavailable>(r);
    }

    [Fact]
    public async Task Non_json_gateway_error_is_unavailable()
    {
        var (r, _) = await Verify(new StubHandler(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>", "text/html"));
        var u = Assert.IsType<SlipCheckResult.Unavailable>(r);
        Assert.Null(u.RawJson);   // ไม่ใช่ JSON เก็บลง jsonb ไม่ได้
    }

    [Fact]
    public async Task Network_failure_and_timeout_are_unavailable()
    {
        Assert.IsType<SlipCheckResult.Unavailable>((await Verify(new StubHandler(HttpStatusCode.OK, "") { Throw = new HttpRequestException("down") })).Result);
        Assert.IsType<SlipCheckResult.Unavailable>((await Verify(new StubHandler(HttpStatusCode.OK, "") { Throw = new TaskCanceledException("timeout") })).Result);
    }

    [Fact]
    public void Sha256_is_lowercase_hex() =>
        Assert.Matches("^[0-9a-f]{64}$", Image.Sha256);
}
