using CashPilot.Domain.Transactions;

namespace CashPilot.Domain.Tests;

public class TransferDetectorTests
{
    private static Transaction Tx(string account, int day, decimal amount, string description = "PIX") =>
        new() { Account = account, Date = new DateOnly(2026, 9, day), Amount = amount, RawDescription = description };

    [Fact]
    public void PairsAcrossDifferentAccountsAreTransfers()
    {
        var outgoing = Tx("Itaú", 1, -1000m);
        var incoming = Tx("Inter", 2, 1000m);

        var pairs = TransferDetector.Detect(new[] { outgoing, incoming });

        var pair = Assert.Single(pairs);
        Assert.Equal(outgoing.Id, pair.OutgoingId);
        Assert.Equal(incoming.Id, pair.IncomingId);
    }

    [Fact]
    public void SameAccountDoesNotPair()
    {
        var pairs = TransferDetector.Detect(new[] { Tx("Itaú", 1, -50m), Tx("Itaú", 1, 50m) });
        Assert.Empty(pairs);
    }

    [Fact]
    public void OutsideTheWindowDoesNotPair()
    {
        var pairs = TransferDetector.Detect(new[] { Tx("Itaú", 1, -50m), Tx("Inter", 10, 50m) }, windowDays: 3);
        Assert.Empty(pairs);
    }

    [Fact]
    public void EachIncomingPairsOnlyOnce()
    {
        var incoming = Tx("Inter", 2, 100m);
        var pairs = TransferDetector.Detect(new[] { Tx("Itaú", 1, -100m), Tx("Bradesco", 1, -100m), incoming });
        Assert.Single(pairs);
    }
}
