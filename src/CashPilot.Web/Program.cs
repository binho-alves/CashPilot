using System.Text;
using CashPilot.Domain.Reports;
using CashPilot.Infrastructure.Backup;
using CashPilot.Infrastructure.Persistence;
using CashPilot.Web.Components;

var builder = WebApplication.CreateBuilder(args);

if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://localhost:5080");

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var databasePath = ResolveDatabasePath(builder.Configuration);
builder.Services.AddScoped(_ => new CashPilotStore(databasePath));
builder.Services.AddScoped<CashPilot.Web.ToastService>();
builder.Services.AddSingleton(new CashPilot.Web.DatabaseLocation(databasePath));
builder.Services.AddSingleton(new CashPilot.Infrastructure.Importing.ImageTextReader(
    Path.Combine(Path.GetDirectoryName(databasePath) ?? ".", "tessdata")));

var app = builder.Build();

// Today's copy of the database, taken before the app opens (and possibly migrates) it.
try
{
    var copy = DatabaseBackups.EnsureDailyCopy(databasePath, DateOnly.FromDateTime(DateTime.Today));
    if (copy is not null) Console.WriteLine($"CashPilot backup: {copy}");
}
catch (Exception ex)
{
    Console.WriteLine("CashPilot backup failed: " + ex.Message);
}

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

// Downloads from the Backup page: a copy of the database and a CSV of the entries.
app.MapGet("/backup/banco", (CashPilotStore store) =>
{
    var temp = Path.Combine(Path.GetTempPath(), $"cashpilot-{Guid.NewGuid():N}.db");
    try
    {
        store.BackupTo(temp);
        return Results.File(File.ReadAllBytes(temp), "application/vnd.sqlite3", $"cashpilot-{DateTime.Now:yyyyMMdd-HHmm}.db");
    }
    finally
    {
        try { File.Delete(temp); } catch (IOException) { }
    }
});

app.MapGet("/backup/lancamentos.csv", (CashPilotStore store) =>
{
    var body = Encoding.UTF8.GetBytes(TransactionExport.ToCsv(store.GetAll()));
    var bytes = Encoding.UTF8.GetPreamble().Concat(body).ToArray();   // BOM: Excel reads the accents right
    return Results.File(bytes, "text/csv; charset=utf-8", $"lancamentos-{DateTime.Now:yyyyMMdd}.csv");
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
