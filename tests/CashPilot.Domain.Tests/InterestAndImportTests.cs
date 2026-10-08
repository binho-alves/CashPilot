using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Interest;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class InterestAndImportTests
{
    [Fact]
    public void CashAdvanceOf1000At308PercentCosts3080AndNets96920()
    {
        var advance = new CashAdvanceFeeTable().Simulate(1000m);

        Assert.Equal(30.80m, advance.Cost);
        Assert.Equal(969.20m, advance.Net);
    }

    [Fact]
    public void UnregisteredInstallmentFeeFailsClearly()
    {
        var table = new CashAdvanceFeeTable();
        Assert.Throws<InvalidOperationException>(() => table.Simulate(1000m, installments: 3));

        table.Set(3, 9.5m);
        Assert.Equal(95m, table.Simulate(1000m, 3).Cost);
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
