using CashPilot.Domain.Reports;
using CashPilot.Infrastructure.Persistence;
using CashPilot.Web.Components;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var databasePath = ResolveDatabasePath(builder.Configuration);
builder.Services.AddScoped(_ => new CashPilotStore(databasePath));
builder.Services.AddScoped<CashPilot.Web.ToastService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseAntiforgery();

// JSON API: the same data the pages show, ready for the future mobile app.
app.MapGet("/api/months", (CashPilotStore store) =>
    SpendingReport.ByMonth(store.GetAll())
        .Select(m => new { m.Year, m.Month, m.Total, m.Count, m.Uncategorized }));

app.MapGet("/api/months/{year:int}/{month:int}", (int year, int month, CashPilotStore store) =>
{
    var selected = SpendingReport.ByMonth(store.GetAll()).FirstOrDefault(m => m.Year == year && m.Month == month);
    return selected is null ? Results.NotFound() : Results.Ok(selected);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

Console.WriteLine($"CashPilot database: {databasePath}");
app.Run();

// Finds data/cashpilot.db at the repository root (where CashPilot.slnx lives), no matter where the app is started.
static string ResolveDatabasePath(IConfiguration configuration)
{
    var configured = configuration["CashPilot:Database"];
    if (!string.IsNullOrWhiteSpace(configured)) return configured;

    var start = new DirectoryInfo(Directory.GetCurrentDirectory());
    var directory = start;
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CashPilot.slnx")))
        directory = directory.Parent;

    return Path.Combine((directory ?? start).FullName, "data", "cashpilot.db");
}
