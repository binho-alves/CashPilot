using CashPilot.Domain.Descriptions;

namespace CashPilot.Domain.Tests;

public class AmbiguityTests
{
    private static readonly Classification Insurance = new("Moradia & Habitação", "Seguro");
    private static readonly Classification Consortium = new("Investimento", "Consórcio");

    [Fact]
    public void SameDescriptionWithDifferentLabelsIsNotGuessed()
    {
        var classifier = new Classifier();
        classifier.Observe("PAGTO ELETRON COBRANCA", Insurance);
        classifier.Observe("Pagto Eletron Cobranca", Consortium);

        Assert.True(classifier.IsAmbiguous("PAGTO ELETRON COBRANCA"));
        Assert.False(classifier.Classify("PAGTO ELETRON COBRANCA").IsClassified);
    }

    [Fact]
    public void SameLabelTwiceIsNotAmbiguous()
    {
        var classifier = new Classifier();
        classifier.Observe("Padaria Exemplo", Insurance);
        classifier.Observe("PADARIA EXEMPLO", Insurance);

        Assert.False(classifier.IsAmbiguous("Padaria Exemplo"));
        Assert.Equal(Insurance, classifier.Classify("Padaria Exemplo").Classification);
    }

    [Fact]
    public void ExplicitLearnSettlesTheAmbiguity()
    {
        var classifier = new Classifier();
        classifier.Observe("PAGTO ELETRON COBRANCA", Insurance);
        classifier.Observe("PAGTO ELETRON COBRANCA", Consortium);

        classifier.Learn("PAGTO ELETRON COBRANCA", Consortium);

        Assert.False(classifier.IsAmbiguous("PAGTO ELETRON COBRANCA"));
        Assert.Equal(Consortium, classifier.Classify("PAGTO ELETRON COBRANCA").Classification);
    }
}
