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

    /// <summary>The recognized text, line by line. Throws <see cref="InvalidOperationException"/> with a message for the user.</summary>
    public string Read(byte[] image)
    {
        if (!File.Exists(Path.Combine(_tessdataDirectory, "por.traineddata")))
            throw new InvalidOperationException(
                $"Falta o arquivo por.traineddata em {_tessdataDirectory} (baixe de github.com/tesseract-ocr/tessdata_fast).");

        try
        {
            using var engine = new TesseractEngine(_tessdataDirectory, "por", EngineMode.Default);
            using var pix = Pix.LoadFromMemory(image);
            using var page = engine.Process(pix);
            return page.GetText().Replace("\r\n", "\n").Trim();
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("Não consegui ler a imagem: " + ex.Message, ex);
        }
    }
}
