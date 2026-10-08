using CashPilot.Infrastructure.Importing;

namespace CashPilot.Infrastructure.Tests;

public class ImageTextReaderTests
{
    [Theory]
    [InlineData("print.png", true)]
    [InlineData("FOTO.JPG", true)]
    [InlineData("scan.tiff", true)]
    [InlineData("extrato.pdf", false)]
    [InlineData("gastos.csv", false)]
    public void RecognizesImageFilesByExtension(string name, bool expected) =>
        Assert.Equal(expected, ImageTextReader.IsImage(name));

    [Fact]
    public void ExplainsWhenTheLanguageFileIsMissing()
    {
        var reader = new ImageTextReader(Path.Combine(Path.GetTempPath(), "cashpilot-no-tessdata-" + Guid.NewGuid()));

        var ex = Assert.Throws<InvalidOperationException>(() => reader.Read([1, 2, 3]));

        Assert.Contains("por.traineddata", ex.Message);
    }
}
