using Tesseract;

namespace CashPilot.Infrastructure.Importing;

/// <summary>
/// Reads the text of a screenshot or photo with Tesseract, locally: free, offline, nothing leaves the computer.
/// Needs <c>por.traineddata</c> in the tessdata folder. OCR is imperfect, so the text is always shown to the user
/// to review before anything is imported.
/// </summary>
public sealed class ImageTextReader
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif"];

    private readonly string _tessdataDirectory;

    public ImageTextReader(string tessdataDirectory)
    {
        _tessdataDirectory = tessdataDirectory;
    }

    public static bool IsImage(string fileName) =>
        Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The language model used. "eng" reads digits, slashes and currency far better than the "fast" Portuguese model
    /// (checked on a bill screenshot: dates like "08/10" came out as "08M0" with por, clean with eng), and a bill is mostly
    /// that; the merchant names only lose their accents, which the description normalizer ignores anyway.
    /// Falls back to "por" when eng.traineddata is not in the tessdata folder.
    /// </summary>
    private string Language => File.Exists(Path.Combine(_tessdataDirectory, "eng.traineddata")) ? "eng" : "por";

    /// <summary>The recognized text, line by line. Throws <see cref="InvalidOperationException"/> with a message for the user.</summary>
    public string Read(byte[] image)
    {
        if (!File.Exists(Path.Combine(_tessdataDirectory, "por.traineddata")) && Language == "por")
            throw new InvalidOperationException(
                $"Falta o arquivo por.traineddata (ou eng.traineddata) em {_tessdataDirectory} (baixe de github.com/tesseract-ocr/tessdata_fast).");

        try
        {
            using var engine = new TesseractEngine(_tessdataDirectory, Language, EngineMode.Default);
            // A screenshot of a list is one block of lines: reading it as such (instead of guessing the page layout)
            // keeps each row together.
            engine.DefaultPageSegMode = PageSegMode.SingleBlock;
            using var original = Pix.LoadFromMemory(image);
            using var pix = Prepare(original);
            using var page = engine.Process(pix);
            return page.GetText().Replace("\r\n", "\n").Trim();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Não consegui ler a imagem: " + ex.Message, ex);
        }
    }

    /// <summary>Gray scale and, for small pictures, up to 4x the size: small screenshot text reads far better that way.</summary>
    private static Pix Prepare(Pix source)
    {
        try
        {
            var gray = source.Depth == 32 ? source.ConvertRGBToGray(0.30f, 0.59f, 0.11f) : source.Clone();
            if (source.Width >= 3000) return gray;
            // Small text reads better the bigger it is: aim at about 3500 px wide, at most 4x (4x gave every date right on the test screenshot).
            var factor = Math.Clamp(MathF.Round(3500f / source.Width), 2f, 4f);
            var scaled = gray.Scale(factor, factor);
            gray.Dispose();
            return scaled;
        }
        catch (Exception)
        {
            return source.Clone();
        }
    }
}
