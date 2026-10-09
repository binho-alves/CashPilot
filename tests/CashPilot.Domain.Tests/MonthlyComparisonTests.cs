using CashPilot.Domain.Reports;

namespace CashPilot.Domain.Tests;

public class MonthlyComparisonTests
{
    private static MonthlySpending Month(int year, int month, params (string Category, decimal Total)[] lines) =>
        new(year, month, lines.Select(l => new SpendingLine(l.Category, "", l.Total, 1)).ToList());

    [Fact]
    public void Rows_show_the_change_against_the_previous_month_biggest_rise_first()
    {
        var comparison = MonthlyComparison.Compare(
        [
            Month(2026, 9, ("Food", 500m), ("Transport", 200m), ("Leisure", 300m)),
            Month(2026, 10, ("Food", 700m), ("Transport", 180m), ("Leisure", 300m)),
        ], 2026, 10);

        Assert.Equal(1180m, comparison.CurrentTotal);
        Assert.Equal(1000m, comparison.PreviousTotal);
        Assert.Equal("Food", comparison.Rows[0].Category);
        Assert.Equal(200m, comparison.Rows[0].Delta);
        Assert.Equal(0.4m, comparison.Rows[0].DeltaFraction);
        Assert.Equal("Transport", comparison.Rows[^1].Category);
    }

    [Fact]
    public void A_category_that_did_not_exist_before_is_new_and_a_big_rise()
    {
        var comparison = MonthlyComparison.Compare(
        [
            Month(2026, 9, ("Food", 500m)),
            Month(2026, 10, ("Food", 500m), ("Pet", 120m)),
        ], 2026, 10);

        var pet = comparison.Rows.Single(r => r.Category == "Pet");
        Assert.True(pet.IsNew);
        Assert.True(pet.IsBigRise);
        Assert.Null(pet.DeltaFraction);
    }

    [Fact]
    public void Small_rises_are_not_flagged()
    {
        var comparison = MonthlyComparison.Compare(
        [
            Month(2026, 9, ("Food", 1000m), ("Gym", 40m)),
            Month(2026, 10, ("Food", 1100m), ("Gym", 70m)), // +10% on Food; Gym +75% but only R$ 30
        ], 2026, 10);

        Assert.All(comparison.Rows, r => Assert.False(r.IsBigRise));
    }

    [Fact]
    public void A_category_that_stopped_shows_as_a_fall()
    {
        var comparison = MonthlyComparison.Compare(
        [
            Month(2026, 9, ("Food", 500m), ("Course", 300m)),
            Month(2026, 10, ("Food", 500m)),
        ], 2026, 10);

        var course = comparison.Rows.Single(r => r.Category == "Course");
        Assert.Equal(-300m, course.Delta);
        Assert.Equal(0m, course.Current);
    }

    [Fact]
    public void Recent_average_uses_up_to_three_earlier_months_that_have_data()
    {
        var comparison = MonthlyComparison.Compare(
        [
            Month(2026, 7, ("Food", 300m)),
            Month(2026, 9, ("Food", 600m)),
            Month(2026, 10, ("Food", 500m)),
        ], 2026, 10);

        // August has no data at all and is not counted as zero: (300 + 600) / 2.
        Assert.Equal(450m, comparison.Rows.Single().RecentAverage);
    }

    [Fact]
    public void Comparison_crosses_the_year_boundary()
    {
        var comparison = MonthlyComparison.Compare(
        [
            Month(2025, 12, ("Food", 400m)),
            Month(2026, 1, ("Food", 500m)),
        ], 2026, 1);

        Assert.Equal(100m, comparison.Rows.Single().Delta);
    }

    [Fact]
    public void Month_without_data_gives_empty_current_and_no_average_when_nothing_earlier()
    {
        var comparison = MonthlyComparison.Compare([Month(2026, 10, ("Food", 100m))], 2026, 10);

        Assert.Null(comparison.Rows.Single().RecentAverage);
        Assert.Equal(0m, comparison.PreviousTotal);
    }
}
