using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CashPilot.Domain.Accounts;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Importing;

/// <param name="Queued">Files read and put in the queue for the user to check.</param>
/// <param name="Failed">Files that could not be read (they are in the queue too, with the reason).</param>
/// <param name="Messages">Notes for the log and the screen (a folder that matches no account, for example).</param>
public sealed record InboxScanResult(int Queued, int Failed, IReadOnlyList<string> Messages);

/// <summary>
/// Looks at a watched folder (a Google Drive folder mirrored on the computer, for example) with one subfolder per account
/// or card, named like the account. Each new file is read with the same readers as the Importar page and put in a queue
/// (<see cref="InboxItem"/>); nothing is imported until the user checks it. Read files are moved to a "_lidos" subfolder
/// and remembered by content hash, so the same file never enters the queue twice.
/// </summary>
public static class InboxScanner
{
    public const string ReadFolder = "_lidos";
    private static readonly string[] FileExtensions = [".pdf", ".png", ".jpg", ".jpeg", ".txt", ".csv"];
    private const long MaxBytes = 150L * 1024 * 1024;

    /// <param name="ocr">Reads the text of a picture (the local OCR).</param>
    /// <param name="now">The clock, so a file still being copied (changed seconds ago) is left for the next round.</param>
    public static InboxScanResult Scan(string root, CashPilotStore store, Func<byte[], string> ocr, DateTime now, TimeSpan? settle = null)
    {
        var messages = new List<string>();
        if (!Directory.Exists(root)) return new InboxScanResult(0, 0, [$"A pasta {root} não existe."]);

        var accounts = store.GetAccounts();
        var names = accounts.Select(a => a.Name).Concat(store.GetAccountNamesInUse())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int queued = 0, failed = 0;

        foreach (var folder in Directory.EnumerateDirectories(root).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(folder);
            if (folderName.StartsWith('_') || folderName.StartsWith('.')) continue;

            var account = Match(folderName, names);
            var files = Directory.EnumerateFiles(folder)
                .Where(f => FileExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
            if (account is null)
            {
                if (files.Count > 0)
                    messages.Add($"A pasta \"{folderName}\" não tem o nome de nenhuma conta ou cartão cadastrado: {files.Count} arquivo(s) ignorado(s).");
                continue;
            }

            var positiveAreExpenses = accounts.FirstOrDefault(a => string.Equals(a.Name, account, StringComparison.OrdinalIgnoreCase))
                ?.Kind != AccountKind.BankAccount;

            foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (now - info.LastWriteTime < (settle ?? TimeSpan.FromSeconds(10))) continue;   // still being synced
                    if (info.Length == 0 || info.Length > MaxBytes) { messages.Add($"{info.Name}: arquivo vazio ou grande demais."); continue; }

                    var bytes = File.ReadAllBytes(file);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!store.InboxHashKnown(hash))
                    {
                        var read = Read(info.Name, bytes, ocr);
                        string status, text = "";
                        string? note;
                        int entries = 0;
                        if (read.Text is null) { status = "failed"; note = read.Message; failed++; }
                        else
                        {
                            var converted = StatementText.ToGastosCsv(read.Text, account, DateOnly.FromDateTime(now), positiveAreExpenses);
                            if (converted.Entries == 0) { status = "failed"; note = "Nenhum lançamento reconhecido no arquivo."; failed++; }
                            else { status = "pending"; text = read.Text; entries = converted.Entries; note = read.Message; queued++; }
                        }
                        store.AddInboxItem(account, info.Name, hash, status, text, entries, note);
                    }
                    MoveToRead(folder, file, now);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    messages.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        return new InboxScanResult(queued, failed, messages);
    }

    /// <summary>The text lines of a file, or null with the reason it could not be read.</summary>
    internal static (string? Text, string? Message) Read(string fileName, byte[] bytes, Func<byte[], string> ocr)
    {
        if (ImageTextReader.IsImage(fileName))
        {
            var raw = ocr(bytes);
            var bill = CardBillPdf.FromOcrText(raw);
            return bill is { Entries: > 0 }
                ? (bill.Text, "Lido da imagem por OCR: confira valores e descrições."
                    + (bill.Repaired > 0 ? $" {bill.Repaired} data(s) tiveram o mês refeito pelas linhas vizinhas." : ""))
                : (raw, "Lido da imagem por OCR (não parecia uma fatura): confira o texto.");
        }

        var loaded = ImportFileLoader.Load(fileName, bytes);
        switch (loaded.Kind)
        {
            case ImportKind.PlainText:
                return (loaded.Text, loaded.Note);
            case ImportKind.BankStatement:
                return (null, "É um extrato de conta, não uma fatura: importe pela página Importar, em \"Enviar arquivo\".");
            case ImportKind.GastosSheet:
                return (null, "É a planilha Gastos: importe pela página Importar.");
        }

        // A PDF without text (a picture of the app screen): read it with OCR.
        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var (result, error) = CardBillPdf.FromPdf(bytes, ocr);
            return result is null
                ? (null, error ?? loaded.Error ?? "PDF não reconhecido.")
                : (result.Text, $"PDF lido por OCR ({result.Entries} lançamentos): confira valores e descrições.");
        }
        return (null, loaded.Error ?? "Arquivo não reconhecido.");
    }

    private static void MoveToRead(string folder, string file, DateTime now)
    {
        var target = Path.Combine(folder, ReadFolder);
        Directory.CreateDirectory(target);
        var name = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Path.GetFileName(file);
        File.Move(file, Path.Combine(target, name));
    }

    /// <summary>The registered account whose name equals the folder name, ignoring case, accents and extra spaces.</summary>
    public static string? Match(string folderName, IEnumerable<string> accountNames)
    {
        var wanted = Simplify(folderName);
        return accountNames.FirstOrDefault(n => Simplify(n) == wanted);
    }

    private static string Simplify(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsWhiteSpace(c) ? ' ' : char.ToLowerInvariant(c));
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>Imports a checked item of the queue with the same path as the Importar page (text lines → Gastos CSV → importer).</summary>
public static class InboxImport
{
    public static (ImportResult? Result, string? Error) Import(CashPilotStore store, InboxItem item, string text, bool positiveAreExpenses, DateOnly today)
    {
        var converted = StatementText.ToGastosCsv(text, item.Account, today, positiveAreExpenses);
        if (converted.Entries == 0) return (null, "Nenhum lançamento reconhecido no texto.");

        var result = new GastosImporter(store).Import(new StringReader(converted.Csv), "pasta:" + item.Account);
        store.SetInboxStatus(item.Id, "imported");
        return (result, null);
    }
}
