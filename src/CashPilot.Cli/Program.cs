using System.Globalization;
using System.Text;
using CashPilot.Domain.Reports;
using CashPilot.Domain.Transactions;
using CashPilot.Domain.Descriptions;
using CashPilot.Infrastructure.Importing;
using CashPilot.Infrastructure.Persistence;

Console.OutputEncoding = Encoding.UTF8;

var arguments = args.ToList();
var databasePath = TakeOption(arguments, "--db") ?? Path.Combine("data", "cashpilot.db");

if (arguments.Count == 0 || arguments[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

using var store = new CashPilotStore(databasePath);

switch (arguments[0])
{
    case "import-gastos" when arguments.Count == 2:
        return ImportGastos(store, arguments[1]);

    case "apply-rules" when arguments.Count is 1 or 2:
        return ApplyRules(store, arguments.Count == 2 ? arguments[1] : Path.Combine("data", "rules.csv"));

    case "mark-cashout" when arguments.Count == 2:
        var marked = store.MarkCashOut(arguments[1]);
        Console.WriteLine($"Marked as card cash-out (not spending): {marked} stored entr{(marked == 1 ? "y" : "ies")}. Future imports will do the same.");
        return 0;

    case "report" when arguments.Count is 1 or 2:
        return ShowReport(store, arguments.Count == 2 ? arguments[1] : null);

    case "pending":
        return ShowPending(store);

    case "classify" when arguments.Count is 3 or 4:
        var updated = store.Classify(arguments[1], new Classification(arguments[2], arguments.Count == 4 ? arguments[3] : ""));
        Console.WriteLine($"Learned. {updated} pending entr{(updated == 1 ? "y" : "ies")} updated.");
        return 0;

    case "stats":
        Console.WriteLine($"{store.Count()} transactions, {store.GetPending().Count} pending classification. Database: {databasePath}");
        return 0;

    default:
        PrintUsage();
        return 1;
}

static int ImportGastos(CashPilotStore store, string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }

    using var reader = new StreamReader(path, Encoding.UTF8);
    var result = new GastosImporter(store).Import(reader, Path.GetFileName(path));

    Console.WriteLine($"Rows read:            {result.RowsRead}");
    Console.WriteLine($"Inserted:             {result.Inserted}");
    Console.WriteLine($"Already there (skip): {result.Duplicates}");
    Console.WriteLine($"Taught from sheet:    {result.TaughtFromSheet}");
    Console.WriteLine($"Classified by rules:  {result.ClassifiedByRules}");
    Console.WriteLine($"Pending (no category):{result.Pending}");
    Console.WriteLine($"Card cash-out (no spend):{result.CardCashOut}");
    foreach (var rejected in result.Rejected)
        Console.WriteLine($"Rejected line {rejected.Line}: {rejected.Reason}");
    return 0;
}

static int ApplyRules(CashPilotStore store, string path)
{
    if (!File.Exists(path))
    {
        Console.Error.WriteLine($"File not found: {path}");
        return 1;
    }

    using var reader = new StreamReader(path, Encoding.UTF8);
    var result = new RulesImporter(store).Import(reader);

    Console.WriteLine($"Rules saved:        {result.RulesSaved}");
    Console.WriteLine($"Entries classified: {result.Reclassified}");
    Console.WriteLine($"Still pending:      {result.StillPending}");
    foreach (var rejected in result.Rejected)
        Console.WriteLine($"Rejected line {rejected.Line}: {rejected.Reason}");
    return 0;
}

static int ShowReport(CashPilotStore store, string? month)
{
    var all = store.GetAll();
    var months = SpendingReport.ByMonth(all);
    var br = CultureInfo.GetCultureInfo("pt-BR");
    var today = DateTime.Today;

    if (month is null)
    {
        Console.WriteLine("Mês        Lançamentos          Gasto   Sem categoria");
        foreach (var m in months)
        {
            var future = m.Year * 12 + m.Month > today.Year * 12 + today.Month ? "   (parcelas futuras)" : "";
            Console.WriteLine($"{m.Year}-{m.Month:00}  {m.Count,11}  {m.Total.ToString("N2", br),13}  {m.Uncategorized.ToString("N2", br),14}{future}");
        }
        PrintExcluded(all, br);
        return 0;
    }

    if (!DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
    {
        Console.Error.WriteLine("Use the format YYYY-MM, e.g. report 2026-09");
        return 1;
    }

    var selected = months.FirstOrDefault(m => $"{m.Year}-{m.Month:00}" == month);
    if (selected is null)
    {
        Console.WriteLine($"No spending in {month}.");
        return 0;
    }

    Console.WriteLine($"{month}: {selected.Total.ToString("N2", br)} em {selected.Count} lançamentos ({selected.Uncategorized.ToString("N2", br)} sem categoria)");
    var categories = selected.Lines
        .GroupBy(l => l.Category)
        .Select(g => (Category: g.Key, Total: g.Sum(l => l.Total), Lines: g.OrderByDescending(l => l.Total).ToList()))
        .OrderByDescending(g => g.Total);
    foreach (var category in categories)
    {
        Console.WriteLine();
        Console.WriteLine($"{category.Category,-32} {category.Total.ToString("N2", br),12}");
        foreach (var line in category.Lines.Where(l => l.Item.Length > 0))
            Console.WriteLine($"    {line.Item,-28} {line.Total.ToString("N2", br),12}  ({line.Count})");
    }
    PrintExcluded(all, br);
    return 0;
}

static void PrintExcluded(IReadOnlyList<Transaction> all, CultureInfo br)
{
    Console.WriteLine();
    Console.WriteLine("Fora dos gastos (todos os meses):");
    foreach (var (type, label) in new[]
    {
        (TransactionType.CardBillPayment, "Pagamento de fatura"),
        (TransactionType.InternalTransfer, "Transferência entre contas"),
        (TransactionType.CardCashAdvance, "Saque na maquininha (bruto)"),
    })
    {
        var items = all.Where(t => t.Type == type).ToList();
        if (items.Count == 0) continue;
        Console.WriteLine($"  {label,-30} {items.Count,4} lançamentos  {items.Sum(t => Math.Abs(t.Amount)).ToString("N2", br),12}");
    }
}

static int ShowPending(CashPilotStore store)
{
    var groups = store.GetPending()
        .GroupBy(t => t.NormalizedDescription)
        .Select(g => (Description: g.First().RawDescription, Count: g.Count(), Total: g.Sum(t => t.Amount)))
        .OrderByDescending(g => Math.Abs(g.Total))
        .ToList();

    if (groups.Count == 0)
    {
        Console.WriteLine("Nothing pending.");
        return 0;
    }

    Console.WriteLine("Count  Total        Description   (use: classify \"<description>\" \"<category>\" \"<item>\")");
    foreach (var group in groups)
        Console.WriteLine($"{group.Count,5}  {group.Total,10:N2}   {group.Description}");
    return 0;
}

static string? TakeOption(List<string> list, string name)
{
    var index = list.IndexOf(name);
    if (index < 0 || index + 1 >= list.Count) return null;
    var value = list[index + 1];
    list.RemoveRange(index, 2);
    return value;
}

static void PrintUsage() => Console.WriteLine("""
    CashPilot CLI

      import-gastos <file.csv>                    Import the "Gastos" tab exported as CSV (safe to repeat)
      apply-rules [rules.csv]                     Load "contains" rules (pattern,category,item; default data/rules.csv)
                                                  and apply them to entries without a category
      mark-cashout "<description>"                Mark a terminal cash-out charge (gross amount) as not spending
      report [YYYY-MM]                            Spending by month; with a month, by category and item
      pending                                     List entries without a category, grouped by description
      classify "<description>" "<category>" ["<item>"]
                                                  Classify pending entries and teach the classifier
      stats                                       Counts

    Option: --db <path>   SQLite file (default: data/cashpilot.db, which is git-ignored)
    """);
