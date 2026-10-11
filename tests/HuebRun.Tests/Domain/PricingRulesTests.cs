using HuebRun.Domain;

namespace HuebRun.Tests.Domain;

public class PricingRulesTests
{
    private static readonly DateTime Cutoff = new(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);

    private static EventDistance Distance(decimal? earlyBird = 390m, DateTime? endsAt = null) => new()
    {
        Price = 450m,
        EarlyBirdPrice = earlyBird,
        EarlyBirdEndsAt = earlyBird is null ? null : endsAt ?? Cutoff,
        Quota = 100,
    };

    [Fact]
    public void Before_cutoff_uses_early_bird_price()
    {
        var d = Distance();
        Assert.True(d.IsEarlyBird(Cutoff.AddSeconds(-1)));
        Assert.Equal(390m, d.GetEffectivePrice(Cutoff.AddSeconds(-1)));
    }

    [Fact]
    public void At_cutoff_uses_regular_price()
    {
        var d = Distance();
        Assert.False(d.IsEarlyBird(Cutoff));
        Assert.Equal(450m, d.GetEffectivePrice(Cutoff));
    }

    [Fact]
    public void Without_early_bird_uses_regular_price()
    {
        var d = Distance(earlyBird: null);
        Assert.False(d.IsEarlyBird(Cutoff.AddDays(-30)));
        Assert.Equal(450m, d.GetEffectivePrice(Cutoff.AddDays(-30)));
    }

    [Fact]
    public void Early_bird_price_without_end_date_is_ignored()
    {
        var d = new EventDistance { Price = 450m, EarlyBirdPrice = 390m, EarlyBirdEndsAt = null };
        Assert.False(d.IsEarlyBird(Cutoff));
        Assert.Equal(450m, d.GetEffectivePrice(Cutoff));
    }

    [Theory]
    [InlineData(100, 30, 70)]
    [InlineData(100, 100, 0)]
    [InlineData(50, 80, 0)]
    public void Distance_remaining_never_negative(int quota, int reserved, int expected) =>
        Assert.Equal(expected, new EventDistance { Quota = quota, ReservedCount = reserved }.GetRemaining());

    [Theory]
    [InlineData(20, 5, 15)]
    [InlineData(10, 12, 0)]
    public void Shirt_remaining_never_negative(int stock, int reserved, int expected) =>
        Assert.Equal(expected, new EventShirtSize { Stock = stock, ReservedCount = reserved }.GetRemaining());

    [Fact]
    public void Registration_open_window_is_inclusive_start_exclusive_end()
    {
        var opens = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var closes = new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        var e = new Event { RegistrationOpensAt = opens, RegistrationClosesAt = closes };

        Assert.False(e.IsRegistrationOpen(opens.AddSeconds(-1)));
        Assert.True(e.IsRegistrationOpen(opens));
        Assert.True(e.IsRegistrationOpen(closes.AddSeconds(-1)));
        Assert.False(e.IsRegistrationOpen(closes));
    }
}
