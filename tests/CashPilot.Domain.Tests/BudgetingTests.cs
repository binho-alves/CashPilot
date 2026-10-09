using CashPilot.Domain.Reports;

namespace CashPilot.Domain.Tests;

public class BudgetingTests
{
    private static MonthlySpending Month(int year, int month, params (string Category, decimal Total)[] lines) =>
        new(year, month, lines.Select(l => new SpendingLine(l.Category, "", l.Total, 1)).ToList());

    private static readonly DateOnly LateOctober = new(2026, 10, 28);

    [Fact]
    public void Status_follows_the_share_of_the_limit_spent()
    {
        var report = Budgeting.Build(
            [Month(2026, 10, ("Food", 850m), ("Leisure", 200m), ("Transport", 120m), ("Pet", 40m))],
            new Dictionary<string, decimal> { ["Food"] = 800m, ["Leisure"] = 250m, ["Transport"] = 500m },
            2026, 10, LateOctober);

        Assert.Equal(BudgetStatus.Over, report.Lines.Single(l => l.Category == "Food").Status);
        Assert.Equal(BudgetStatus.Near, report.Lines.Single(l => l.Category == "Leisure").Status);   // 80% exactly
        Assert.Equal(BudgetStatus.Ok, report.Lines.Single(l => l.Category == "Transport").Status);
        Assert.Equal(BudgetStatus.NoLimit, report.Lines.Single(l => l.Category == "Pet").Status);
        Assert.Equal(-50m, report.Lines.Single(l => l.Category == "Food").Remaining);
    }

    [Fact]
    public void Lines_come_with_the_most_urgent_first()
    {
        var report = Budgeting.Build(
            [Month(2026, 10, ("Food", 850m), ("Leisure", 200m), ("Transport", 120m), ("Pet", 40m))],
            new Dictionary<string, decimal> { ["Food"] = 800m, ["Leisure"] = 250m, ["Transport"] = 500m },
            2026, 10, LateOctober);

        Assert.Equal(["Food", "Leisure", "Transport", "Pet"], report.Lines.Select(l => l.Category));
    }

    [Fact]
    public void A_category_with_limit_and_no_spending_is_listed()
    {
        var report = Budgeting.Build(
            [Month(2026, 10, ("Food", 100m))],
            new Dictionary<string, decimal> { ["Gym"] = 90m }, 2026, 10, LateOctober);

        var gym = report.Lines.Single(l => l.Category == "Gym");
        Assert.Equal(0m, gym.Spent);
        Assert.Equal(90m, gym.Remaining);
    }

    [Fact]
    public void Uncategorized_is_reported_apart_and_never_gets_a_limit()
    {
        var report = Budgeting.Build(
            [Month(2026, 10, ("Food", 100m), (SpendingReport.Uncategorized, 70m))],
            new Dictionary<string, decimal> { [SpendingReport.Uncategorized] = 10m }, 2026, 10, LateOctober);

        Assert.Equal(70m, report.Uncategorized);
        Assert.DoesNotContain(report.Lines, l => l.Category == SpendingReport.Uncategorized);
    }

    [Fact]
    public void Totals_only_count_categories_with_a_limit()
    {
        var report = Budgeting.Build(
            [Month(2026, 10, ("Food", 300m), ("Pet", 40m))],
            new Dictionary<string, decimal> { ["Food"] = 800m }, 2026, 10, LateOctober);

        Assert.Equal(800m, report.TotalLimit);
        Assert.Equal(300m, report.SpentInBudgeted);
    }

    [Fact]
    public void Suggestion_is_the_average_of_up_to_three_earlier_months_rounded_up_to_ten()
    {
        var months = new[]
        {
            Month(2026, 6, ("Food", 1000m)),      // too old: only 3 months are used
            Month(2026, 7, ("Food", 301m)),
            Month(2026, 8, ("Food", 300m)),
            Month(2026, 9, ("Food", 300m)),
            Month(2026, 10, ("Food", 999m)),      // the month itself is not history
        };

        Assert.Equal(310m, Budgeting.Suggest(months, "Food", 2026, 10));
    }

    [Fact]
    public void No_suggestion_without_history_or_without_spending_in_the_category()
    {
        var months = new[] { Month(2026, 9, ("Food", 300m)), Month(2026, 10, ("Food", 100m)) };

        Assert.Null(Budgeting.Suggest(months, "Pet", 2026, 10));
        Assert.Null(Budgeting.Suggest(months, "Food", 2026, 9));
    }

    [Fact]
    public void Projection_extrapolates_the_pace_in_the_running_month_only()
    {
        var months = new[] { Month(2026, 10, ("Food", 300m)) };
        var limits = new Dictionary<string, decimal> { ["Food"] = 800m };

        var running = Budgeting.Build(months, limits, 2026, 10, new DateOnly(2026, 10, 10));
        var line = running.Lines.Single();
        Assert.Equal(930m, line.Projected);   // 300 / 10 days * 31 days
        Assert.True(line.ProjectedOver);

        var closed = Budgeting.Build(months, limits, 2026, 10, new DateOnly(2026, 11, 3));
        Assert.Null(closed.Lines.Single().Projected);
    }

    [Fact]
    public void No_projection_in_the_first_days_of_the_month()
    {
        var report = Budgeting.Build(
            [Month(2026, 10, ("Food", 300m))],
            new Dictionary<string, decimal> { ["Food"] = 800m }, 2026, 10, new DateOnly(2026, 10, 3));

        Assert.Null(report.Lines.Single().Projected);
    }
}
