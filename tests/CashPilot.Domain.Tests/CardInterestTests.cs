using CashPilot.Domain.Accounts;
using CashPilot.Domain.Interest;

namespace CashPilot.Domain.Tests;

public class CardInterestTests
{
    [Fact]
    public void ThirtyDaysCostsTheMonthlyRatePlusIof()
    {
        // Same shape as a real bill: 141.86 financed at 17% a month -> 24.12 of charges and 0.89 of IOF.
        Assert.Equal(24.12m, CardInterest.Interest(141.86m, 30, 17m));
        Assert.Equal(0.89m, CardInterest.Iof(141.86m, 30));
        Assert.Equal(25.01m, CardInterest.CarryCost(141.86m, 30, 17m));
    }

    [Fact]
    public void ShorterStaysCostLess()
    {
        var fifteen = CardInterest.CarryCost(1000m, 15, 15m);
        var thirty = CardInterest.CarryCost(1000m, 30, 15m);
        Assert.True(fifteen < thirty);
        Assert.True(fifteen > 0m);
    }

    [Fact]
    public void IofDailyPartStopsAtOneYear()
    {
        Assert.Equal(CardInterest.Iof(1000m, 365), CardInterest.Iof(1000m, 900));
    }

    [Fact]
    public void CostNeverExceedsTheAmountCarried()
    {
        Assert.Equal(100m, CardInterest.CarryCost(100m, 365, 20m));
    }

    [Fact]
    public void BeyondOneCycleTheInstallmentRateApplies()
    {
        var revolving = CardInterest.CarryCost(1000m, 60, 17m, null);
        var installment = CardInterest.CarryCost(1000m, 60, 17m, 10m);
        Assert.True(installment < revolving);
        // Within a cycle the installment rate is ignored.
        Assert.Equal(CardInterest.CarryCost(1000m, 30, 17m), CardInterest.CarryCost(1000m, 30, 17m, 10m));
    }

    [Fact]
    public void LateBillAddsFeeAndMoraOnTopOfTheRevolving()
    {
        // 1000 for 30 days late at 17%: 20 multa + 10 mora + 170 interest + 6.26 IOF
        Assert.Equal(206.26m, CardInterest.LateCost(1000m, 30, 17m));
        Assert.True(CardInterest.LateCost(1000m, 30, 17m) > CardInterest.CarryCost(1000m, 30, 17m));
    }

    [Fact]
    public void LateCostIsCappedAndZeroWhenNotLate()
    {
        Assert.Equal(100m, CardInterest.LateCost(100m, 365, 20m));
        Assert.Equal(0m, CardInterest.LateCost(1000m, 0, 17m));
    }

    [Fact]
    public void NothingToCarryCostsNothing()
    {
        Assert.Equal(0m, CardInterest.CarryCost(0m, 30, 17m));
        Assert.Equal(0m, CardInterest.CarryCost(500m, 0, 17m));
    }

    [Theory]
    [InlineData("Cartão PF Itaú Pão de Açúcar", 17.00, null, false)]
    [InlineData("Cartão PF Itaú Uniclass Mult Signature", 15.23, null, false)]
    [InlineData("Cartão PF Itaú Uniclass Signature", 15.23, 13.40, false)]
    [InlineData("Cartão PF Pic Pay", 14.90, null, false)]
    [InlineData("Cartão PF MercadoPago", 17.90, 15.90, false)]
    [InlineData("Cartão PJ FLM Bradesco", 15.99, 13.30, false)]
    [InlineData("Cartão PF Bradesco", 15.99, 13.30, true)]
    [InlineData("Cartão PF Neon", 15.00, null, true)]
    public void KnownRatesAreFoundByName(string name, double revolving, double? installment, bool estimated)
    {
        var rate = KnownCardRates.Find(name);
        Assert.NotNull(rate);
        Assert.Equal((decimal)revolving, rate!.RevolvingPercent);
        Assert.Equal(installment is { } i ? (decimal?)(decimal)i : null, rate.InstallmentPercent);
        Assert.Equal(estimated, rate.Estimated);
    }

    [Fact]
    public void UnknownCardHasNoRate() => Assert.Null(KnownCardRates.Find("Cartão Qualquer"));

    [Fact]
    public void SimulatorOffersTheRevolvingOfEachCardWithARate()
    {
        var accounts = new[]
        {
            new Account { Name = "Cartão A", Kind = AccountKind.CreditCard, RevolvingMonthlyRatePercent = 17m },
            new Account { Name = "Cartão B", Kind = AccountKind.CreditCard, RevolvingMonthlyRatePercent = 14.9m },
            new Account { Name = "Cartão C", Kind = AccountKind.CreditCard },
        };

        var options = CostSimulator.Compare(1000m, 30, accounts, new Dictionary<string, decimal>());

        var revolving = options.Where(o => o.Kind == FundingKind.CardRevolving).ToList();
        Assert.Equal(2, revolving.Count);
        Assert.Equal(2, options.Count(o => o.Kind == FundingKind.LateCardBill));
        Assert.Equal(CardInterest.CarryCost(1000m, 30, 14.9m), revolving.Min(o => o.Cost));
        Assert.DoesNotContain(revolving, o => o.Name.Contains("Cartão C"));
    }
}
