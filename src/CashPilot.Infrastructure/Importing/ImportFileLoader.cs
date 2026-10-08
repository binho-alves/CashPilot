using System.Text;

namespace CashPilot.Infrastructure.Importing;

public enum ImportKind
{
    Unknown,
    /// <summary>The user's "Gastos" spreadsheet exported as CSV.</summary>
    GastosSheet,
    /// <summary>A bank account statement (Bradesco CSV/PDF, Itaú PDF).</summary>
    BankStatement,
    /// <summary>Plain text (pasted card bill lines, for example); needs the account chosen by the user.</summary>
    PlainText,
}

public sealed record LoadedFile(ImportKind Kind, string? Text = null, BankStatement? Statement = null, string? Error = null);

/// <summary>Looks at an uploaded file (CSV, TXT or PDF) and says what it is, so the user does not have to choose.</summary>
public static class ImportFileLoader
{
    public static LoadedFile Load(string fileName, byte[] bytes)
    {
        if (bytes.Length == 0) return new LoadedFile(ImportKind.Unknown, Error: "O arquivo está vazio.");

        if (IsPdf(fileName, bytes))
        {
            IReadOnlyList<PdfWord> words;
            try { words = PdfWordReader.Read(bytes); }
            catch (Exception ex)
            {
                return new LoadedFile(ImportKind.Unknown, Error: "Não consegui abrir o PDF: " + ex.Message);
            }

            if (words.Count == 0)
                return new LoadedFile(ImportKind.Unknown, Error: "O PDF não tem texto (parece escaneado ou é uma imagem). Por enquanto só leio PDFs com texto.");

            var statement = BankStatementParsers.TryParsePdf(words);
            return statement is null
                ? new LoadedFile(ImportKind.Unknown, Error: "Formato de PDF não reconhecido. Por enquanto: extrato do Bradesco e extrato do Itaú.")
                : new LoadedFile(ImportKind.BankStatement, Statement: statement);
        }

        var text = Decode(bytes);

        var bank = BankStatementParsers.TryParseBradescoCsv(text);
        if (bank is not null) return new LoadedFile(ImportKind.BankStatement, Statement: bank);

        if (LooksLikeGastosSheet(text)) return new LoadedFile(ImportKind.GastosSheet, Text: text);

        return new LoadedFile(ImportKind.PlainText, Text: text);
    }

    private static bool IsPdf(string fileName, byte[] bytes) =>
        fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
        || (bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F');

    /// <summary>Banks often export in Windows-1252: UTF-8 is tried strictly first, then Latin-1.</summary>
    private static string Decode(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        return text.TrimStart('﻿');
    }

    private static bool LooksLikeGastosSheet(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = (end < 0 ? text : text[..end]).ToUpperInvariant();
        return header.Contains("CONTA", StringComparison.Ordinal) && header.Contains("VALOR", StringComparison.Ordinal);
    }
}
