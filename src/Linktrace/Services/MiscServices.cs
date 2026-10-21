using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public enum SearchHitKind { Device, Port, Cable }

public record SearchHit(SearchHitKind Kind, string Title, string Subtitle, string Href);

public record DashboardStats(int Sites, int Rooms, int Racks, int Devices, int Ports, int ConnectedPorts, int Cables);

public class SearchService(IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task<List<SearchHit>> SearchAsync(string? text, int maxPerKind = 8)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var t = $"%{text.Trim()}%";
        await using var ctx = await factory.CreateDbContextAsync();

        var devices = await ctx.Devices.AsNoTracking()
            .Where(d => EF.Functions.Like(d.Name, t) || EF.Functions.Like(d.ManagementAddress ?? "", t)
                || EF.Functions.Like(d.SerialNumber ?? "", t) || EF.Functions.Like(d.Model ?? "", t))
            .OrderBy(d => d.Name).Take(maxPerKind)
            .Select(d => new { d.Id, d.Name, d.Kind, Site = d.Room.Site.Name, Room = d.Room.Name })
            .ToListAsync();

        var cables = await ctx.Cables.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Label ?? "", t))
            .OrderBy(c => c.Label).Take(maxPerKind)
            .Include(c => c.Ports).ThenInclude(p => p.Device)
            .ToListAsync();

        var ports = await ctx.Ports.AsNoTracking()
            .Where(p => EF.Functions.Like(p.Description ?? "", t)
                || EF.Functions.Like(p.Device.Name + " " + p.Name, t))
            .OrderBy(p => p.Device.Name).ThenBy(p => p.Position).Take(maxPerKind)
            .Include(p => p.Device)
            .ToListAsync();

        var hits = new List<SearchHit>();
        hits.AddRange(devices.Select(d => new SearchHit(SearchHitKind.Device, d.Name,
            $"{Display.Kind(d.Kind)} · {d.Site} / {d.Room}", $"devices/{d.Id}")));
        hits.AddRange(cables.Select(c => new SearchHit(SearchHitKind.Cable, c.Label ?? $"Cable #{c.Id}",
            string.Join(" ↔ ", c.Ports.Select(Display.Port)),
            c.Ports.FirstOrDefault() is { } p ? $"trace/{p.Id}" : "cables")));
        hits.AddRange(ports.Select(p => new SearchHit(SearchHitKind.Port, Display.Port(p),
            p.Description ?? (p.CableId is null ? "Not connected" : "Connected"), $"trace/{p.Id}")));
        return hits;
    }

    public async Task<DashboardStats> GetStatsAsync()
    {
        await using var ctx = await factory.CreateDbContextAsync();
        return new DashboardStats(
            await ctx.Sites.CountAsync(),
            await ctx.Rooms.CountAsync(),
            await ctx.Racks.CountAsync(),
            await ctx.Devices.CountAsync(),
            await ctx.Ports.CountAsync(),
            await ctx.Ports.CountAsync(p => p.CableId != null),
            await ctx.Cables.CountAsync());
    }
}

public class AuditService(IDbContextFactory<ApplicationDbContext> factory)
{
    public async Task<List<AuditEntry>> GetAsync(string? text = null, string? user = null, int max = 200)
    {
        await using var ctx = await factory.CreateDbContextAsync();
        var q = ctx.AuditEntries.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(text)) q = q.Where(a => EF.Functions.Like(a.Summary, $"%{text.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(user)) q = q.Where(a => a.UserName == user);
        return await q.OrderByDescending(a => a.Id).Take(max).ToListAsync();
    }
}

public record UserListItem(string Id, string UserName, string Role, bool LockedOut);

/// <summary>Admin-only user management. Uses a fresh scope per call so Identity data is never stale.</summary>
public class UserAdminService(IServiceScopeFactory scopes, ICurrentUser currentUser)
{
    private async Task RequireAdminAsync()
    {
        if (!await currentUser.IsAdminAsync()) throw new LinktraceException("Only admins can manage users.");
    }

    public async Task<List<UserListItem>> GetUsersAsync()
    {
        await RequireAdminAsync();
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var list = new List<UserListItem>();
        foreach (var u in await users.Users.OrderBy(u => u.UserName).ToListAsync())
        {
            var roles = await users.GetRolesAsync(u);
            var role = AppRoles.All.FirstOrDefault(roles.Contains) ?? AppRoles.Viewer;
            list.Add(new UserListItem(u.Id, u.UserName ?? u.Id, role, u.LockoutEnd > DateTimeOffset.UtcNow));
        }
        return list;
    }

    public async Task SetRoleAsync(string userId, string role)
    {
        await RequireAdminAsync();
        if (!AppRoles.All.Contains(role)) throw new LinktraceException("Unknown role.");
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId) ?? throw new NotFoundException("User");

        if (role != AppRoles.Admin && await users.IsInRoleAsync(user, AppRoles.Admin)
            && (await users.GetUsersInRoleAsync(AppRoles.Admin)).Count <= 1)
            throw new LinktraceException("You can't remove the last admin.");

        await users.RemoveFromRolesAsync(user, (await users.GetRolesAsync(user)).ToList());
        await users.AddToRoleAsync(user, role);
        await users.UpdateSecurityStampAsync(user); // forces the new role to apply on their next request
        await LogAsync(scope, "Updated", $"User '{user.UserName}' role set to {role}");
    }

    public async Task SetLockedAsync(string userId, bool locked)
    {
        await RequireAdminAsync();
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId) ?? throw new NotFoundException("User");
        if (user.UserName == await currentUser.GetUserNameAsync()) throw new LinktraceException("You can't lock your own account.");

        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        await users.UpdateSecurityStampAsync(user);
        await LogAsync(scope, "Updated", $"User '{user.UserName}' {(locked ? "locked" : "unlocked")}");
    }

    private async Task LogAsync(IServiceScope scope, string action, string summary)
    {
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ctx.AuditEntries.Add(new AuditEntry
        {
            TimestampUtc = DateTime.UtcNow,
            UserName = await currentUser.GetUserNameAsync(),
            Action = action,
            EntityType = "User",
            Summary = summary
        });
        await ctx.SaveChangesAsync();
    }
}

public static class DatabaseSetup
{
    /// <summary>Applies migrations, tunes SQLite for several concurrent users, and creates roles.</summary>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ctx.Database.GetConnectionString()).DataSource;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await ctx.Database.MigrateAsync();
        // WAL lets readers keep working while someone writes.
        await ctx.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in AppRoles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
    }

    /// <summary>First account becomes Admin; everyone after starts as Viewer until promoted.</summary>
    public static async Task AssignInitialRoleAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var anyAdmin = (await users.GetUsersInRoleAsync(AppRoles.Admin)).Count > 0;
        await users.AddToRoleAsync(user, anyAdmin ? AppRoles.Viewer : AppRoles.Admin);
    }
}
