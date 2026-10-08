using CashPilot.Domain.Accounts;
using CashPilot.Domain.Interest;

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
}
