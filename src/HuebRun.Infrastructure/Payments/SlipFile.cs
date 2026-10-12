namespace HuebRun.Infrastructure.Payments;

/// ตรวจไฟล์สลิปที่อัปโหลด: ขนาด, นามสกุลที่อนุญาต และชนิดจริงจาก magic bytes (SlipOK รับแค่ JPEG/PNG/WebP)
public static class SlipFile
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    public static async Task<SlipImage> ReadAsync(string? fileName, long length, Stream content, MinioOptions options, CancellationToken ct)
    {
        if (length <= 0) throw PaymentErrors.InvalidFile("กรุณาแนบรูปสลิป");
        if (length > options.MaxFileSizeBytes)
            throw PaymentErrors.InvalidFile($"ไฟล์ต้องมีขนาดไม่เกิน {options.MaxFileSizeBytes / 1024 / 1024} MB");

        var ext = Path.GetExtension(fileName ?? "");
        if (!Supported.Contains(ext) || !options.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw PaymentErrors.InvalidFile("รองรับเฉพาะไฟล์ .jpg .jpeg .png .webp");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        if (buffer.Length > options.MaxFileSizeBytes)
            throw PaymentErrors.InvalidFile($"ไฟล์ต้องมีขนาดไม่เกิน {options.MaxFileSizeBytes / 1024 / 1024} MB");
        var image = FromBytes(buffer.ToArray()) ?? throw PaymentErrors.InvalidFile("ไฟล์ไม่ใช่รูปภาพที่รองรับ");
        return image with { OriginalName = Path.GetFileName(fileName) };
    }

    /// ชนิดจาก magic bytes; null = ไม่ใช่ JPEG/PNG/WebP
    public static SlipImage? FromBytes(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return new(b, ".jpg", "image/jpeg");
        if (b.Length >= 8 && b.AsSpan(0, 8).SequenceEqual(PngMagic)) return new(b, ".png", "image/png");
        if (b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return new(b, ".webp", "image/webp");
        return null;
    }

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
}
