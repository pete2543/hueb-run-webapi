using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HuebRun.Domain;
using HuebRun.Tests.Infrastructure;

namespace HuebRun.Tests.Api;

[Collection("db")]
public sealed class LookupsEndpointTests(PostgresFixture pg) : IDisposable
{
    private readonly ApiFactory _factory = new(pg.ConnectionString);
    public void Dispose() => _factory.Dispose();

    private async Task<JsonElement> GetLookups()
    {
        var res = await _factory.CreateClient().GetAsync("/api/v1/lookups");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Returns_all_four_lists_sorted_by_sort_order()
    {
        var body = await GetLookups();

        var titles = body.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("nameTh").GetString()).ToList();
        Assert.Equal(["นาย", "นาง", "นางสาว", "เด็กชาย", "เด็กหญิง"], titles.Take(5));
        Assert.Equal("Mr.", body.GetProperty("titles")[0].GetProperty("nameEn").GetString());
        Assert.NotEmpty(body.GetProperty("nationalities").EnumerateArray());
        Assert.NotEmpty(body.GetProperty("bloodTypes").EnumerateArray());
        Assert.Equal("อื่น ๆ", body.GetProperty("emergencyRelations").EnumerateArray().Last().GetProperty("nameTh").GetString());
    }

    [Fact]
    public async Task Inactive_items_are_hidden()
    {
        await GetLookups(); // ให้ seed ทำงานก่อน
        await using (var db = pg.CreateDbContext())
        {
            db.Titles.Add(new Title { NameTh = "ยศที่ปิดแล้ว", SortOrder = 50, IsActive = false });
            await db.SaveChangesAsync();
        }

        var body = await GetLookups();
        Assert.DoesNotContain(body.GetProperty("titles").EnumerateArray(),
            t => t.GetProperty("nameTh").GetString() == "ยศที่ปิดแล้ว");
    }
}
