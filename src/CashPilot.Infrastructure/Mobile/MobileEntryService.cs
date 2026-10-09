using CashPilot.Contracts;
using CashPilot.Domain.Accounts;
using CashPilot.Domain.Descriptions;
using CashPilot.Domain.Transactions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Mobile;

/// <summary>A request the phone sent that cannot be accepted (the message is shown to the user as is).</summary>
public sealed class MobileRequestException(string message) : Exception(message);

/// <summary>
/// What the phone app does with the database: type an entry, list the last ones typed by hand, undo one, and read the
/// pick lists. Sits on top of <see cref="ManualEntries"/>, so the rules are the same as on the Lançamentos page.
/// </summary>
public static class MobileEntryService
{
    public const int MaxDescriptionLength = 200;

    /// <summary>Adds the entry once: the same <see cref="NewEntryRequest.ClientId"/> sent again changes nothing.</summary>
    public static NewEntryResponse Create(CashPilotStore store, NewEntryRequest request)
    {
        if (request.ClientId == Guid.Empty) throw new MobileRequestException("Falta o identificador do lançamento.");
        if (!EntryKinds.IsValid(request.Kind)) throw new MobileRequestException("Tipo de lançamento desconhecido.");
        if (request.Amount <= 0) throw new MobileRequestException("O valor deve ser maior que zero.");
        if (decimal.Round(request.Amount, 2) != request.Amount) throw new MobileRequestException("O valor tem no máximo 2 casas decimais.");
        var description = (request.Description ?? "").Trim();
        if (description.Length == 0) throw new MobileRequestException("Informe a descrição.");
        if (description.Length > MaxDescriptionLength) throw new MobileRequestException("A descrição é longa demais.");

        var account = ResolveAccount(store, request.Account);
        var income = request.Kind is EntryKinds.Income or EntryKinds.TransferIn;
        TransactionType? type = request.Kind switch
        {
            EntryKinds.BillPayment => TransactionType.CardBillPayment,
            EntryKinds.TransferOut or EntryKinds.TransferIn => TransactionType.InternalTransfer,
            _ => null,
        };

        Classification? chosen = string.IsNullOrWhiteSpace(request.Category)
            ? null
            : new Classification(request.Category.Trim(), request.Item?.Trim() ?? "");

        ManualEntryResult result;
        try
        {
            result = ManualEntries.Add(store, request.Date, account, description, request.Amount, income, chosen,
                learn: true, type, request.ClientId);
        }
        catch (ArgumentException ex)
        {
            throw new MobileRequestException(ex.Message);
        }

        // A retry: report what is stored, not what this request would have produced.
        var saved = result.AlreadyExisted
            ? store.GetAll().FirstOrDefault(t => t.Id == request.ClientId) ?? result.Transaction
            : result.Transaction;
        return new NewEntryResponse(saved.Id, saved.Category, saved.Item, result.AutoClassified, result.AlreadyExisted);
    }

    /// <summary>The entries typed by hand, newest first.</summary>
    public static IReadOnlyList<EntryDto> Recent(CashPilotStore store, int limit = 30) =>
        store.GetRecentManual(limit).Select(ToDto).ToList();

    /// <summary>Removes an entry typed by hand. Imported entries are refused, so a tap on the phone cannot erase history.</summary>
    public static bool DeleteManual(CashPilotStore store, Guid id) =>
        store.IsManualEntry(id) && store.DeleteTransaction(id);

    /// <summary>Accounts and categories for the pick lists.</summary>
    public static LookupsDto Lookups(CashPilotStore store)
    {
        var accounts = store.GetAccounts()
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new AccountDto(a.Name, a.Kind switch
            {
                AccountKind.CreditCard => "card",
                AccountKind.Cash => "cash",
                _ => "bank",
            }))
            .ToList();
        var categories = store.GetKnownClassifications()
            .Select(c => new CategoryDto(c.Category, c.Item))
            .ToList();
        return new LookupsDto(accounts, categories);
    }

    private static string ResolveAccount(CashPilotStore store, string? name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) throw new MobileRequestException("Informe a conta.");
        var known = store.GetAccounts().Select(a => a.Name)
            .Concat(store.GetAccountNamesInUse())
            .FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        return known ?? throw new MobileRequestException($"A conta \"{wanted}\" não está cadastrada.");
    }

    private static EntryDto ToDto(Transaction t) => new(
        t.Id, t.Date, t.Account, t.RawDescription, Math.Abs(t.Amount),
        t.Type switch
        {
            TransactionType.CardBillPayment => EntryKinds.BillPayment,
            TransactionType.InternalTransfer => t.Amount < 0 ? EntryKinds.TransferOut : EntryKinds.TransferIn,
            _ => t.Amount < 0 ? EntryKinds.Expense : EntryKinds.Income,
        },
        t.Category, t.Item);
}
