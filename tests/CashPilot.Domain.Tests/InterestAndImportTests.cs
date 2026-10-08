using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Interest;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class InterestAndImportTests
{
    [Fact]
    public void CashAdvanceOf1000At309PercentCosts3090AndNets96910()
    {
        var advance = new CashAdvanceFeeTable().Simulate(1000m);

        Assert.Equal(30.90m, advance.Cost);
        Assert.Equal(969.10m, advance.Net);
    }

    [Theory]
    [InlineData(1, 30.90, 969.10)]
    [InlineData(2, 57.90, 942.10)]
    [InlineData(3, 60.90, 939.10)]
    [InlineData(4, 79.90, 920.10)]
    [InlineData(5, 80.90, 919.10)]
    [InlineData(6, 81.90, 918.10)]
    [InlineData(7, 94.90, 905.10)]
    [InlineData(8, 96.80, 903.20)]
    [InlineData(9, 103.70, 896.30)]
    [InlineData(10, 110.50, 889.50)]
    [InlineData(11, 122.70, 877.30)]
    [InlineData(12, 123.80, 876.20)]
    public void TerminalFeeTableMatchesTheTerminalApp(int installments, double cost, double net)
    {
        var advance = new CashAdvanceFeeTable().Simulate(1000m, installments);

        Assert.Equal((decimal)cost, advance.Cost);
        Assert.Equal((decimal)net, advance.Net);
    }

    [Fact]
    public void UnregisteredInstallmentFeeFailsClearly()
    {
        var table = new CashAdvanceFeeTable();
        Assert.Throws<InvalidOperationException>(() => table.Simulate(1000m, installments: 13));

        table.Set(13, 9.5m);
        Assert.Equal(95m, table.Simulate(1000m, 13).Cost);
    }

    [Theory]
    [InlineData("R$ 1.000,00", 1000.00)]
    [InlineData("R$ 3.564,21", 3564.21)]
    [InlineData("R$ 0,02", 0.02)]
    [InlineData("-R$ 5,00", -5.00)]
    [InlineData("(R$ 5,00)", -5.00)]
    public void ParsesBrazilianCurrency(string text, double expected) =>
        Assert.Equal((decimal)expected, BrazilianCurrencyParser.Parse(text));

    [Fact]
    public void DedupKeyIsStableAndDistinguishesOccurrences()
    {
        var a = new Transaction { Account = "Itaú", Date = new DateOnly(2026, 9, 1), Amount = -10m, RawDescription = "PIX QRS X 01/09" };
        var b = a with { Id = Guid.NewGuid(), RawDescription = "pix qrs x 05/09" };

        Assert.Equal(a.ComputeDedupKey(), b.ComputeDedupKey());
        Assert.NotEqual(a.ComputeDedupKey(1), a.ComputeDedupKey(2));
    }

    [Theory]
    [InlineData("PAGAMENTO FATURA CARTAO", true)]
    [InlineData("SUPERMERCADO EXTRA", false)]
    public void DetectsCardBillPayment(string normalized, bool expected) =>
        Assert.Equal(expected, new CardBillPaymentDetector().IsCardBillPayment(normalized));
}
