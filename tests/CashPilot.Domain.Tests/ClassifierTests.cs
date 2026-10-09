using CashPilot.Domain.Descriptions;

namespace CashPilot.Domain.Tests;

public class ClassifierTests
{
    private static readonly Classification Groceries = new("Alimentação", "Supermercado");
    private static readonly Classification Pharmacy = new("Saúde & Bem-Estar", "Farmácia & Medicamentos");
    private static readonly Classification Delivery = new("Alimentação", "Restaurantes & Delivery");

    [Fact]
    public void WithoutKnowledgeReturnsUnknown()
    {
        var result = new Classifier().Classify("ALGO NOVO");
        Assert.False(result.IsClassified);
        Assert.Equal(ClassificationSource.None, result.Source);
    }

    [Fact]
    public void LearnsAndRecognizesSameDescriptionIgnoringDateAndCode()
    {
        var classifier = new Classifier();
        classifier.Learn("PIX QRS AUTO POSTO 15/08", new Classification("Transporte", "Combustível"));

        var result = classifier.Classify("PIX QRS AUTO POSTO 02/09");

        Assert.Equal(ClassificationSource.Exact, result.Source);
        Assert.Equal("Combustível", result.Classification!.Item);
    }

    [Fact]
    public void ClassifiesVerySimilarDescription()
    {
        var classifier = new Classifier();
        classifier.Learn("Cizi Mercado Express L", Groceries);

        var result = classifier.Classify("CIZI MERCADO EXPRESS LJ");

        Assert.Equal(ClassificationSource.Similarity, result.Source);
        Assert.Equal(Groceries, result.Classification);
        Assert.True(result.Confidence >= 0.8);
    }

    [Fact]
    public void DoesNotClassifyDifferentDescription()
    {
        var classifier = new Classifier();
        classifier.Learn("FARMACIA DROGA RAIA", Pharmacy);

        Assert.False(classifier.Classify("PADARIA SAO JOAO").IsClassified);
    }

    [Fact]
    public void ContainsRuleWorks()
    {
        var classifier = new Classifier();
        classifier.AddContainsRule("99FOOD", Delivery);

        var result = classifier.Classify("99FOOD *PEDIDO 8841234");

        Assert.Equal(ClassificationSource.Contains, result.Source);
        Assert.Equal(Delivery, result.Classification);
    }

    [Fact]
    public void SuggestOffersTheClosestKnownDescriptionEvenBelowTheThreshold()
    {
        var classifier = new Classifier();
        classifier.Learn("Farmacia Central Centro", Pharmacy);

        Assert.False(classifier.Classify("Farmacia Central Norte").IsClassified);   // not sure enough to classify

        var suggestion = classifier.Suggest("Farmacia Central Norte");

        Assert.True(suggestion.IsClassified);
        Assert.Equal(Pharmacy, suggestion.Classification);
        Assert.True(suggestion.Confidence < classifier.SimilarityThreshold);
    }

    [Fact]
    public void SuggestReturnsTheSureAnswerWhenThereIsOne()
    {
        var classifier = new Classifier();
        classifier.Learn("Cizi Mercado Express L", Groceries);

        Assert.Equal(ClassificationSource.Exact, classifier.Suggest("Cizi Mercado Express L").Source);
    }

    [Fact]
    public void SuggestSaysNothingForUnrelatedOrAmbiguousDescriptions()
    {
        var classifier = new Classifier();
        classifier.Learn("Farmacia Central Centro", Pharmacy);
        Assert.False(classifier.Suggest("Oficina Mecanica Beta").IsClassified);

        classifier.Observe("PAGTO ELETRON COBRANCA", Groceries);
        classifier.Observe("PAGTO ELETRON COBRANCA", Delivery);   // same description, two classifications
        Assert.False(classifier.Suggest("PAGTO ELETRON COBRANCA").IsClassified);
    }
}
