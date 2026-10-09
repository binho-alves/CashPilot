using CashPilot.Domain.Payments;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class RecurringEntriesTests
{
    private static readonly DateOnly Today = new(2026, 10, 8);

    private static Transaction Tx(string date, decimal amount, string description = "Rent flat", string account = "Bank A",
        TransactionType? type = null) => new()
    {
        Account = account,
        Date = DateOnly.Parse(date),
        Amount = amount,
        RawDescription = description,
        Type = type ?? (amount < 0 ? TransactionType.Expense : TransactionType.Income),
    };

    [Fact]
    public void Monthly_expense_on_a_stable_day_is_found()
    {
        var found = RecurringEntries.Detect(
            [Tx("2026-07-05", -1500m), Tx("2026-08-05", -1500m), Tx("2026-09-06", -1500m)], Today);

        var rent = Assert.Single(found);
        Assert.Equal(-1500m, rent.Amount);
        Assert.Equal(5, rent.DayOfMonth);
        Assert.Equal(3, rent.Months);
        Assert.False(rent.IsIncome);
    }

    [Fact]
    public void Monthly_income_is_found_too()
    {
        var found = RecurringEntries.Detect(
            [Tx("2026-07-05", 5000m, "Salary Co"), Tx("2026-08-05", 5000m, "Salary Co"), Tx("2026-09-05", 5000m, "Salary Co")], Today);

        Assert.True(Assert.Single(found).IsIncome);
    }

    [Fact]
    public void Two_months_are_not_enough()
    {
        Assert.Empty(RecurringEntries.Detect([Tx("2026-08-05", -90m, "Streaming"), Tx("2026-09-05", -90m, "Streaming")], Today));
    }

    [Fact]
    public void Wildly_different_amounts_are_not_recurring()
    {
        Assert.Empty(RecurringEntries.Detect(
            [Tx("2026-07-05", -50m, "Market"), Tx("2026-08-05", -300m, "Market"), Tx("2026-09-05", -120m, "Market")], Today));
    }

    [Fact]
    public void Slightly_different_amounts_still_count_and_the_expected_one_is_the_recent_median()
    {
        var found = RecurringEntries.Detect(
            [Tx("2026-07-05", -100m, "Power co"), Tx("2026-08-05", -110m, "Power co"), Tx("2026-09-05", -105m, "Power co")], Today);

        Assert.Equal(-105m, Assert.Single(found).Amount);
    }

    [Fact]
    public void Scattered_days_are_not_recurring()
    {
        Assert.Empty(RecurringEntries.Detect(
            [Tx("2026-07-02", -60m, "Pharmacy"), Tx("2026-08-20", -60m, "Pharmacy"), Tx("2026-09-11", -60m, "Pharmacy")], Today));
    }

    [Fact]
    public void Two_entries_in_a_month_disqualify_it()
    {
        Assert.Empty(RecurringEntries.Detect(
        [
            Tx("2026-07-05", -30m, "Taxi"), Tx("2026-08-05", -30m, "Taxi"),
            Tx("2026-09-05", -30m, "Taxi"), Tx("2026-09-06", -30m, "Taxi"),
        ], Today));
    }

    [Fact]
    public void Something_that_stopped_long_ago_is_not_recurring()
    {
        Assert.Empty(RecurringEntries.Detect(
            [Tx("2026-04-05", -90m, "Old gym"), Tx("2026-05-05", -90m, "Old gym"), Tx("2026-06-05", -90m, "Old gym")], Today));
    }

    [Fact]
    public void Installments_transfers_and_card_bill_payments_are_ignored()
    {
        var installment = Tx("2026-07-05", -100m, "Shop PARC 01/03") with { InstallmentNumber = 1, InstallmentCount = 3 };
        var found = RecurringEntries.Detect(
        [
            installment,
            installment with { Date = new DateOnly(2026, 8, 5), InstallmentNumber = 2 },
            installment with { Date = new DateOnly(2026, 9, 5), InstallmentNumber = 3 },
            Tx("2026-07-06", -500m, "Own account", type: TransactionType.InternalTransfer),
            Tx("2026-08-06", -500m, "Own account", type: TransactionType.InternalTransfer),
            Tx("2026-09-06", -500m, "Own account", type: TransactionType.InternalTransfer),
            Tx("2026-07-07", -900m, "Card bill", type: TransactionType.CardBillPayment),
            Tx("2026-08-07", -900m, "Card bill", type: TransactionType.CardBillPayment),
            Tx("2026-09-07", -900m, "Card bill", type: TransactionType.CardBillPayment),
        ], Today);

        Assert.Empty(found);
    }

    [Fact]
    public void Same_description_on_two_accounts_is_two_entries()
    {
        var found = RecurringEntries.Detect(
        [
            Tx("2026-07-05", -40m, "Cloud", "Bank A"), Tx("2026-08-05", -40m, "Cloud", "Bank A"), Tx("2026-09-05", -40m, "Cloud", "Bank A"),
            Tx("2026-07-05", -40m, "Cloud", "Card B"), Tx("2026-08-05", -40m, "Cloud", "Card B"), Tx("2026-09-05", -40m, "Cloud", "Card B"),
        ], Today);

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Occurrences_start_after_the_last_entry_and_clamp_short_months()
    {
        var entry = new RecurringEntry("Bank A", "Rent flat", -1500m, 31, new DateOnly(2026, 12, 31), 3);

        var dates = RecurringEntries.Occurrences(entry, new DateOnly(2027, 1, 10), new DateOnly(2027, 3, 31)).ToList();

        Assert.Equal([new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31)], dates);
    }

    [Fact]
    public void An_occurrence_already_in_the_past_is_not_predicted()
    {
        // Last seen in September; the October date (the 5th) already went by without an entry: do not invent it.
        var entry = new RecurringEntry("Bank A", "Rent flat", -1500m, 5, new DateOnly(2026, 9, 5), 3);

        var dates = RecurringEntries.Occurrences(entry, Today, new DateOnly(2026, 12, 31)).ToList();

        Assert.Equal([new DateOnly(2026, 11, 5), new DateOnly(2026, 12, 5)], dates);
    }

    [Fact]
    public void A_counter_in_the_description_limits_the_projection_to_the_installments_left()
    {
        var found = RecurringEntries.Detect(
        [
            Tx("2026-07-24", -165.34m, "Shop Campinas 06/12"),
            Tx("2026-08-24", -165.34m, "Shop Campinas 07/12"),
            Tx("2026-09-24", -165.34m, "Shop Campinas 08/12"),
        ], Today);

        var entry = Assert.Single(found);
        Assert.Equal(4, entry.Remaining);
        var dates = RecurringEntries.Occurrences(entry, Today, new DateOnly(2027, 12, 31)).ToList();
        Assert.Equal(4, dates.Count);
        Assert.Equal(new DateOnly(2027, 1, 24), dates[^1]);
    }

    [Fact]
    public void A_finished_counter_is_not_projected_at_all()
    {
        Assert.Empty(RecurringEntries.Detect(
        [
            Tx("2026-07-24", -90m, "Shop Campinas 10/12"),
            Tx("2026-08-24", -90m, "Shop Campinas 11/12"),
            Tx("2026-09-24", -90m, "Shop Campinas 12/12"),
        ], Today));
    }

    [Fact]
    public void A_repeated_date_in_the_description_is_not_taken_for_a_counter()
    {
        var found = RecurringEntries.Detect(
        [
            Tx("2026-07-05", -80m, "Club fee ref 05/12"),
            Tx("2026-08-05", -80m, "Club fee ref 05/12"),
            Tx("2026-09-05", -80m, "Club fee ref 05/12"),
        ], Today);

        Assert.Null(Assert.Single(found).Remaining);
    }
}
