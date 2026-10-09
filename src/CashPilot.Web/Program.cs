using System.Net;
using System.Text;
using CashPilot.Contracts;
using CashPilot.Domain.Reports;
using CashPilot.Infrastructure.Backup;
using CashPilot.Infrastructure.Mobile;
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

// The web pages and the older /api/months JSON have no login: they only answer to this computer. The phone reaches
// the app over the home network, and only through /api/v1 (which needs the API key). Set CashPilot:AllowRemoteUi=true
// to open the pages to the whole network on purpose.
var allowRemoteUi = app.Configuration.GetValue<bool>("CashPilot:AllowRemoteUi");
app.Use(async (context, next) =>
{
    var remote = context.Connection.RemoteIpAddress;
    var local = remote is null || IPAddress.IsLoopback(remote);
    if (!allowRemoteUi && !local && !context.Request.Path.StartsWithSegments("/api/v1"))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden: only this computer can open CashPilot's pages.");
        return;
    }
    await next();
});

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

// Phone API (.NET MAUI app): type entries on the home network, with an API key.
var apiKey = app.Configuration["CashPilot:ApiKey"];
var api = app.MapGroup("/api/v1");
api.AddEndpointFilter(async (context, next) =>
{
    if (string.IsNullOrEmpty(apiKey))
        return Results.Json(new ApiError("A API do celular está desligada: configure CashPilot:ApiKey."), statusCode: 503);
    if (!ApiKeyCheck.IsValid(apiKey, context.HttpContext.Request.Headers[ApiKeyCheck.HeaderName].ToString()))
        return Results.Json(new ApiError("Chave de API inválida."), statusCode: 401);
    return await next(context);
});

api.MapGet("/ping", () => Results.Ok(new { ok = true }));
api.MapGet("/lookups", (CashPilotStore store) => Results.Ok(MobileEntryService.Lookups(store)));
api.MapGet("/entries", (CashPilotStore store, int? limit) =>
    Results.Ok(MobileEntryService.Recent(store, limit ?? 30)));
api.MapPost("/entries", (NewEntryRequest request, CashPilotStore store) =>
{
    try
    {
        var response = MobileEntryService.Create(store, request);
        return response.AlreadyExisted ? Results.Ok(response) : Results.Created($"/api/v1/entries/{response.Id}", response);
    }
    catch (MobileRequestException ex)
    {
        return Results.BadRequest(new ApiError(ex.Message));
    }
});
api.MapDelete("/entries/{id:guid}", (Guid id, CashPilotStore store) =>
    MobileEntryService.DeleteManual(store, id)
        ? Results.NoContent()
        : Results.NotFound(new ApiError("Só lançamentos digitados à mão podem ser apagados pelo celular.")));

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
