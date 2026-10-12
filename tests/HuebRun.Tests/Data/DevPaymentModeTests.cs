using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Data;

public class DevPaymentModeTests
{
    private static Task<SlipCheckResult> Verify(string? name) =>
        new DevSlipVerifier().VerifyAsync(Slips.Image(1) with { OriginalName = name }, 400.01m, default);

    [Theory]
    [InlineData("slip.png")]
    [InlineData(null)]                       // ตรวจซ้ำจาก storage ไม่มีชื่อไฟล์
    public async Task Normal_name_is_verified_with_expected_amount(string? name)
    {
        var v = Assert.IsType<SlipCheckResult.Verified>(await Verify(name));
        Assert.Equal(400.01m, v.Amount);
    }

    [Theory]
    [InlineData("test-amount.png", PaymentRejectReason.AmountMismatch)]
    [InlineData("RECEIVER.jpg", PaymentRejectReason.WrongReceiver)]
    [InlineData("invalid_1.png", PaymentRejectReason.InvalidSlip)]
    public async Task Keyword_in_name_picks_rejection(string name, string reason) =>
        Assert.Equal(reason, Assert.IsType<SlipCheckResult.Rejected>(await Verify(name)).Reason);

    [Fact]
    public async Task Delay_retries_after_one_minute() =>
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.IsType<SlipCheckResult.Retry>(await Verify("delay.png")).After);

    [Fact]
    public async Task Review_is_an_unknown_duplicate() =>
        Assert.Null(Assert.IsType<SlipCheckResult.Duplicate>(await Verify("review.png")).TransRef);

    [Fact]
    public async Task Slip_file_keeps_original_name()
    {
        var image = await SlipFile.ReadAsync("dir/delay.PNG", 16, new MemoryStream(Slips.Png(1)),
            new MinioOptions { MaxFileSizeBytes = 1024, AllowedExtensions = [".png"] }, default);
        Assert.Equal("delay.PNG", image.OriginalName);
    }

    [Fact]
    public async Task Local_storage_round_trip()
    {
        var root = Path.Combine(Path.GetTempPath(), "slips-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalSlipStorage(root);
        var bytes = Slips.Png(3);

        await storage.PutAsync("slips/1/abc/1.png", bytes, "image/png", default);
        Assert.Equal(bytes, await storage.GetAsync("slips/1/abc/1.png", default));
        await storage.DeleteAsync("slips/1/abc/1.png", default);
        await Assert.ThrowsAnyAsync<IOException>(() => storage.GetAsync("slips/1/abc/1.png", default));
        Directory.Delete(root, recursive: true);
    }
}
