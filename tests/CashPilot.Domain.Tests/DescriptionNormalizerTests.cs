using CashPilot.Domain.Descriptions;

namespace CashPilot.Domain.Tests;

public class DescriptionNormalizerTests
{
    [Theory]
    [InlineData("PIX QRS GDS INFORMA31/08", "PIX QRS GDS INFORMA")]
    [InlineData("PIX QRS GDS INFORMA12/08", "PIX QRS GDS INFORMA")]
    [InlineData("DA DAS MEI SDAU15811467", "DA DAS MEI")]
    [InlineData("RiHappy - Carrinhos Mário Maggie", "RIHAPPY CARRINHOS MARIO MAGGIE")]
    [InlineData("99FOOD *PEDIDO", "99FOOD PEDIDO")]
    [InlineData("MERCADO LIVRE PARC02/03", "MERCADO LIVRE")]
    [InlineData("AMAZON MARKETPLACE 03/06", "AMAZON MARKETPLACE")]
    [InlineData("Cizi Mercado Express L", "CIZI MERCADO EXPRESS L")]
    [InlineData("   ", "")]
    public void Normalizes(string input, string expected) =>
        Assert.Equal(expected, DescriptionNormalizer.Normalize(input));
}
