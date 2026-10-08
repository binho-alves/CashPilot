using CashPilot.Domain.Accounts;
using CashPilot.Domain.Interest;
using CashPilot.Domain.Payments;

namespace CashPilot.Domain.Tests;

public class CostSimulatorTests
{
    private static readonly Account Bank = new()
    {
        Name = "Banco Exemplo",
        Kind = AccountKind.BankAccount,
        OverdraftLimit = 3000m,
        OverdraftFreeDays = 10,
        OverdraftMonthlyRatePercent = 8.5m,
    };

    [Theory]
    [InlineData(1000, 5, 0)]       // inside the free days
    [InlineData(1000, 10, 0)]      // exactly the free days
    [InlineData(1000, 30, 56.67)]  // 20 charged days: 1000 x 8.5% x 20/30
    [InlineData(1000, 40, 85.00)]  // 30 charged days = one full month
    public void OverdraftChargesOnlyDaysBeyondTheFreeOnes(decimal amount, int days, decimal expected) =>
        Assert.Equal(expected, CostSimulator.OverdraftCost(amount, days, 10, 8.5m));

    [Theory]
    [InlineData(1000, 3.09, 1)]
    [InlineData(1500.50, 8.19, 6)]
    [InlineData(333.33, 12.38, 12)]
    public void CashOutNetCoversTheAmountWithAlmostNoOvershoot(decimal net, decimal fee, int installments)
    {
        var result = CostSimulator.CashOutForNet(net, fee, installments);

        Assert.True(result.Net >= net);
        Assert.True(result.Net - net < 0.02m); // no more than a couple of cents of overshoot
    }

    [Fact]
    public void CompareRanksTheCheapestFirstAndKeepsUnfeasibleLast()
    {
        var balances = new Dictionary<string, decimal> { ["Banco Exemplo"] = 0m };

        // 8 days: the LIS is free, so it beats any terminal fee.
        var short8 = CostSimulator.Compare(1000m, 8, new[] { Bank }, balances);
        Assert.Equal(FundingKind.Overdraft, short8[0].Kind);
        Assert.Equal(0m, short8[0].Cost);
        Assert.Equal(13, short8.Count); // LIS + 12 terminal options

        // 90 days: LIS = 1000 x 8.5% x 80/30 = 226.67, dearer than 1x at 3.09%.
        var long90 = CostSimulator.Compare(1000m, 90, new[] { Bank }, balances);
        Assert.Equal("Saque na maquininha 1x", long90[0].Name);
        Assert.True(long90[0].Cost < long90.Single(o => o.Kind == FundingKind.Overdraft).Cost);

        // Above the LIS limit: not feasible, listed after the feasible ones.
        var big = CostSimulator.Compare(5000m, 8, new[] { Bank }, balances);
        var lis = big.Single(o => o.Kind == FundingKind.Overdraft);
        Assert.False(lis.Feasible);
        Assert.Same(lis, big[^1]);
    }

    [Fact]
    public void OverdraftAlreadyInUseReducesWhatIsAvailable()
    {
        var balances = new Dictionary<string, decimal> { ["Banco Exemplo"] = -2500m };

        var options = CostSimulator.Compare(1000m, 8, new[] { Bank }, balances);

        var lis = options.Single(o => o.Kind == FundingKind.Overdraft);
        Assert.False(lis.Feasible); // 3000 - 2500 = 500 left
    }

    [Fact]
    public void BanksWithoutALisRateAreSkipped()
    {
        var noRate = new Account { Name = "Sem LIS", Kind = AccountKind.BankAccount };

        var options = CostSimulator.Compare(500m, 5, new[] { noRate }, new Dictionary<string, decimal>());

        Assert.All(options, o => Assert.Equal(FundingKind.CardCashOut, o.Kind));
    }
    [Theory]
    [InlineData(1000, 0)]    // paid on the due date: nothing extra
    [InlineData(1000, -3)]   // paid early
    [InlineData(1000, 1)]    // 2% fee + 1 day of 1% a.m.: 20 + 0.33
    [InlineData(1000, 30)]   // 2% fee + one full month of interest: 20 + 10
    public void BoletoLateCostIsTheFeePlusProRataInterest(decimal amount, int daysLate)
    {
        var expected = daysLate <= 0 ? 0m : Math.Round(amount * 0.02m + amount * 0.01m * daysLate / 30m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, CostSimulator.BoletoLateCost(amount, 2m, 1m, daysLate));
    }

    private static readonly DateOnly Today = new(2026, 10, 8);

    private static IReadOnlyList<FundingOption> CompareWith(decimal amount, int days, params Payable[] payables) =>
        CostSimulator.Compare(amount, days, new[] { Bank }, new Dictionary<string, decimal>(), payables: payables, today: Today);

    [Fact]
    public void ABoletoDueBeforeTheEndOfThePeriodIsAnOptionWithItsExtraCost()
    {
        var bill = new Payable
        {
            Description = "Condomínio", DueDate = new DateOnly(2026, 10, 18), Amount = 1000m,
            LateFeePercent = 2m, LateInterestMonthlyPercent = 1m,
        };

        var option = Assert.Single(CompareWith(1000m, 30, bill), o => o.Kind == FundingKind.LateBoleto);

        // Paid on 07/11, due 18/10: 20 days late = 20 + 1000 x 1% x 20/30.
        Assert.Equal(26.67m, option.Cost);
        Assert.True(option.Feasible);
        Assert.Contains("20 dias", option.Detail);
    }

    [Fact]
    public void ABoletoAlreadyOverdueOnlyAddsTheInterestOfTheNewDays()
    {
        var bill = new Payable
        {
            Description = "Luz", DueDate = new DateOnly(2026, 10, 3), Amount = 900m,
            LateFeePercent = 2m, LateInterestMonthlyPercent = 1m,
        };

        var option = Assert.Single(CompareWith(900m, 10, bill), o => o.Kind == FundingKind.LateBoleto);

        // 5 days late today, 15 when paid: the fee is already owed, only 10 more days of interest = 900 x 1% x 10/30.
        Assert.Equal(3m, option.Cost);
    }

    [Fact]
    public void BoletosThatAreNotRelevantAreLeftOut()
    {
        var farAway = new Payable { Description = "Longe", DueDate = new DateOnly(2026, 12, 1), Amount = 500m, LateFeePercent = 2m, LateInterestMonthlyPercent = 1m };
        var noTerms = new Payable { Description = "Sem taxas", DueDate = new DateOnly(2026, 10, 10), Amount = 500m };
        var paid = new Payable { Description = "Pago", DueDate = new DateOnly(2026, 10, 10), Amount = 500m, Paid = true, LateFeePercent = 2m, LateInterestMonthlyPercent = 1m };

        Assert.DoesNotContain(CompareWith(500m, 30, farAway, noTerms, paid), o => o.Kind == FundingKind.LateBoleto);
    }

    [Fact]
    public void ABoletoSmallerThanTheNeedDoesNotCoverIt()
    {
        var bill = new Payable { Description = "Pequeno", DueDate = new DateOnly(2026, 10, 10), Amount = 200m, LateFeePercent = 2m, LateInterestMonthlyPercent = 1m };

        var option = Assert.Single(CompareWith(1000m, 30, bill), o => o.Kind == FundingKind.LateBoleto);

        Assert.False(option.Feasible);
        Assert.Contains("200", option.Note);
    }
}
