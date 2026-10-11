using HuebRun.Infrastructure.Payments;

namespace HuebRun.Tests.Infrastructure;

/// รูปปลอมที่ magic bytes ถูกต้อง เปลี่ยน seed แล้ว hash ไม่ซ้ำ
public static class Slips
{
    public static byte[] Png(int seed) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. BitConverter.GetBytes(seed), .. BitConverter.GetBytes(Environment.TickCount64)];

    public static SlipImage Image(int seed = 0) => new(Png(seed), ".png", "image/png");
}
