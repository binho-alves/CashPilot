using CashPilot.Contracts;
using CashPilot.Mobile.Services;

namespace CashPilot.Mobile.Pages;

public partial class NewEntryPage : ContentPage
{
    private const string AutomaticCategory = "— automática —";

    private static readonly (string Kind, string Label)[] Kinds =
    [
        (EntryKinds.Expense, "Gasto"),
        (EntryKinds.Income, "Receita"),
        (EntryKinds.BillPayment, "Pagamento de fatura"),
        (EntryKinds.TransferOut, "Transferência entre minhas contas (saiu)"),
        (EntryKinds.TransferIn, "Transferência entre minhas contas (entrou)"),
    ];

    private readonly AppSettings _settings;
    private readonly PendingQueue _queue;
    private readonly LookupsCache _lookups;
    private readonly SyncService _sync;

    private List<CategoryDto> _categoryChoices = [];

    public NewEntryPage(AppSettings settings, PendingQueue queue, LookupsCache lookups, SyncService sync)
    {
        InitializeComponent();
        _settings = settings;
        _queue = queue;
        _lookups = lookups;
        _sync = sync;

        KindPicker.ItemsSource = Kinds.Select(k => k.Label).ToList();
        KindPicker.SelectedIndex = 0;
        DatePick.Date = DateTime.Today;
        _sync.Changed += () => MainThread.BeginInvokeOnMainThread(async () => await RefreshQueueLabelAsync());
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        DatePick.Date = DatePick.Date == default ? DateTime.Today : DatePick.Date;
        LoadPickers();
        await RefreshQueueLabelAsync();
        if (_settings.IsConfigured) await TrySyncAsync(quiet: true);
    }

    private string SelectedKind => Kinds[Math.Max(0, KindPicker.SelectedIndex)].Kind;

    private void OnKindChanged(object? sender, EventArgs e) =>
        CategoryBox.IsVisible = SelectedKind is EntryKinds.Expense or EntryKinds.Income;

    private void LoadPickers()
    {
        var current = _lookups.Current;
        var keep = AccountPicker.SelectedItem as string ?? _settings.LastAccount;
        AccountPicker.ItemsSource = current.Accounts.Select(a => a.Name).ToList();
        var index = current.Accounts.ToList().FindIndex(a => a.Name == keep);
        if (index >= 0) AccountPicker.SelectedIndex = index;

        _categoryChoices = current.Categories.ToList();
        var labels = new List<string> { AutomaticCategory };
        labels.AddRange(_categoryChoices.Select(c => c.Item.Length == 0 ? c.Category : $"{c.Category} › {c.Item}"));
        CategoryPicker.ItemsSource = labels;
        CategoryPicker.SelectedIndex = 0;
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        StatusLabel.TextColor = Colors.Firebrick;
        if (!MoneyInput.TryParse(AmountEntry.Text, out var amount) || amount <= 0)
        {
            StatusLabel.Text = "Digite um valor válido, como 12,50.";
            return;
        }
        var description = (DescriptionEntry.Text ?? "").Trim();
        if (description.Length == 0)
        {
            StatusLabel.Text = "Digite a descrição.";
            return;
        }
        if (AccountPicker.SelectedItem is not string account)
        {
            StatusLabel.Text = _lookups.Current.Accounts.Count == 0
                ? "Ainda não há contas no celular. Abra a aba Config. e toque em Testar conexão (com o PC ligado, em casa)."
                : "Escolha a conta.";
            return;
        }

        string? category = null, item = null;
        if (CategoryBox.IsVisible && CategoryPicker.SelectedIndex > 0)
        {
            var chosen = _categoryChoices[CategoryPicker.SelectedIndex - 1];
            category = chosen.Category;
            item = chosen.Item.Length == 0 ? null : chosen.Item;
        }

        var request = new NewEntryRequest(Guid.NewGuid(), DateOnly.FromDateTime(DatePick.Date), account, description,
            amount, SelectedKind, category, item);

        SaveButton.IsEnabled = false;
        try
        {
            // First to the file, then to the PC: if the app dies in between, the entry is still on the phone.
            await _queue.AddAsync(request);
            _settings.LastAccount = account;

            AmountEntry.Text = "";
            DescriptionEntry.Text = "";
            CategoryPicker.SelectedIndex = 0;

            await _sync.SyncAsync();
            var stillThere = (await _queue.GetAllAsync()).FirstOrDefault(p => p.Request.ClientId == request.ClientId);
            if (stillThere is null)
            {
                StatusLabel.TextColor = Colors.SeaGreen;
                StatusLabel.Text = "Salvo no PC.";
            }
            else if (stillThere.Error is not null)
            {
                StatusLabel.TextColor = Colors.Firebrick;
                StatusLabel.Text = "O PC recusou: " + stillThere.Error;
            }
            else
            {
                StatusLabel.TextColor = Colors.DarkOrange;
                StatusLabel.Text = "Guardado no celular. Será enviado quando o PC estiver ao alcance.";
            }
            await RefreshQueueLabelAsync();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void OnSyncClicked(object? sender, EventArgs e) => await TrySyncAsync(quiet: false);

    private async Task TrySyncAsync(bool quiet)
    {
        var pending = (await _queue.GetAllAsync()).Count(p => p.Error is null);
        if (pending == 0 && quiet) return;
        SyncButton.IsEnabled = false;
        try
        {
            var report = await _sync.SyncAsync();
            if (!quiet)
            {
                StatusLabel.TextColor = report.Problem is null ? Colors.SeaGreen : Colors.DarkOrange;
                StatusLabel.Text = report.Problem ?? (report.Sent > 0 ? $"{report.Sent} enviado(s)." : "Nada a enviar.");
            }
            LoadPickers();
            await RefreshQueueLabelAsync();
        }
        finally { SyncButton.IsEnabled = true; }
    }

    private async Task RefreshQueueLabelAsync()
    {
        var all = await _queue.GetAllAsync();
        var waiting = all.Count(p => p.Error is null);
        var refused = all.Count - waiting;
        var parts = new List<string>();
        if (waiting > 0) parts.Add($"{waiting} lançamento(s) na fila, ainda não enviado(s) ao PC");
        if (refused > 0) parts.Add($"{refused} recusado(s) pelo PC (veja em Últimos)");
        QueueLabel.Text = string.Join(" · ", parts);
        SyncButton.IsVisible = waiting > 0;
    }
}
