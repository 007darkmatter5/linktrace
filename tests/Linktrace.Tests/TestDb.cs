using Linktrace.Data;
using Linktrace.Data.Entities;
using Linktrace.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Tests;

public class FakeUser : ICurrentUser
{
    public bool CanEdit { get; set; } = true;
    public bool IsAdmin { get; set; } = true;
    public string Name { get; set; } = "tester@example.com";

    public Task<string> GetUserNameAsync() => Task.FromResult(Name);
    public Task<bool> CanEditAsync() => Task.FromResult(CanEdit);
    public Task<bool> IsAdminAsync() => Task.FromResult(IsAdmin);
}

/// <summary>An in-memory SQLite database plus the services under test.</summary>
public sealed class TestDb : IDbContextFactory<ApplicationDbContext>, IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public FakeUser User { get; } = new();
    public LocationService Locations { get; }
    public DeviceService Devices { get; }
    public CableService Cables { get; }
    public TraceService Tracer { get; }
    public FaceplateService Faceplates { get; }

    public TestDb()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;
        // EnsureCreated rather than Migrate: the app's migration includes Identity schema v3
        // (configured via AddIdentityCore), which this bare context doesn't know about.
        using (var ctx = CreateDbContext()) ctx.Database.EnsureCreated();

        Locations = new LocationService(this, User);
        Devices = new DeviceService(this, User);
        Cables = new CableService(this, User);
        Tracer = new TraceService(this);
        Faceplates = new FaceplateService(this, User);
    }

    public ApplicationDbContext CreateDbContext() => new(_options);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    // ---- Helpers for building test networks ----

    public async Task<Room> CreateRoomAsync()
    {
        var site = await Locations.SaveSiteAsync(new Site { Name = "HQ" });
        return await Locations.SaveRoomAsync(new Room { SiteId = site.Id, Name = "IDF-1" });
    }

    public async Task<Device> CreateDeviceAsync(Room room, string name, DeviceKind kind, PortGenerationRequest? ports = null)
    {
        var device = await Devices.SaveAsync(new Device { Name = name, Kind = kind, RoomId = room.Id });
        if (ports is not null) await Devices.AddPortsAsync(device.Id, ports);
        return device;
    }

    public async Task<Port> PortAsync(Device device, string name, PortFace face = PortFace.Standard)
    {
        var ports = await Devices.GetPortsAsync(device.Id);
        return ports.Single(p => p.Name == name && p.Face == face);
    }
}
