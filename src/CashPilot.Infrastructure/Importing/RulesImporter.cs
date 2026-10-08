using CashPilot.Domain.Descriptions;
using CashPilot.Infrastructure.Persistence;

namespace CashPilot.Infrastructure.Importing;

public sealed record RulesImportResult(int RulesSaved, int Reclassified, int StillPending, IReadOnlyList<RejectedRow> Rejected);

/// <summary>
/// Loads manual "contains" rules from a CSV (header: pattern,category,item) and applies them to entries without a category.
/// A rule matches when the normalized description contains the normalized pattern; the longest pattern wins.
/// </summary>
public sealed class RulesImporter
{
    private readonly CashPilotStore _store;

    public RulesImporter(CashPilotStore store)
    {
        _store = store;
    }

    public RulesImportResult Import(TextReader reader)
    {
        var table = CsvParser.Parse(reader.ReadToEnd());
        if (table.Count == 0) throw new FormatException("Empty rules file.");

        var header = table[0].Select(h => h.Trim().ToUpperInvariant()).ToList();
        var pattern = header.IndexOf("PATTERN");
        var category = header.IndexOf("CATEGORY");
        var item = header.IndexOf("ITEM");
        if (pattern < 0 || category < 0)
            throw new FormatException("Header must contain: pattern, category (and optionally item).");

        var rejected = new List<RejectedRow>();
        var saved = 0;
        var reclassified = 0;

        _store.InTransaction(() =>
        {
            for (var i = 1; i < table.Count; i++)
            {
                var cells = table[i];
                string Cell(int column) => column >= 0 && column < cells.Count ? cells[column].Trim() : "";

                if (cells.All(c => string.IsNullOrWhiteSpace(c))) continue;

                var text = Cell(pattern);
                if (DescriptionNormalizer.Normalize(text).Length == 0)
                {
                    rejected.Add(new RejectedRow(i + 1, "Empty pattern."));
                    continue;
                }
                if (Cell(category).Length == 0)
                {
                    rejected.Add(new RejectedRow(i + 1, $"Missing category for '{text}'."));
                    continue;
                }

                _store.SaveRule(RuleKind.Contains, text, new Classification(Cell(category), Cell(item)));
                saved++;
            }

            reclassified = _store.ReclassifyPending();
        });

        return new RulesImportResult(saved, reclassified, _store.GetPending().Count, rejected);
    }
}
