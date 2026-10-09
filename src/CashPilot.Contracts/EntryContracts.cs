namespace CashPilot.Contracts;

/// <summary>The kinds of entry the mobile app can create.</summary>
public static class EntryKinds
{
    public const string Expense = "expense";
    public const string Income = "income";
    public const string BillPayment = "billPayment";
    public const string TransferOut = "transferOut";
    public const string TransferIn = "transferIn";

    public static readonly IReadOnlyList<string> All =
        [Expense, Income, BillPayment, TransferOut, TransferIn];

    public static bool IsValid(string? kind) => kind is not null && All.Contains(kind);
}

/// <summary>
/// A new entry typed on the phone. <see cref="ClientId"/> is created on the phone when the entry is typed, so sending
/// it twice (a timeout, a retry) never creates two entries.
/// </summary>
/// <param name="Amount">Always positive; <paramref name="Kind"/> decides the direction.</param>
public sealed record NewEntryRequest(
    Guid ClientId,
    DateOnly Date,
    string Account,
    string Description,
    decimal Amount,
    string Kind,
    string? Category = null,
    string? Item = null);

public sealed record NewEntryResponse(Guid Id, string? Category, string? Item, bool AutoClassified, bool AlreadyExisted);

/// <summary>An entry typed by hand (from the phone or the web page), as the phone lists it.</summary>
public sealed record EntryDto(
    Guid Id,
    DateOnly Date,
    string Account,
    string Description,
    decimal Amount,
    string Kind,
    string? Category,
    string? Item);

public sealed record AccountDto(string Name, string Kind);

public sealed record CategoryDto(string Category, string Item);

/// <summary>The pick lists the phone caches so it can type entries while offline.</summary>
public sealed record LookupsDto(IReadOnlyList<AccountDto> Accounts, IReadOnlyList<CategoryDto> Categories);

public sealed record ApiError(string Error);
