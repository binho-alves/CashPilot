using CashPilot.Mobile.Services;

namespace CashPilot.Mobile.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly AppSettings _settings;
    private readonly CashPilotClient _client;
    private readonly SyncService _sync;

    public SettingsPage(AppSettings settings, CashPilotClient client, SyncService sync)
    {
        InitializeComponent();
        _settings = settings;
        _client = client;
        _sync = sync;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UrlEntry.Text = _settings.ServerUrl;
        KeyEntry.Text = await _settings.GetApiKeyAsync();
    }

    private async void OnSaveAndTest(object? sender, EventArgs e)
    {
        _settings.ServerUrl = UrlEntry.Text ?? "";
        await _settings.SetApiKeyAsync(KeyEntry.Text ?? "");
        UrlEntry.Text = _settings.ServerUrl;

        if (!_settings.IsConfigured)
        {
            StatusLabel.TextColor = Colors.Firebrick;
            StatusLabel.Text = "Informe o endereço do PC.";
            return;
        }

        StatusLabel.TextColor = Colors.Gray;
        StatusLabel.Text = "Testando…";
        var ping = await _client.PingAsync();
        if (!ping.Ok)
        {
            StatusLabel.TextColor = Colors.Firebrick;
            StatusLabel.Text = ping.Message ?? "Não foi possível conectar.";
            return;
        }

        var report = await _sync.SyncAsync();
        StatusLabel.TextColor = Colors.SeaGreen;
        StatusLabel.Text = "Conectado. Contas e categorias atualizadas"
            + (report.Sent > 0 ? $"; {report.Sent} lançamento(s) enviado(s)." : ".");
    }
}
