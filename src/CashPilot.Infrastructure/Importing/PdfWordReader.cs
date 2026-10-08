using UglyToad.PdfPig;

namespace CashPilot.Infrastructure.Importing;

/// <summary>Reads the words of a text PDF (not a scanned one) with their positions.</summary>
public static class PdfWordReader
{
    public static IReadOnlyList<PdfWord> Read(byte[] pdf)
    {
        var result = new List<PdfWord>();
        using var document = PdfDocument.Open(pdf);
        foreach (var page in document.GetPages())
        {
            foreach (var word in page.GetWords())
            {
                if (word.Letters.Count == 0) continue;

                // The baseline is the same for every word of a line; the top of the glyphs is not
                // (digits are shorter than letters with ascenders).
                var baseline = word.Letters[0].StartBaseLine.Y;
                result.Add(new PdfWord(page.Number, word.Text, word.BoundingBox.Left, word.BoundingBox.Right, page.Height - baseline));
            }
        }
        return result;
    }
}
