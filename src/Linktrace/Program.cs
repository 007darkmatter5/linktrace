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

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
