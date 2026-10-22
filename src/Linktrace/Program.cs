using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using Linktrace.Components;
using Linktrace.Components.Account;
using Linktrace.Data;
using Linktrace.Services;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
// The factory gives each operation its own short-lived context (Blazor Server best practice).
// It also registers a scoped ApplicationDbContext, which Identity uses.
builder.Services.AddDbContextFactory<ApplicationDbContext>(options =>
    options.UseSqlite(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    // No email server is configured, so accounts work as soon as they're registered.
    options.SignIn.RequireConfirmedAccount = false;
    options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// Keys that sign the login cookie. In a container, DataProtection:KeysPath points at a persistent volume
// (/keys) so people stay signed in across restarts and image updates.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Linktrace");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddHealthChecks();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AppPolicies.CanEdit, p => p.RequireRole(AppRoles.Admin, AppRoles.Editor))
    .AddPolicy(AppPolicies.Admin, p => p.RequireRole(AppRoles.Admin));

// Linktrace services
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<LocationService>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<CableService>();
builder.Services.AddScoped<TraceService>();
builder.Services.AddScoped<PortMapService>();
builder.Services.AddScoped<FaceplateService>();
builder.Services.AddScoped<PortMapViewService>();
builder.Services.AddScoped<TransferService>();
builder.Services.AddScoped<UserPreferenceService>();
builder.Services.AddScoped<PortMapPdfService>();
builder.Services.AddScoped<DeviceTemplateService>();
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<UserAdminService>();

var app = builder.Build();

await DatabaseSetup.InitializeAsync(app.Services);
if (args.Contains("--seed-demo"))
    await DemoData.SeedAsync(app.Services);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Used by Docker/Unraid and the release smoke test to see that the app is up.
app.MapHealthChecks("/healthz");

// Download of all network data as one JSON file (see TransferService).
app.MapGet("/api/export", async (HttpContext http, TransferService transfer) =>
{
    var now = DateTime.UtcNow;
    var json = await transfer.ExportJsonAsync(http.User.Identity?.Name);
    return Results.File(json, "application/json", TransferService.FileName(now));
}).RequireAuthorization();

// Printable PDF of a port map, as currently laid out on screen (saved or not).
//   /api/portmap/pdf?d=1,2,3&c=0,1,0&s=0,0,2&via=true&ext=true&title=Edge&paper=Letter
app.MapGet("/api/portmap/pdf", async (HttpContext http, PortMapPdfService pdf,
    string? d, string? c, string? s, bool? via, bool? ext, string? title, PaperSize? paper) =>
{
    static List<int> Ints(string? csv) => (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0).ToList();

    var name = string.IsNullOrWhiteSpace(title) ? "Port map" : title.Trim();
    try
    {
        var bytes = await pdf.RenderAsync(new PortMapPdfRequest(name, Ints(d).Where(id => id > 0).ToList(), Ints(c),
            via ?? true, ext ?? true, paper ?? PaperSize.Letter, http.User.Identity?.Name, Ints(s)));
        // "inline" opens it in the browser's PDF viewer (print or save from there), with a sensible name if saved.
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(PortMapPdfService.FileName(name));
        http.Response.Headers.ContentDisposition = disposition.ToString();
        return Results.File(bytes, "application/pdf");
    }
    catch (LinktraceException ex)
    {
        return Results.BadRequest(ex.Message);
    }
}).RequireAuthorization();

// Download device templates as a shareable file: /api/templates/export?ids=1,2 (no ids = all of them).
app.MapGet("/api/templates/export", async (HttpContext http, DeviceTemplateService templates, string? ids) =>
{
    var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
    var all = await templates.GetAllAsync();
    var chosen = wanted.Count == 0 ? all : all.Where(t => wanted.Contains(t.Id)).ToList();
    if (chosen.Count == 0) return Results.NotFound("No templates to export.");
    var json = await templates.ExportJsonAsync(chosen.Select(t => t.Id).ToList(), http.User.Identity?.Name);
    return Results.File(json, "application/json", DeviceTemplateService.FileName(chosen));
}).RequireAuthorization();

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
