using HuebRun.Domain;

namespace HuebRun.Tests.Domain;

public class PromptPayPayloadTests
{
    [Fact]
    public void Crc16_matches_ccitt_false_check_value() =>
        Assert.Equal(0x29B1, PromptPayPayload.Crc16("123456789"));

    [Fact]
    public void Phone_number_payload_matches_reference()
    {
        // โครงตาม promptpay-qr: generatePayload('0801234567', { amount: 4.22 }); CRC ยืนยันด้วย Python binascii.crc_hqx(init 0xFFFF)
        Assert.Equal("00020101021229370016A000000677010111011300668012345675802TH530376454044.22630444FE",
            PromptPayPayload.Create("080-123-4567", 4.22m));
    }

    [Fact]
    public void National_id_uses_tag_02_and_two_decimal_amount()
    {
        var payload = PromptPayPayload.Create("1234567890123", 400.01m);
        Assert.Contains("29370016A00000067701011102131234567890123", payload);
        Assert.Contains("5406400.01", payload);
        Assert.Equal(PromptPayPayload.Crc16(payload[..^4]).ToString("X4"), payload[^4..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1812345678")]
    public void Invalid_id_throws(string id) =>
        Assert.Throws<InvalidOperationException>(() => PromptPayPayload.Create(id, 100m));
}
