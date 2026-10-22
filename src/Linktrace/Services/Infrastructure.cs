using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Editor = "Editor";
    public const string Viewer = "Viewer";

    public static readonly string[] All = [Admin, Editor, Viewer];
}

public static class AppPolicies
{
    public const string CanEdit = "CanEdit";
    public const string Admin = "Admin";
}

/// <summary>An error that is safe and useful to show to the user.</summary>
public class LinktraceException(string message) : Exception(message);

public class ConcurrencyConflictException() : LinktraceException(
    "Someone else changed this record while you were editing it. Reload the page and try again.");

public class NotFoundException(string what) : LinktraceException($"{what} was not found. It may have been deleted by someone else.");

public interface ICurrentUser
{
    Task<string> GetUserNameAsync();
    Task<bool> CanEditAsync();
    Task<bool> IsAdminAsync();
}

public class CurrentUser(AuthenticationStateProvider authProvider) : ICurrentUser
{
    public async Task<string> GetUserNameAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return state.User.Identity?.Name ?? "anonymous";
    }

    public async Task<bool> CanEditAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return state.User.IsInRole(AppRoles.Admin) || state.User.IsInRole(AppRoles.Editor);
    }

    public async Task<bool> IsAdminAsync()
    {
        var state = await authProvider.GetAuthenticationStateAsync();
        return state.User.IsInRole(AppRoles.Admin);
    }
}

/// <summary>
/// Shared plumbing for the data services. Each operation uses its own short-lived
/// DbContext, which is the recommended pattern for Blazor Server.
/// </summary>
public abstract class ServiceBase(IDbContextFactory<ApplicationDbContext> factory, ICurrentUser currentUser)
{
    protected ICurrentUser CurrentUser => currentUser;

    protected Task<ApplicationDbContext> ReadAsync() => factory.CreateDbContextAsync();

    protected async Task<ApplicationDbContext> WriteAsync()
    {
        if (!await currentUser.CanEditAsync())
            throw new LinktraceException("You don't have permission to make changes. Ask an admin for the Editor role.");

        var ctx = await factory.CreateDbContextAsync();
        ctx.CurrentUserName = await currentUser.GetUserNameAsync();
        return ctx;
    }

    protected static void Audit(ApplicationDbContext ctx, string action, string entityType, int? entityId, string summary)
    {
        ctx.AuditEntries.Add(new AuditEntry
        {
            TimestampUtc = DateTime.UtcNow,
            UserName = ctx.CurrentUserName ?? "system",
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Summary = summary.Length > 1000 ? summary[..1000] : summary
        });
    }

    protected static async Task SaveAsync(ApplicationDbContext ctx)
    {
        try
        {
            await ctx.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConcurrencyConflictException();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 } sql)
        {
            if (sql.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
                throw new LinktraceException("That name is already in use here. Pick a different one.");
            throw new LinktraceException("This change conflicts with related records (for example, something still depends on it).");
        }
    }

    /// <summary>Tell EF to only save if the record still has the version the user loaded.</summary>
    protected static void ExpectVersion<T>(ApplicationDbContext ctx, T entity, Guid version) where T : EntityBase
        => ctx.Entry(entity).Property(e => e.Version).OriginalValue = version;

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum MediaFamily { Copper, Fiber, Transceiver, Usb, Power, Coax, Console, Other }

public static class FileNames
{
    // Characters Windows, macOS or Linux reject in file names. A fixed list (not the server OS's own),
    // because downloads are saved on the user's computer, which may not be the same OS as the server.
    private static readonly char[] Unsafe = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Replaces characters that aren't allowed in file names with <paramref name="replacement"/>.</summary>
    public static string Safe(string name, char replacement = '-') =>
        new string(name.Select(c => Unsafe.Contains(c) || char.IsControl(c) ? replacement : c).ToArray()).Trim();
}

public static class Display
{
    public static string Face(PortFace face) => face switch
    {
        PortFace.Front => "front",
        PortFace.Rear => "rear",
        _ => ""
    };

    /// <summary>"PP-A › 12 (rear)"</summary>
    public static string Port(string deviceName, string portName, PortFace face)
        => face == PortFace.Standard ? $"{deviceName} › {portName}" : $"{deviceName} › {portName} ({Face(face)})";

    public static string Port(Port p) => Port(p.Device?.Name ?? "?", p.Name, p.Face);

    public static string PortOnly(Port p) => p.Face == PortFace.Standard ? p.Name : $"{p.Name} ({Face(p.Face)})";

    public static string Location(Device d)
    {
        var parts = new List<string>();
        if (d.Room?.Site is not null) parts.Add(d.Room.Site.Name);
        if (d.Room is not null) parts.Add(d.Room.Name);
        if (d.Rack is not null) parts.Add(d.RackUnit is int u ? $"{d.Rack.Name} U{u}" : d.Rack.Name);
        return string.Join(" / ", parts);
    }

    public static string Kind(DeviceKind kind) => kind switch
    {
        DeviceKind.PatchPanel => "Patch panel",
        DeviceKind.AccessPoint => "Access point",
        DeviceKind.WallJack => "Wall jack",
        DeviceKind.MediaConverter => "Media converter",
        _ => kind.ToString()
    };

    public static string Media(MediaType m) => m switch
    {
        MediaType.SFPPlus => "SFP+",
        MediaType.PunchDown => "Punch-down",
        MediaType.UsbC => "USB-C",
        MediaType.UsbA => "USB-A",
        MediaType.PowerNema515 => "Outlet (NEMA 5-15)",
        MediaType.PowerC13 => "Outlet (C13)",
        MediaType.PowerC19 => "Outlet (C19)",
        MediaType.PowerC14 => "Power inlet (C14)",
        MediaType.PowerC20 => "Power inlet (C20)",
        MediaType.PowerDc => "DC power",
        _ => m.ToString()
    };

    public static string CableKind(CableType t) => t switch
    {
        CableType.FiberSingleMode => "Fiber SM",
        CableType.FiberMultiModeOM3 => "Fiber MM OM3",
        CableType.FiberMultiModeOM4 => "Fiber MM OM4",
        CableType.Power => "Power cord",
        CableType.Usb => "USB",
        _ => t.ToString()
    };

    /// <summary>Broad family of a connector, used to warn about cabling unlike ports together.</summary>
    public static MediaFamily Family(MediaType m) => m switch
    {
        MediaType.RJ45 or MediaType.Keystone or MediaType.PunchDown => MediaFamily.Copper,
        MediaType.LC or MediaType.SC or MediaType.ST or MediaType.MPO => MediaFamily.Fiber,
        MediaType.SFP or MediaType.SFPPlus or MediaType.SFP28 or MediaType.QSFP => MediaFamily.Transceiver,
        MediaType.UsbC or MediaType.UsbA => MediaFamily.Usb,
        MediaType.PowerNema515 or MediaType.PowerC13 or MediaType.PowerC19 or MediaType.PowerC14
            or MediaType.PowerC20 or MediaType.PowerDc => MediaFamily.Power,
        MediaType.Coax => MediaFamily.Coax,
        MediaType.Console => MediaFamily.Console,
        _ => MediaFamily.Other
    };

    /// <summary>
    /// Whether two connectors plausibly cable together. Network ports (copper, fiber, transceiver cages) mix
    /// freely, since modules and media converters bridge them; USB, power, coax and console only match their own kind.
    /// </summary>
    public static bool CanConnect(MediaType a, MediaType b)
    {
        MediaFamily[] network = [MediaFamily.Copper, MediaFamily.Fiber, MediaFamily.Transceiver, MediaFamily.Other];
        var (fa, fb) = (Family(a), Family(b));
        return fa == fb || (network.Contains(fa) && network.Contains(fb)) || fa == MediaFamily.Other || fb == MediaFamily.Other;
    }

    /// <summary>The cable type to suggest when connecting a port of this kind.</summary>
    public static CableType SuggestedCable(MediaType m) => m switch
    {
        MediaType.LC or MediaType.SC or MediaType.ST or MediaType.MPO => CableType.FiberMultiModeOM4,
        MediaType.SFPPlus or MediaType.SFP28 or MediaType.QSFP => CableType.DAC,
        MediaType.Coax => CableType.Coax,
        MediaType.Console => CableType.Console,
        MediaType.UsbC or MediaType.UsbA => CableType.Usb,
        _ when Family(m) == MediaFamily.Power => CableType.Power,
        _ => CableType.Cat6
    };

    /// <summary>A Material icon for connectors that look nothing like a network port (USB, power), else null.</summary>
    public static string? MediaIcon(MediaType m) => Family(m) switch
    {
        MediaFamily.Usb => MudBlazor.Icons.Material.Filled.Usb,
        MediaFamily.Power => MudBlazor.Icons.Material.Filled.Power,
        _ => null
    };

    public static string KindIcon(DeviceKind kind) => kind switch
    {
        DeviceKind.Switch => MudBlazor.Icons.Material.Filled.Hub,
        DeviceKind.Router => MudBlazor.Icons.Material.Filled.Router,
        DeviceKind.Firewall => MudBlazor.Icons.Material.Filled.Security,
        DeviceKind.PatchPanel => MudBlazor.Icons.Material.Filled.ViewWeek,
        DeviceKind.Server => MudBlazor.Icons.Material.Filled.Dns,
        DeviceKind.AccessPoint => MudBlazor.Icons.Material.Filled.Wifi,
        DeviceKind.WallJack => MudBlazor.Icons.Material.Filled.SettingsInputComponent,
        DeviceKind.Workstation => MudBlazor.Icons.Material.Filled.Computer,
        DeviceKind.Phone => MudBlazor.Icons.Material.Filled.Phone,
        DeviceKind.Printer => MudBlazor.Icons.Material.Filled.Print,
        DeviceKind.Camera => MudBlazor.Icons.Material.Filled.Videocam,
        DeviceKind.MediaConverter => MudBlazor.Icons.Material.Filled.SwapHoriz,
        _ => MudBlazor.Icons.Material.Filled.DeviceUnknown
    };
}
