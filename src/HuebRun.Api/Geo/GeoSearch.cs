namespace HuebRun.Api.Geo;

/// ค้นหาที่อยู่จากช่องเดียว: ชื่อตำบล/อำเภอ/จังหวัด (ไทย/อังกฤษ) หรือรหัสไปรษณีย์ หลายคำคั่นด้วยช่องว่าง
public static class GeoSearch
{
    public const int MinQueryLength = 2;

    private enum Field { Any, Subdistrict, District, Province }

    private sealed record Term(Field Field, string Text, bool IsZip);

    // คำนำหน้าบอกว่าคำนั้นเป็นชื่อระดับไหน (ยาวก่อน เพื่อไม่ให้ "ต." ตัด "ตำบล" ครึ่งเดียว)
    private static readonly (string Prefix, Field Field)[] Prefixes =
    [
        ("จังหวัด", Field.Province), ("อำเภอ", Field.District), ("ตำบล", Field.Subdistrict),
        ("แขวง", Field.Subdistrict), ("เขต", Field.District),
        ("จ.", Field.Province), ("อ.", Field.District), ("ต.", Field.Subdistrict),
        ("khwaeng ", Field.Subdistrict), ("khet ", Field.District), ("amphoe ", Field.District), ("tambon ", Field.Subdistrict),
    ];

    public static IReadOnlyList<GeoEntry> Search(IReadOnlyList<GeoEntry> entries, string? query, int limit)
    {
        var trimmed = query?.Trim() ?? "";
        if (trimmed.Length < MinQueryLength) return [];

        var terms = Parse(trimmed.ToLowerInvariant());
        if (terms.Count == 0) return [];

        var textTerms = terms.Where(t => !t.IsZip).ToList();
        var zipOnly = textTerms.Count == 0;

        return entries
            .Where(e => terms.All(t => Matches(e, t)))
            .Select(e => (Entry: e, Rank: Rank(e, textTerms)))
            .OrderBy(x => x.Rank)
            .ThenBy(x => zipOnly ? x.Entry.ZipCode : "", StringComparer.Ordinal)
            .ThenBy(x => x.Entry.SubdistrictTh.Length)
            .ThenBy(x => x.Entry.ProvinceTh, StringComparer.Ordinal)
            .ThenBy(x => x.Entry.DistrictTh, StringComparer.Ordinal)
            .ThenBy(x => x.Entry.SubdistrictTh, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    // แยกคำ: "อ.เมือง" และ "อำเภอ เมือง" ได้ (District, "เมือง") เหมือนกัน
    private static List<Term> Parse(string query)
    {
        var terms = new List<Term>();
        Field? pending = null;
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (text, field) = SplitPrefix(word);
            if (text.Length == 0)
            {
                pending = field;
                continue;
            }
            if (IsAsciiDigits(text))
                terms.Add(new Term(Field.Any, text, IsZip: true));
            else
                terms.Add(new Term(field == Field.Any ? pending ?? Field.Any : field, text, IsZip: false));
            pending = null;
        }
        return terms;
    }

    private static (string Text, Field Field) SplitPrefix(string value)
    {
        foreach (var (prefix, field) in Prefixes)
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return (value[prefix.Length..].Trim(), field);
        return (value, Field.Any);
    }

    internal static string StripPrefix(string value) => SplitPrefix(value).Text;

    private static bool Matches(GeoEntry e, Term t)
    {
        if (t.IsZip) return e.ZipCode.StartsWith(t.Text, StringComparison.Ordinal);
        return t.Field switch
        {
            Field.Subdistrict => ContainsAny(e.SubdistrictNames, t.Text),
            Field.District => ContainsAny(e.DistrictNames, t.Text),
            Field.Province => ContainsAny(e.ProvinceNames, t.Text),
            _ => e.Haystack.Contains(t.Text, StringComparison.Ordinal),
        };
    }

    // 0 = ชื่อตำบลขึ้นต้นด้วยคำค้น, 1 = ชื่ออำเภอ, 2 = ชื่อจังหวัด, 3 = เจอแค่ตรงกลางคำหรือรหัสไปรษณีย์
    private static int Rank(GeoEntry e, List<Term> textTerms)
    {
        bool StartsWithAny(string[] names) =>
            textTerms.Any(t => names.Any(n => n.StartsWith(t.Text, StringComparison.Ordinal)));
        if (StartsWithAny(e.SubdistrictNames)) return 0;
        if (StartsWithAny(e.DistrictNames)) return 1;
        if (StartsWithAny(e.ProvinceNames)) return 2;
        return 3;
    }

    private static bool ContainsAny(string[] names, string text) =>
        names.Any(n => n.Contains(text, StringComparison.Ordinal));

    private static bool IsAsciiDigits(string s) => s.All(c => c is >= '0' and <= '9');
}
