using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class GeoEndpointTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg.ConnectionString);
    private HttpClient Client => _factory.CreateClient();
    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> Body(HttpResponseMessage res, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Provinces_returns_77()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/provinces"));
        Assert.Equal(77, body.GetArrayLength());
        Assert.Contains(body.EnumerateArray(), p => p.GetProperty("nameTh").GetString() == "กรุงเทพมหานคร");
    }

    [Fact]
    public async Task Bangkok_districts_then_subdistricts_cascade()
    {
        var districts = await Body(await Client.GetAsync("/api/v1/geo/provinces/1/districts"));
        var phraNakhon = districts.EnumerateArray().Single(d => d.GetProperty("nameTh").GetString() == "เขตพระนคร");

        var subs = await Body(await Client.GetAsync($"/api/v1/geo/districts/{phraNakhon.GetProperty("id").GetInt32()}/subdistricts"));
        var first = subs.EnumerateArray().Single(s => s.GetProperty("nameTh").GetString() == "พระบรมมหาราชวัง");
        Assert.Equal("10200", first.GetProperty("zipCode").GetString());
    }

    [Fact]
    public async Task Unknown_province_is_404()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/provinces/999999/districts"), HttpStatusCode.NotFound);
        Assert.Equal("PROVINCE_NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_district_is_404()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/districts/999999/subdistricts"), HttpStatusCode.NotFound);
        Assert.Equal("DISTRICT_NOT_FOUND", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Non_numeric_id_is_validation_error()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/provinces/abc/districts"), HttpStatusCode.BadRequest);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Zip_lookup_returns_full_address_chain()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/zip/10200"));
        var match = body.EnumerateArray().First(m => m.GetProperty("subdistrict").GetProperty("nameTh").GetString() == "พระบรมมหาราชวัง");
        Assert.Equal("เขตพระนคร", match.GetProperty("district").GetProperty("nameTh").GetString());
        Assert.Equal("กรุงเทพมหานคร", match.GetProperty("province").GetProperty("nameTh").GetString());
        Assert.Equal("10200", match.GetProperty("zipCode").GetString());
    }

    [Fact]
    public async Task Unknown_zip_returns_empty_list()
    {
        var body = await Body(await Client.GetAsync("/api/v1/geo/zip/99999"));
        Assert.Equal(0, body.GetArrayLength());
    }

    [Theory]
    [InlineData("1020")]
    [InlineData("102000")]
    [InlineData("10a00")]
    [InlineData("๑๐๒๐๐")]
    public async Task Malformed_zip_is_validation_error(string zip)
    {
        var body = await Body(await Client.GetAsync($"/api/v1/geo/zip/{Uri.EscapeDataString(zip)}"), HttpStatusCode.BadRequest);
        Assert.Equal("VALIDATION_ERROR", body.GetProperty("code").GetString());
    }
}
