using CashPilot.Domain.Reports;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class SpendingReportTests
{
    private static Transaction Tx(string date, decimal amount, TransactionType type, string? category = null, string? item = null) =>
        new()
        {
            Account = "Conta X",
            Date = DateOnly.ParseExact(date, "yyyy-MM-dd"),
            Amount = amount,
            RawDescription = "Exemplo",
            Type = type,
            Category = category,
            Item = item,
        };

    [Fact]
    public void TransfersBillPaymentsAndCashOutAreNotSpending()
    {
        var months = SpendingReport.ByMonth(new[]
        {
            Tx("2026-09-01", -100m, TransactionType.Expense, "Alimentação", "Supermercado"),
            Tx("2026-09-02", -500m, TransactionType.InternalTransfer),
            Tx("2026-09-03", -800m, TransactionType.CardBillPayment),
            Tx("2026-09-04", -1000m, TransactionType.CardCashAdvance),
        });

        var september = Assert.Single(months);
        Assert.Equal(100m, september.Total);
        Assert.Equal(1, september.Count);
    }

    [Fact]
    public void CategorizedRefundReducesTheCategoryButUncategorizedIncomeIsIgnored()
    {
        var months = SpendingReport.ByMonth(new[]
        {
            Tx("2026-09-01", -100m, TransactionType.Expense, "Compras", "Geral"),
            Tx("2026-09-05", 30m, TransactionType.Income, "Compras", "Geral"),
            Tx("2026-09-06", 999m, TransactionType.Income),
        });

        var line = Assert.Single(Assert.Single(months).Lines);
        Assert.Equal(70m, line.Total);
        Assert.Equal(2, line.Count);
    }

    [Fact]
    public void GroupsByMonthOldestFirstAndKeepsUncategorizedVisible()
    {
        var months = SpendingReport.ByMonth(new[]
        {
            Tx("2026-09-10", -40m, TransactionType.Expense),
            Tx("2026-08-10", -10m, TransactionType.Expense, "Lazer", "Passeios"),
            Tx("2026-09-11", -60m, TransactionType.Expense, "Lazer", "Passeios"),
        });

        Assert.Equal(new[] { (2026, 8), (2026, 9) }, months.Select(m => (m.Year, m.Month)).ToArray());
        var september = months[1];
        Assert.Equal(100m, september.Total);
        Assert.Equal(40m, september.Uncategorized);
        Assert.Equal("Lazer", september.Lines[0].Category);
        Assert.Equal(SpendingReport.Uncategorized, september.Lines[1].Category);
    }
}
