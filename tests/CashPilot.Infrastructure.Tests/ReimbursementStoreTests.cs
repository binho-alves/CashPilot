using CashPilot.Domain.Reimbursements;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class ReimbursementStoreTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    private static Transaction Add(CashPilotStore store, decimal amount, string description, string date = "2026-09-01",
        string? category = "Health", string? item = "Therapy")
    {
        var transaction = new Transaction
        {
            Account = "Bank A", Date = DateOnly.Parse(date), Amount = amount, RawDescription = description,
            Category = category, Item = item,
            Type = amount < 0 ? TransactionType.Expense : TransactionType.Income,
        };
        Assert.True(store.TryInsert(transaction, transaction.ComputeDedupKey()));
        return transaction;
    }

    [Fact]
    public void A_claim_expects_payment_thirty_days_after_the_request()
    {
        using var store = new CashPilotStore(":memory:");
        var expense = Add(store, -200m, "Dr. Silva");

        Assert.True(store.AddReimbursementClaim(expense.Id, new DateOnly(2026, 9, 3)));
        Assert.False(store.AddReimbursementClaim(expense.Id, new DateOnly(2026, 9, 4)));   // already claimed

        var claim = Assert.Single(store.GetReimbursementClaims());
        Assert.Equal(new DateOnly(2026, 10, 3), claim.ExpectedOn);
        Assert.Equal(200m, claim.Paid);
        Assert.Equal(ClaimStatus.Requested, claim.Status);
    }

    [Fact]
    public void Only_an_existing_expense_can_be_claimed()
    {
        using var store = new CashPilotStore(":memory:");
        var income = Add(store, 500m, "Salary");

        Assert.Throws<ArgumentException>(() => store.AddReimbursementClaim(income.Id, Today));
        Assert.Throws<ArgumentException>(() => store.AddReimbursementClaim(Guid.NewGuid(), Today));
    }

    [Fact]
    public void One_pix_can_pay_several_claims_and_takes_their_category()
    {
        using var store = new CashPilotStore(":memory:");
        var a = Add(store, -100m, "Therapy A", "2026-09-01");
        var b = Add(store, -150m, "Therapy B", "2026-09-05");
        store.AddReimbursementClaim(a.Id, new DateOnly(2026, 9, 2));
        store.AddReimbursementClaim(b.Id, new DateOnly(2026, 9, 6));
        var pix = Add(store, 250m, "PIX Health Plan", "2026-10-08", category: null, item: null);

        store.LinkPix(pix.Id, [new Allocation(a.Id, 100m), new Allocation(b.Id, 150m)]);

        var claims = store.GetReimbursementClaims();
        Assert.All(claims, c => Assert.True(c.Settled));
        Assert.Empty(store.GetPending());   // the Pix got a category
        var linked = store.GetAll().Single(t => t.Id == pix.Id);
        Assert.Equal("Health", linked.Category);
        Assert.Equal("Therapy", linked.Item);
    }

    [Fact]
    public void A_pix_that_already_has_a_category_keeps_it()
    {
        using var store = new CashPilotStore(":memory:");
        var expense = Add(store, -100m, "Therapy A");
        store.AddReimbursementClaim(expense.Id, Today);
        var pix = Add(store, 100m, "PIX Health Plan", category: "Other", item: null);

        store.LinkPix(pix.Id, [new Allocation(expense.Id, 100m)]);

        Assert.Equal("Other", store.GetAll().Single(t => t.Id == pix.Id).Category);
    }

    [Fact]
    public void A_partial_payment_leaves_a_difference_that_becomes_final_when_closed()
    {
        using var store = new CashPilotStore(":memory:");
        var expense = Add(store, -200m, "Therapy A");
        store.AddReimbursementClaim(expense.Id, new DateOnly(2026, 9, 2));
        var pix = Add(store, 120m, "PIX Health Plan", category: null);

        store.LinkPix(pix.Id, [new Allocation(expense.Id, 120m)]);

        var open = Assert.Single(store.GetReimbursementClaims());
        Assert.True(open.IsOpen);
        Assert.Equal(80m, open.Remaining);

        Assert.True(store.CloseReimbursementClaim(expense.Id));
        var closed = Assert.Single(store.GetReimbursementClaims());
        Assert.False(closed.IsOpen);
        Assert.Equal(80m, closed.Difference);

        Assert.True(store.ReopenReimbursementClaim(expense.Id));
        Assert.True(Assert.Single(store.GetReimbursementClaims()).IsOpen);
    }

    [Fact]
    public void Linking_more_than_the_pix_or_the_claim_is_refused()
    {
        using var store = new CashPilotStore(":memory:");
        var a = Add(store, -100m, "Therapy A");
        var b = Add(store, -100m, "Therapy B");
        store.AddReimbursementClaim(a.Id, Today);
        store.AddReimbursementClaim(b.Id, Today);
        var pix = Add(store, 150m, "PIX Health Plan", category: null);

        Assert.Throws<ArgumentException>(() => store.LinkPix(pix.Id, [new Allocation(a.Id, 100m), new Allocation(b.Id, 100m)])); // 200 > 150
        Assert.Throws<ArgumentException>(() => store.LinkPix(pix.Id, [new Allocation(a.Id, 120m)]));                              // 120 > claim
        Assert.Throws<ArgumentException>(() => store.LinkPix(pix.Id, [new Allocation(a.Id, 0m)]));
        Assert.Throws<ArgumentException>(() => store.LinkPix(pix.Id, []));
        Assert.All(store.GetReimbursementClaims(), c => Assert.Equal(0m, c.Received));
    }

    [Fact]
    public void What_is_already_linked_counts_against_the_pix()
    {
        using var store = new CashPilotStore(":memory:");
        var a = Add(store, -100m, "Therapy A");
        var b = Add(store, -100m, "Therapy B");
        store.AddReimbursementClaim(a.Id, Today);
        store.AddReimbursementClaim(b.Id, Today);
        var pix = Add(store, 150m, "PIX Health Plan", category: null);

        store.LinkPix(pix.Id, [new Allocation(a.Id, 100m)]);

        store.LinkPix(pix.Id, [new Allocation(b.Id, 50m)]);                                      // exactly what was left
        Assert.Throws<ArgumentException>(() => store.LinkPix(pix.Id, [new Allocation(b.Id, 1m)]));
    }

    [Fact]
    public void The_entry_must_be_an_inflow()
    {
        using var store = new CashPilotStore(":memory:");
        var a = Add(store, -100m, "Therapy A");
        var b = Add(store, -50m, "Other expense");
        store.AddReimbursementClaim(a.Id, Today);

        Assert.Throws<ArgumentException>(() => store.LinkPix(b.Id, [new Allocation(a.Id, 50m)]));
    }

    [Fact]
    public void Unlinking_and_removing_a_claim_undo_the_money()
    {
        using var store = new CashPilotStore(":memory:");
        var expense = Add(store, -100m, "Therapy A");
        store.AddReimbursementClaim(expense.Id, Today);
        var pix = Add(store, 100m, "PIX Health Plan", category: null);
        store.LinkPix(pix.Id, [new Allocation(expense.Id, 100m)]);

        Assert.True(store.UnlinkPix(expense.Id, pix.Id));
        Assert.Equal(0m, Assert.Single(store.GetReimbursementClaims()).Received);

        store.LinkPix(pix.Id, [new Allocation(expense.Id, 100m)]);
        Assert.True(store.RemoveReimbursementClaim(expense.Id));
        Assert.Empty(store.GetReimbursementClaims());
        Assert.Equal(2, store.Count());   // the expense and the Pix stay
    }

    [Fact]
    public void Asking_for_more_documents_restarts_the_wait()
    {
        using var store = new CashPilotStore(":memory:");
        var expense = Add(store, -100m, "Therapy A");
        store.AddReimbursementClaim(expense.Id, new DateOnly(2026, 9, 1));

        Assert.True(store.RequestMoreDocuments(expense.Id, Today, "send the receipt"));

        var claim = Assert.Single(store.GetReimbursementClaims());
        Assert.Equal(ClaimStatus.AwaitingDocuments, claim.Status);
        Assert.Equal(new DateOnly(2026, 11, 8), claim.ExpectedOn);
        Assert.Equal("send the receipt", claim.Note);
    }

    [Fact]
    public void A_deleted_expense_or_pix_drops_out()
    {
        using var store = new CashPilotStore(":memory:");
        var a = Add(store, -100m, "Therapy A");
        var b = Add(store, -100m, "Therapy B");
        store.AddReimbursementClaim(a.Id, Today);
        store.AddReimbursementClaim(b.Id, Today);
        var pix = Add(store, 100m, "PIX Health Plan", category: null);
        store.LinkPix(pix.Id, [new Allocation(a.Id, 100m)]);

        store.DeleteTransaction(pix.Id);
        store.DeleteTransaction(b.Id);

        var claim = Assert.Single(store.GetReimbursementClaims());
        Assert.Equal(a.Id, claim.Id);
        Assert.Equal(0m, claim.Received);
    }
}
