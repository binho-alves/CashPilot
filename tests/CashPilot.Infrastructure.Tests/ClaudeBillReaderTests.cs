using System.Net;
using System.Text;
using CashPilot.Infrastructure.Importing;

namespace CashPilot.Infrastructure.Tests;

public class ClaudeBillReaderTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Request = request;
            Sent = await request.Content!.ReadAsStringAsync(cancel);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Reply(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } } });

    [Fact]
    public void ParseReplyTurnsJsonIntoBillLinesAndToleratesAFence()
    {
        var result = ClaudeBillReader.ParseReply("""
            Aqui está:
            ```json
            [{"date":"08/10","description":"LOJA  ALFA","amount":35.05},
             {"date":"05/10","description":"Estorno loja","amount":-12.5},
             {"date":"03/10","description":"Sem valor","amount":0}]
            ```
            """);

        Assert.Equal(2, result.Entries);
        Assert.Equal("08/10 LOJA ALFA R$ 35,05\n05/10 Estorno loja -R$ 12,50\n", result.Text);
    }

    [Fact]
    public void ParseReplyWithoutAListIsAnError() =>
        Assert.Throws<InvalidOperationException>(() => ClaudeBillReader.ParseReply("não consegui"));

    [Fact]
    public async Task ReadSendsTheImageWithTheKeyAndReturnsTheEntries()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Reply("[{\"date\":\"08/10\",\"description\":\"Loja Alfa\",\"amount\":10}]"));
        var reader = new ClaudeBillReader(new HttpClient(handler), "chave-de-teste");

        var result = await reader.ReadAsync(new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2 } });

        Assert.Equal(1, result.Entries);
        Assert.Equal("chave-de-teste", handler.Request!.Headers.GetValues("x-api-key").Single());
        Assert.Contains("image/png", handler.Sent);
        Assert.Contains("\"model\":\"" + ClaudeBillReader.DefaultModel + "\"", handler.Sent);
    }

    [Fact]
    public async Task ARefusedKeyBecomesAMessageForTheUser()
    {
        var reader = new ClaudeBillReader(new HttpClient(new FakeHandler(HttpStatusCode.Unauthorized, "{}")), "errada");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(new[] { new byte[] { 1, 2, 3, 4 } }));
        Assert.Contains("401", error.Message);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsSent()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");
        var reader = new ClaudeBillReader(new HttpClient(handler), "  ");

        Assert.False(reader.IsConfigured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(new[] { new byte[] { 1 } }));
        Assert.Null(handler.Request);
    }
}
