using CashPilot.Domain.Descriptions;

namespace CashPilot.Domain.Tests;

public class LookalikeTests
{
    [Fact]
    public void GreekLookalikesNormalizeToLatin()
    {
        // "AMAZON" spelled with Greek capital letters alpha, mu, alpha, zeta, omicron, nu.
        var greek = "\u0391\u039C\u0391\u0396\u039F\u039D Prime";

        Assert.Equal(DescriptionNormalizer.Normalize("Amazon Prime"), DescriptionNormalizer.Normalize(greek));
    }

    [Fact]
    public void CyrillicLookalikesNormalizeToLatin()
    {
        // "CA" spelled with Cyrillic capital es and a.
        var cyrillic = "SHOPPING ABC \u0421\u0410";

        Assert.Equal("SHOPPING ABC CA", DescriptionNormalizer.Normalize(cyrillic));
    }
}
