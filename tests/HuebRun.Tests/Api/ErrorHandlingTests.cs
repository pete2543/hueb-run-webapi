using System.Text.Json;
using HuebRun.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace HuebRun.Tests.Api;

public class ErrorHandlingTests
{
    private static async Task<(int Status, JsonElement Body, string Raw)> Handle(Exception ex)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var handled = await new AppExceptionHandler(NullLogger<AppExceptionHandler>.Instance)
            .TryHandleAsync(ctx, ex, CancellationToken.None);
        Assert.True(handled);
        ctx.Response.Body.Position = 0;
        var raw = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        return (ctx.Response.StatusCode, JsonDocument.Parse(raw).RootElement, raw);
    }

    [Fact]
    public async Task App_exception_maps_to_its_status_and_code()
    {
        var (status, body, _) = await Handle(new AppException(404, "EVENT_NOT_FOUND", "ไม่พบงานนี้", new { slug = "x" }));
        Assert.Equal(404, status);
        Assert.Equal("EVENT_NOT_FOUND", body.GetProperty("code").GetString());
        Assert.Equal("ไม่พบงานนี้", body.GetProperty("message").GetString());
        Assert.Equal("x", body.GetProperty("details").GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Unexpected_exception_returns_500_without_leaking_details()
    {
        var (status, body, raw) = await Handle(new InvalidOperationException("db password is hunter2"));
        Assert.Equal(500, status);
        Assert.Equal("INTERNAL_ERROR", body.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("details").ValueKind);
        Assert.DoesNotContain("hunter2", raw);
    }
}
