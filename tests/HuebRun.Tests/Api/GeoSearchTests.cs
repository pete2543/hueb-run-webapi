using HuebRun.Api.Geo;

namespace HuebRun.Tests.Api;

public class GeoSearchTests
{
    private static GeoEntry E(int id, string sub, string district, string province, string zip,
        string? subEn = null, string? districtEn = null, string? provinceEn = null) =>
        new(id, sub, subEn, id / 100, district, districtEn, id / 10000, province, provinceEn, zip);

    private static readonly IReadOnlyList<GeoEntry> Entries =
    [
        E(100101, "พระบรมมหาราชวัง", "เขตพระนคร", "กรุงเทพมหานคร", "10200", "Phra Borom Maha Ratchawang", "Khet Phra Nakhon", "Bangkok"),
        E(100401, "มหาพฤฒาราม", "เขตบางรัก", "กรุงเทพมหานคร", "10500"),
        E(100402, "สีลม", "เขตบางรัก", "กรุงเทพมหานคร", "10500"),
        E(100405, "บางรัก", "เขตบางรัก", "กรุงเทพมหานคร", "10500"),
        E(901101, "หาดใหญ่", "หาดใหญ่", "สงขลา", "90110"),
        E(901102, "ควนลัง", "หาดใหญ่", "สงขลา", "90110"),
        E(501101, "ศรีภูมิ", "เมืองเชียงใหม่", "เชียงใหม่", "50200"),
        E(500401, "เมืองนะ", "เชียงดาว", "เชียงใหม่", "50170"),
    ];

    private static List<string> Subs(string q, int limit = 20) =>
        GeoSearch.Search(Entries, q, limit).Select(e => e.SubdistrictTh).ToList();

    [Fact]
    public void Finds_by_subdistrict_name() => Assert.Equal(["สีลม"], Subs("สีลม"));

    [Fact]
    public void Finds_by_district_name() =>
        Assert.Equal(["หาดใหญ่", "ควนลัง"], Subs("หาดใหญ่"));

    [Fact]
    public void Finds_by_province_name() =>
        Assert.Equal(["ควนลัง", "หาดใหญ่"], Subs("สงขลา").Order().ToList());

    [Fact]
    public void Finds_by_zip_prefix() =>
        Assert.Equal(["ควนลัง", "หาดใหญ่"], Subs("901").Order().ToList());

    [Fact]
    public void Full_zip_matches_every_subdistrict_with_that_zip() =>
        Assert.Equal(3, Subs("10500").Count);

    [Fact]
    public void Every_word_must_match_somewhere()
    {
        Assert.Equal(["สีลม"], Subs("สีลม กรุงเทพ"));
        Assert.Equal(["ควนลัง"], Subs("ควนลัง 90110"));
        Assert.Empty(Subs("สีลม สงขลา"));
    }

    [Theory]
    [InlineData("ต.สีลม")]
    [InlineData("ตำบล สีลม")]
    [InlineData("แขวงสีลม")]
    public void Address_prefixes_in_query_are_ignored(string q) => Assert.Equal(["สีลม"], Subs(q));

    [Fact]
    public void District_and_province_prefixes_are_ignored()
    {
        Assert.Equal(["ศรีภูมิ"], Subs("อ.เมือง จ.เชียงใหม่"));
        Assert.Equal(3, Subs("เขตบางรัก").Count);
    }

    [Fact]
    public void English_search_is_case_insensitive() =>
        Assert.Equal(["พระบรมมหาราชวัง"], Subs("phra NAKHON"));

    [Fact]
    public void Subdistrict_name_prefix_ranks_before_district_match() =>
        Assert.Equal("บางรัก", Subs("บางรัก")[0]);

    [Fact]
    public void Limit_caps_results() => Assert.Equal(2, Subs("กรุงเทพ", limit: 2).Count);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ส")]
    [InlineData("ต.")]
    public void Too_short_query_returns_nothing(string q) => Assert.Empty(Subs(q));

    [Fact]
    public void No_match_returns_nothing() => Assert.Empty(Subs("ไม่มีที่นี่"));

    [Fact]
    public void Label_shows_full_address()
    {
        var hit = GeoSearch.Search(Entries, "สีลม", 20).Single();
        Assert.Equal("สีลม » เขตบางรัก » กรุงเทพมหานคร 10500", hit.Label);
    }

    [Fact]
    public void District_prefix_restricts_word_to_district_name()
    {
        Assert.Equal(["ศรีภูมิ"], Subs("อ.เมือง จ.เชียงใหม่"));
        Assert.Equal(["เมืองนะ"], Subs("ต.เมือง จ.เชียงใหม่"));
    }

    [Fact]
    public void Province_prefix_restricts_word_to_province_name() =>
        Assert.Empty(Subs("จ.หาดใหญ่"));

    [Fact]
    public void Zip_only_query_is_sorted_by_zip() =>
        Assert.Equal(["10200", "10500", "10500", "10500"], GeoSearch.Search(Entries, "10", 20).Select(e => e.ZipCode).ToList());
}
