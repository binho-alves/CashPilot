using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Tests;

public class BudgetStoreTests
{
    [Fact]
    public void Limit_is_saved_replaced_and_removed()
    {
        using var store = new CashPilotStore(":memory:");

        store.SetBudget("Food", 800m);
        store.SetBudget("Food", 850.50m);
        store.SetBudget("Leisure", 200m);

        var budgets = store.GetBudgets();
        Assert.Equal(2, budgets.Count);
        Assert.Equal(850.50m, budgets["Food"]);

        Assert.True(store.RemoveBudget("Leisure"));
        Assert.False(store.RemoveBudget("Leisure"));
        Assert.Single(store.GetBudgets());
    }

    [Fact]
    public void Limit_must_be_positive_and_category_present()
    {
        using var store = new CashPilotStore(":memory:");

        Assert.Throws<ArgumentException>(() => store.SetBudget("Food", 0m));
        Assert.Throws<ArgumentException>(() => store.SetBudget("  ", 10m));
    }

    [Fact]
    public void Renaming_a_category_moves_its_limit()
    {
        using var store = new CashPilotStore(":memory:");
        store.SetBudget("Eating out", 300m);

        store.RenameCategory("Eating out", "Food");

        var budgets = store.GetBudgets();
        Assert.Equal(300m, budgets["Food"]);
        Assert.DoesNotContain("Eating out", budgets.Keys);
    }

    [Fact]
    public void Merging_into_a_category_that_has_a_limit_keeps_the_target_limit()
    {
        using var store = new CashPilotStore(":memory:");
        store.SetBudget("Eating out", 300m);
        store.SetBudget("Food", 800m);

        store.RenameCategory("Eating out", "Food");

        var budgets = store.GetBudgets();
        Assert.Single(budgets);
        Assert.Equal(800m, budgets["Food"]);
    }
}
