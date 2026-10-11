using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Infrastructure.Payments;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Data;

public class SlipFileTests
{
    private static readonly MinioOptions Options = new()
    {
        MaxFileSizeBytes = 1024, AllowedExtensions = [".jpg", ".jpeg", ".png", ".webp"],
    };

    private static Task<SlipImage> Read(string name, byte[] bytes, long? length = null) =>
        SlipFile.ReadAsync(name, length ?? bytes.Length, new MemoryStream(bytes), Options, default);

    private static async Task AssertInvalid(Task<SlipImage> read)
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => read);
        Assert.Equal((400, "VALIDATION_ERROR"), (ex.StatusCode, ex.Code));
        Assert.True(JsonSerializer.SerializeToElement(ex.Details).TryGetProperty("file", out _));
    }

    [Theory]
    [InlineData("slip.png")]
    [InlineData("SLIP.PNG")]
    public async Task Png_is_accepted_regardless_of_extension_case(string name)
    {
        var image = await Read(name, Slips.Png(1));
        Assert.Equal((".png", "image/png"), (image.Extension, image.ContentType));
    }

    [Fact]
    public async Task Jpeg_and_webp_are_detected_from_magic_bytes()
    {
        Assert.Equal("image/jpeg", (await Read("a.JPG", [0xFF, 0xD8, 0xFF, 0xE0, 1])).ContentType);
        Assert.Equal("image/webp", (await Read("a.webp", [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, 1])).ContentType);
    }

    [Fact]
    public async Task Gif_renamed_to_jpg_is_rejected() =>
        await AssertInvalid(Read("slip.jpg", [.. "GIF89a"u8, 1, 2, 3]));

    [Theory]
    [InlineData("slip.gif")]
    [InlineData("slip.heic")]
    [InlineData("slip")]
    public async Task Unsupported_extension_is_rejected(string name) =>
        await AssertInvalid(Read(name, Slips.Png(1)));

    [Fact]
    public async Task Empty_and_oversized_files_are_rejected()
    {
        await AssertInvalid(Read("slip.png", []));
        await AssertInvalid(Read("slip.png", Slips.Png(1), length: 2048));
    }
}
