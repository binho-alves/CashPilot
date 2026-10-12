using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CashPilot.Infrastructure.Importing;

/// <summary>
/// Optional: reads a picture of a card bill with Claude (vision) through the Anthropic API. Unlike the local OCR, the
/// picture LEAVES the computer, so it only runs when the user presses the button and an API key is configured
/// (<c>CashPilot:AnthropicApiKey</c>, kept in user-secrets). The answer is turned into the same "dd/MM DESC R$ amount"
/// lines as the OCR, so the user still reviews the text before importing.
/// </summary>
public sealed class ClaudeBillReader
{
    public const string DefaultModel = "claude-sonnet-5-5";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const long MaxImageBytes = 5 * 1024 * 1024;   // API limit per image

    private const string Prompt =
        "Esta é a imagem de uma fatura de cartão de crédito. Liste TODOS os lançamentos (compras, tarifas, IOF, parcelas, estornos). " +
        "Ignore pagamentos da fatura anterior, saldo anterior, totais e títulos de seção. " +
        "Responda SOMENTE com um array JSON, sem texto antes ou depois, no formato " +
        "[{\"date\":\"dd/MM\",\"description\":\"texto do histórico\",\"amount\":35.05}]. " +
        "amount é o valor em reais como número positivo; use número negativo só para créditos e estornos. " +
        "Se houver colunas de moeda estrangeira, use a coluna em reais. Não invente lançamentos: se não conseguir ler, deixe de fora.";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string _model;

    public ClaudeBillReader(HttpClient http, string? apiKey, string? model = null)
    {
        _http = http;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
    }

    public bool IsConfigured => _apiKey is not null;

    /// <summary>The bill lines for the text box, or an <see cref="InvalidOperationException"/> with a message for the user.</summary>
    public async Task<CardBillPdfResult> ReadAsync(IReadOnlyList<byte[]> images, CancellationToken cancel = default)
    {
        if (_apiKey is null) throw new InvalidOperationException("Falta a chave da API (CashPilot:AnthropicApiKey).");
        if (images.Count == 0) throw new InvalidOperationException("Não há imagem para enviar.");

        var lines = new StringBuilder();
        int entries = 0;
        foreach (var image in images)
        {
            var reply = await AskAsync(image, cancel);
            var parsed = ParseReply(reply);
            lines.Append(parsed.Text);
            entries += parsed.Entries;
        }
        if (entries == 0) throw new InvalidOperationException("A IA não encontrou lançamentos na imagem.");
        return new CardBillPdfResult(lines.ToString(), entries, 0);
    }

    private async Task<string> AskAsync(byte[] image, CancellationToken cancel)
    {
        if (image.Length > MaxImageBytes)
            throw new InvalidOperationException("A imagem passa de 5 MB, o limite da API. Recorte ou reduza o print, ou use a leitura local.");

        var body = BuildRequest(image, _model);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, cancel); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new InvalidOperationException("Não consegui falar com a API da Anthropic: " + ex.Message, ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancel);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(DescribeError((int)response.StatusCode, text));
            return ExtractText(text);
        }
    }

    internal static string BuildRequest(byte[] image, string model)
    {
        var mediaType = image.Length > 3 && image[0] == 0xFF && image[1] == 0xD8 ? "image/jpeg" : "image/png";
        return JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 8000,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image", source = new { type = "base64", media_type = mediaType, data = Convert.ToBase64String(image) } },
                        new { type = "text", text = Prompt },
                    },
                },
            },
        });
    }

    /// <summary>The text of the first text block of an API response.</summary>
    internal static string ExtractText(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                if (block.TryGetProperty("text", out var t) && t.GetString() is { } s) return s;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
        throw new InvalidOperationException("Resposta inesperada da API.");
    }

    private static string DescribeError(int status, string body) => status switch
    {
        401 => "A API recusou a chave (401). Confira CashPilot:AnthropicApiKey.",
        402 or 403 => "A API negou o acesso (" + status + "). Confira a chave e o saldo da conta no Console da Anthropic.",
        429 => "Limite de uso da API atingido (429). Tente de novo em instantes.",
        _ => $"A API respondeu erro {status}: " + (body.Length > 200 ? body[..200] + "…" : body),
    };

    /// <summary>Turns the model's JSON answer into "dd/MM DESC R$ amount" lines (a ```json fence around it is tolerated).</summary>
    public static CardBillPdfResult ParseReply(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) throw new InvalidOperationException("A IA não devolveu uma lista de lançamentos.");

        var text = new StringBuilder();
        int entries = 0;
        try
        {
            using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var date = item.GetProperty("date").GetString()?.Trim();
                var description = item.GetProperty("description").GetString()?.Trim();
                if (string.IsNullOrEmpty(date) || string.IsNullOrEmpty(description)) continue;
                if (!TryAmount(item.GetProperty("amount"), out var amount) || amount == 0) continue;

                var formatted = Math.Abs(amount).ToString("N2", CultureInfo.GetCultureInfo("pt-BR"));
                text.Append(date).Append(' ').Append(Collapse(description)).Append(' ')
                    .Append(amount < 0 ? "-R$ " : "R$ ").Append(formatted).Append('\n');
                entries++;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Não entendi a resposta da IA: " + ex.Message, ex);
        }
        return new CardBillPdfResult(text.ToString(), entries, 0);
    }

    private static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool TryAmount(JsonElement element, out decimal amount)
    {
        amount = 0;
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDecimal(out amount);
        return element.ValueKind == JsonValueKind.String
               && decimal.TryParse(element.GetString(), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out amount);
    }
}
