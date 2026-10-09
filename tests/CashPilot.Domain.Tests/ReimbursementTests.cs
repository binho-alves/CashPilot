using CashPilot.Domain.Accounts;
using CashPilot.Domain.Payments;
using CashPilot.Domain.Reimbursements;
using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class ReimbursementTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static ReimbursementClaim Claim(decimal paid, string requested = "2026-09-01", ClaimStatus status = ClaimStatus.Requested,
        decimal received = 0m, string description = "Therapy session")
    {
        var expense = new Transaction
        {
            Account = "Bank A", Date = DateOnly.Parse(requested), Amount = -paid, RawDescription = description,
            Category = "Health", Type = TransactionType.Expense,
        };
        var requestedOn = DateOnly.Parse(requested);
        var payments = received > 0 ? new[] { new ReimbursementPayment(expense.Id, Guid.NewGuid(), received) } : Array.Empty<ReimbursementPayment>();
        return new ReimbursementClaim(expense, requestedOn, requestedOn.AddDays(30), status, null, payments);
    }

    [Fact]
    public void A_claim_is_open_until_it_is_paid_in_full_or_closed()
    {
        Assert.True(Claim(200m).IsOpen);
        Assert.True(Claim(200m, received: 120m).IsOpen);
        Assert.False(Claim(200m, received: 200m).IsOpen);
        Assert.True(Claim(200m, received: 200m).Settled);
        Assert.False(Claim(200m, received: 120m, status: ClaimStatus.Closed).IsOpen);
    }

    [Fact]
    public void Difference_is_what_it_really_cost()
    {
        var partial = Claim(200m, received: 120m, status: ClaimStatus.Closed);

        Assert.Equal(80m, partial.Difference);
        Assert.Equal(80m, partial.Remaining);
        Assert.Equal(200m, partial.Paid);
        Assert.Equal(0m, Claim(200m, received: 200m).Difference);
    }

    [Fact]
    public void Late_means_open_and_past_the_expected_date()
    {
        var claim = Claim(200m, requested: "2026-09-01");   // expected 2026-10-01

        Assert.True(claim.IsLate(Today));
        Assert.False(claim.IsLate(new DateOnly(2026, 9, 30)));
        Assert.False(Claim(200m, requested: "2026-09-01", received: 200m).IsLate(Today));
        Assert.Equal(38, claim.DaysWaiting(Today));
    }

    [Fact]
    public void Totals_split_what_is_waiting_from_what_is_lost()
    {
        var totals = ReimbursementRules.Summarize(
        [
            Claim(200m, requested: "2026-09-01"),                                              // late, waiting 200
            Claim(150m, requested: "2026-10-05", received: 50m),                              // open, waiting 100
            Claim(300m, received: 300m),                                                       // settled, cost 0
            Claim(100m, received: 60m, status: ClaimStatus.Closed),                           // closed, cost 40
            Claim(80m, status: ClaimStatus.Closed),                                           // denied, cost 80
        ], Today);

        Assert.Equal(2, totals.OpenCount);
        Assert.Equal(1, totals.LateCount);
        Assert.Equal(300m, totals.Waiting);
        Assert.Equal(120m, totals.FinalDifference);
        Assert.Equal(830m, totals.Paid);
        Assert.Equal(410m, totals.Received);
    }

    [Fact]
    public void Suggestion_finds_the_claims_that_add_up_to_the_pix()
    {
        var a = Claim(100m, requested: "2026-09-01");
        var b = Claim(150m, requested: "2026-09-05");
        var c = Claim(80m, requested: "2026-09-10");
        var unrelated = Claim(999m, requested: "2026-09-02");

        var suggestion = ReimbursementRules.Suggest(230m, [a, b, c, unrelated]);

        Assert.True(suggestion.Exact);
        Assert.Equal(new[] { b.Id, c.Id }.Order(), suggestion.Items.Select(i => i.ClaimId).Order());
        Assert.Equal(230m, suggestion.Items.Sum(i => i.Amount));
    }

    [Fact]
    public void Suggestion_prefers_fewer_and_older_claims()
    {
        var old = Claim(100m, requested: "2026-08-01");
        var newer = Claim(100m, requested: "2026-09-01");

        var suggestion = ReimbursementRules.Suggest(100m, [newer, old]);

        Assert.True(suggestion.Exact);
        Assert.Equal(old.Id, Assert.Single(suggestion.Items).ClaimId);
    }

    [Fact]
    public void Without_an_exact_match_the_oldest_claims_are_filled_in_order()
    {
        var a = Claim(100m, requested: "2026-09-01");
        var b = Claim(150m, requested: "2026-09-05");

        var suggestion = ReimbursementRules.Suggest(180m, [b, a]);   // the plan paid less than claimed

        Assert.False(suggestion.Exact);
        Assert.Equal(100m, suggestion.Items[0].Amount);
        Assert.Equal(a.Id, suggestion.Items[0].ClaimId);
        Assert.Equal(80m, suggestion.Items[1].Amount);
        Assert.Equal(b.Id, suggestion.Items[1].ClaimId);
    }

    [Fact]
    public void Suggestion_uses_what_is_left_of_a_part_paid_claim()
    {
        var a = Claim(200m, requested: "2026-09-01", received: 120m);   // 80 to go

        var suggestion = ReimbursementRules.Suggest(80m, [a]);

        Assert.True(suggestion.Exact);
        Assert.Equal(80m, Assert.Single(suggestion.Items).Amount);
    }

    [Fact]
    public void Suggestion_ignores_closed_and_settled_claims()
    {
        var suggestion = ReimbursementRules.Suggest(100m,
            [Claim(100m, status: ClaimStatus.Closed), Claim(100m, received: 100m)]);

        Assert.Empty(suggestion.Items);
        Assert.False(suggestion.Exact);
    }

    [Fact]
    public void Forecast_expects_the_money_on_the_expected_date_or_tomorrow_when_late()
    {
        var onTime = Claim(200m, requested: "2026-09-25");                  // expected 2026-10-25
        var late = Claim(150m, requested: "2026-08-01", description: "Late one");   // expected 2026-08-31
        var done = Claim(300m, received: 300m);

        var items = CashForecast.Build(
            [new Account { Name = "Bank A", Kind = AccountKind.BankAccount }], [], [], Today, Today.AddDays(90),
            reimbursements: [onTime, late, done]);

        var expected = items.Where(i => i.Kind == UpcomingKind.ExpectedReimbursement).OrderBy(i => i.DueDate).ToList();
        Assert.Equal(2, expected.Count);
        Assert.Equal(Today.AddDays(1), expected[0].DueDate);
        Assert.Equal(150m, expected[0].Amount);
        Assert.Equal(new DateOnly(2026, 10, 25), expected[1].DueDate);
    }

    [Fact]
    public void Forecast_counts_the_expected_reimbursement_as_an_inflow()
    {
        var items = new[]
        {
            new UpcomingPayment(UpcomingKind.ExpectedReimbursement, "Reembolso", new DateOnly(2026, 10, 20), 200m, null, null, false),
        };

        var summary = CashForecast.Project(100m, items, 0m, Today, 30);

        Assert.Equal(300m, summary.Days.Single().Balance);
        Assert.Equal(200m, summary.Days.Single().Inflow);
    }

    [Fact]
    public void Forecast_expects_only_what_is_still_to_receive()
    {
        var items = CashForecast.Build(
            [new Account { Name = "Bank A", Kind = AccountKind.BankAccount }], [], [], Today, Today.AddDays(90),
            reimbursements: [Claim(200m, requested: "2026-09-25", received: 120m)]);

        Assert.Equal(80m, items.Single(i => i.Kind == UpcomingKind.ExpectedReimbursement).Amount);
    }

    [Fact]
    public void Months_group_by_the_month_of_the_expense_newest_first()
    {
        var months = ReimbursementRules.ByMonth(
        [
            Claim(200m, requested: "2026-09-01", received: 200m),                          // settled
            Claim(100m, requested: "2026-09-10", received: 60m, status: ClaimStatus.Closed), // closed, lost 40
            Claim(150m, requested: "2026-09-20", received: 50m),                           // open, waiting 100
            Claim(80m, requested: "2026-10-02"),                                           // open, waiting 80
        ]);

        Assert.Equal(2, months.Count);
        Assert.Equal((2026, 10), (months[0].Year, months[0].Month));
        Assert.Equal(80m, months[0].Waiting);

        var september = months[1];
        Assert.Equal(3, september.Claims);
        Assert.Equal(450m, september.Paid);
        Assert.Equal(310m, september.Received);
        Assert.Equal(100m, september.Waiting);
        Assert.Equal(40m, september.Lost);
        Assert.Equal(140m, september.NetCost);
        Assert.Equal(310m / 450m, september.ReimbursedFraction);
    }

    [Fact]
    public void Months_without_claims_give_an_empty_list()
    {
        Assert.Empty(ReimbursementRules.ByMonth([]));
    }
}
