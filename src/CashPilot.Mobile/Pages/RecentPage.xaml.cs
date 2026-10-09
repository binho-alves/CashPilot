using System.Globalization;
using CashPilot.Contracts;
using CashPilot.Mobile.Services;

namespace CashPilot.Mobile.Pages;

public sealed record EntryRow(Guid Id, string Title, string Subtitle, string AmountText, string? Note, bool IsPending)
{
    public bool HasNote => !string.IsNullOrEmpty(Note);
}

public partial class RecentPage : ContentPage
{
    private static readonly CultureInfo Brazil = new("pt-BR");

    private readonly PendingQueue _queue;
    private readonly CashPilotClient _client;
    private readonly SyncService _sync;

    public RecentPage(PendingQueue queue, CashPilotClient client, SyncService sync)
    {
        InitializeComponent();
        _queue = queue;
        _client = client;
        _sync = sync;
        _sync.Changed += () => MainThread.BeginInvokeOnMainThread(async () => await LoadAsync());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async void OnRefreshing(object? sender, EventArgs e)
    {
        await _sync.SyncAsync();
        await LoadAsync();
        Refresher.IsRefreshing = false;
    }

    private async Task LoadAsync()
    {
        var rows = new List<EntryRow>();

        // Entries still on the phone come first: they are what the user may not see on the PC yet.
        foreach (var pending in await _queue.GetAllAsync())
        {
            var r = pending.Request;
            rows.Add(new EntryRow(r.ClientId, r.Description,
                $"{r.Date.ToString("dd/MM", Brazil)} · {r.Account} · {KindLabel(r.Kind)}",
                Signed(r.Kind, r.Amount),
                pending.Error is null ? "Na fila: ainda não foi enviado ao PC" : "Recusado pelo PC: " + pending.Error,
                IsPending: true));
        }

        var fetched = await _client.GetRecentAsync();
        if (fetched is { Ok: true, Value: { } entries })
        {
            InfoLabel.Text = "Digitados à mão, do mais novo ao mais antigo.";
            foreach (var e in entries)
            {
                var subtitle = $"{e.Date.ToString("dd/MM", Brazil)} · {e.Account} · {KindLabel(e.Kind)}";
                if (e.Category is not null) subtitle += " · " + (e.Item is null ? e.Category : $"{e.Category} › {e.Item}");
                rows.Add(new EntryRow(e.Id, e.Description, subtitle, Signed(e.Kind, e.Amount), null, IsPending: false));
            }
        }
        else
        {
            InfoLabel.Text = (fetched.Message ?? "Sem conexão com o PC.") + " Mostrando só o que está na fila.";
        }

        List.ItemsSource = rows;
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (sender is not BindableObject { BindingContext: EntryRow row }) return;
        if (!await DisplayAlert("Apagar lançamento", $"Apagar \"{row.Title}\"?", "Apagar", "Cancelar")) return;

        if (row.IsPending)
        {
            await _queue.RemoveAsync(row.Id);
        }
        else
        {
            var result = await _client.DeleteAsync(row.Id);
            if (!result.Ok)
            {
                await DisplayAlert("Não foi possível apagar", result.Message ?? "Tente de novo em casa.", "OK");
                return;
            }
        }
        await LoadAsync();
    }

    private static string KindLabel(string kind) => kind switch
    {
        EntryKinds.Income => "receita",
        EntryKinds.BillPayment => "pagamento de fatura",
        EntryKinds.TransferOut => "transferência (saiu)",
        EntryKinds.TransferIn => "transferência (entrou)",
        _ => "gasto",
    };

    private static string Signed(string kind, decimal amount)
    {
        var inflow = kind is EntryKinds.Income or EntryKinds.TransferIn;
        return (inflow ? "+" : "−") + amount.ToString("C2", Brazil);
    }
}
