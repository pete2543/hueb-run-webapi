using System.Globalization;
using System.Text;

namespace HuebRun.Domain;

/// string QR PromptPay แบบ dynamic (EMVCo) ที่ระบุยอด หน้าเว็บนำไปวาด QR เอง
public static class PromptPayPayload
{
    private const string Aid = "A000000677010111";

    public static string Create(string promptPayId, decimal amount)
    {
        var merchant = Tlv("00", Aid) + Target(promptPayId);
        var body = Tlv("00", "01") + Tlv("01", "12") + Tlv("29", merchant) + Tlv("58", "TH") + Tlv("53", "764")
                 + Tlv("54", amount.ToString("0.00", CultureInfo.InvariantCulture)) + "6304";
        return body + Crc16(body).ToString("X4");
    }

    // เบอร์โทร 10 หลัก → 0066 + 9 หลัก; เลขบัตร/ภาษี 13 หลัก; e-wallet 15 หลัก
    private static string Target(string id)
    {
        var digits = new string(id.Where(char.IsAsciiDigit).ToArray());
        return digits.Length switch
        {
            10 when digits[0] == '0' => Tlv("01", "0066" + digits[1..]),
            13 => Tlv("02", digits),
            15 => Tlv("03", digits),
            _ => throw new InvalidOperationException($"Invalid PromptPay ID (digits: {digits.Length})"),
        };
    }

    private static string Tlv(string tag, string value) => tag + value.Length.ToString("D2") + value;

    /// CRC-16/CCITT-FALSE (init 0xFFFF, poly 0x1021)
    public static ushort Crc16(string data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in Encoding.ASCII.GetBytes(data))
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
