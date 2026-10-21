using Linktrace.Data;
using Linktrace.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Services;

/// <summary>
/// Sample network for trying the app out. Runs only with the --seed-demo flag and only on an empty database.
/// Includes a desk → wall jack → patch panel → access switch → fiber trunk → core switch path.
/// </summary>
public static class DemoData
{
    private sealed class SystemUser : ICurrentUser
    {
        public Task<string> GetUserNameAsync() => Task.FromResult("demo-seed");
        public Task<bool> CanEditAsync() => Task.FromResult(true);
        public Task<bool> IsAdminAsync() => Task.FromResult(true);
    }

    public static async Task SeedAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var check = await factory.CreateDbContextAsync();
        if (!await check.Sites.AnyAsync()) await SeedHeadOfficeAsync(factory);
        // Added separately so existing demo databases pick it up too.
        if (await check.Sites.AnyAsync(s => s.Name == "Head Office") && !await check.Devices.AnyAsync(d => d.Name == "FW-PRI"))
            await SeedWanEdgeAsync(factory);
    }

    /// <summary>
    /// A WAN edge rack: three internet circuits, each feeding both firewalls of an HA pair,
    /// with the firewalls uplinked to the core switch.
    /// </summary>
    private static async Task SeedWanEdgeAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        var user = new SystemUser();
        var loc = new LocationService(factory, user);
        var dev = new DeviceService(factory, user);
        var cab = new CableService(factory, user);

        await using var ctx = await factory.CreateDbContextAsync();
        var serverRoom = await ctx.Rooms.FirstAsync(r => r.Name == "Server Room");
        var core = await ctx.Devices.FirstAsync(d => d.Name == "CORE-SW01");
        var rack = await loc.SaveRackAsync(new Rack { RoomId = serverRoom.Id, Name = "EDGE-R1", HeightU = 24, Notes = "WAN edge" });

        async Task<Device> Add(string name, DeviceKind kind, int u, string? model, params PortGenerationRequest[] ports)
        {
            var d = await dev.SaveAsync(new Device { Name = name, Kind = kind, Model = model, RoomId = serverRoom.Id, RackId = rack.Id, RackUnit = u });
            foreach (var p in ports) await dev.AddPortsAsync(d.Id, p);
            return d;
        }
        PortGenerationRequest Lan(int n) => new() { Prefix = "lan", Count = n };
        PortGenerationRequest Wan() => new() { Prefix = "wan", Count = 1 };

        var sp = await Add("SP-WAN", DeviceKind.Router, 22, "Spectrum circuit", Lan(2), Wan());
        var sl = await Add("SL-WAN", DeviceKind.Router, 20, "Starlink", Lan(2), Wan());
        var att = await Add("ATT-WAN", DeviceKind.Router, 18, "AT&T fiber", Lan(3), Wan());
        var pri = await Add("FW-PRI", DeviceKind.Firewall, 14, "HA primary", new PortGenerationRequest { Prefix = "port", Count = 8 });
        var sec = await Add("FW-SEC", DeviceKind.Firewall, 12, "HA secondary", new PortGenerationRequest { Prefix = "port", Count = 8 });

        async Task<int> P(Device d, string name) => (await dev.GetPortsAsync(d.Id)).Single(p => p.Name == name).Id;
        async Task Link(int a, int b, string color, string? notes = null) =>
            await cab.ConnectAsync(a, b, new Cable { Label = await cab.SuggestLabelAsync(), CableType = CableType.Cat6, Color = color, LengthMeters = 1, Notes = notes });

        // Each circuit feeds both firewalls.
        await Link(await P(sp, "lan1"), await P(pri, "port1"), "Blue");
        await Link(await P(sp, "lan2"), await P(sec, "port1"), "Blue");
        await Link(await P(sl, "lan1"), await P(pri, "port2"), "Purple");
        await Link(await P(sl, "lan2"), await P(sec, "port2"), "Purple");
        await Link(await P(att, "lan1"), await P(pri, "port3"), "Orange");
        await Link(await P(att, "lan2"), await P(sec, "port3"), "Orange");
        // HA heartbeat and uplinks to the core.
        await Link(await P(pri, "port8"), await P(sec, "port8"), "Red", "HA heartbeat");
        await Link(await P(pri, "port7"), await P(core, "Gi1/0/9"), "Green", "Inside");
        await Link(await P(sec, "port7"), await P(core, "Gi1/0/12"), "Green", "Inside");
    }

    private static async Task SeedHeadOfficeAsync(IDbContextFactory<ApplicationDbContext> factory)
    {
        var user = new SystemUser();
        var loc = new LocationService(factory, user);
        var dev = new DeviceService(factory, user);
        var cab = new CableService(factory, user);

        var site = await loc.SaveSiteAsync(new Site { Name = "Head Office", Address = "1 Main Street" });
        var serverRoom = await loc.SaveRoomAsync(new Room { SiteId = site.Id, Name = "Server Room", Floor = "1" });
        var idf = await loc.SaveRoomAsync(new Room { SiteId = site.Id, Name = "IDF-2", Floor = "2" });
        var office = await loc.SaveRoomAsync(new Room { SiteId = site.Id, Name = "Office 2.14", Floor = "2" });
        var r1 = await loc.SaveRackAsync(new Rack { RoomId = serverRoom.Id, Name = "SR-R1", HeightU = 42 });
        var r2 = await loc.SaveRackAsync(new Rack { RoomId = idf.Id, Name = "IDF2-R1", HeightU = 24 });

        async Task<Device> Add(string name, DeviceKind kind, Room room, Rack? rack, int? u, int height, params PortGenerationRequest[] ports)
        {
            var d = await dev.SaveAsync(new Device { Name = name, Kind = kind, RoomId = room.Id, RackId = rack?.Id, RackUnit = u, HeightU = height });
            foreach (var p in ports) await dev.AddPortsAsync(d.Id, p);
            return d;
        }

        var fpSr = await Add("FP-SR-A", DeviceKind.PatchPanel, serverRoom, r1, 42, 1,
            new PortGenerationRequest { Count = 12, MediaType = MediaType.LC, RearMediaType = MediaType.LC, FrontAndRearPairs = true });
        var core = await Add("CORE-SW01", DeviceKind.Switch, serverRoom, r1, 40, 1,
            new PortGenerationRequest { Prefix = "Gi1/0/", Count = 24 }, new PortGenerationRequest { Prefix = "Te1/1/", Count = 4, MediaType = MediaType.SFPPlus });
        var fw = await Add("FW01", DeviceKind.Firewall, serverRoom, r1, 38, 1, new PortGenerationRequest { Prefix = "port", Count = 8 });
        var srv = await Add("SRV-01", DeviceKind.Server, serverRoom, r1, 20, 2, new PortGenerationRequest { Prefix = "eth", Start = 0, Count = 2 });

        var fpIdf = await Add("FP-IDF2-A", DeviceKind.PatchPanel, idf, r2, 24, 1,
            new PortGenerationRequest { Count = 12, MediaType = MediaType.LC, RearMediaType = MediaType.LC, FrontAndRearPairs = true });
        var ppIdf = await Add("PP-IDF2-A", DeviceKind.PatchPanel, idf, r2, 22, 1, new PortGenerationRequest { Count = 24, FrontAndRearPairs = true });
        var acc = await Add("IDF2-SW01", DeviceKind.Switch, idf, r2, 21, 1,
            new PortGenerationRequest { Prefix = "Gi1/0/", Count = 24 }, new PortGenerationRequest { Prefix = "Te1/1/", Count = 2, MediaType = MediaType.SFPPlus });

        var jack1 = await Add("J-2.14-01", DeviceKind.WallJack, office, null, null, 1, new PortGenerationRequest { Count = 1, MediaType = MediaType.Keystone, FrontAndRearPairs = true });
        var jack2 = await Add("J-2.14-02", DeviceKind.WallJack, office, null, null, 1, new PortGenerationRequest { Count = 1, MediaType = MediaType.Keystone, FrontAndRearPairs = true });
        var pc = await Add("PC-214A", DeviceKind.Workstation, office, null, null, 1, new PortGenerationRequest { Prefix = "eth", Start = 0, Count = 1 });
        var phone = await Add("PHONE-214A", DeviceKind.Phone, office, null, null, 1, new PortGenerationRequest { Prefix = "eth", Start = 0, Count = 1 });

        async Task<int> P(Device d, string name, PortFace face = PortFace.Standard)
            => (await dev.GetPortsAsync(d.Id)).Single(p => p.Name == name && p.Face == face).Id;

        async Task Link(int a, int b, CableType type, string? color, decimal? meters, string? notes = null)
            => await cab.ConnectAsync(a, b, new Cable { Label = await cab.SuggestLabelAsync(), CableType = type, Color = color, LengthMeters = meters, Notes = notes });

        // Fiber uplink: core → fiber panel → trunk → IDF fiber panel → access switch
        await Link(await P(core, "Te1/1/1"), await P(fpSr, "1", PortFace.Front), CableType.FiberMultiModeOM4, "Aqua", 2);
        await Link(await P(fpSr, "1", PortFace.Rear), await P(fpIdf, "1", PortFace.Rear), CableType.FiberMultiModeOM4, "Aqua", 60, "12-core trunk, core 1/2");
        await Link(await P(fpIdf, "1", PortFace.Front), await P(acc, "Te1/1/1"), CableType.FiberMultiModeOM4, "Aqua", 1.5m);

        // Desks: access switch → copper patch panel → horizontal run → wall jack → device
        await Link(await P(acc, "Gi1/0/1"), await P(ppIdf, "1", PortFace.Front), CableType.Cat6, "Blue", 0.5m);
        await Link(await P(ppIdf, "1", PortFace.Rear), await P(jack1, "1", PortFace.Rear), CableType.Cat6, null, 42, "Horizontal run");
        await Link(await P(jack1, "1", PortFace.Front), await P(pc, "eth0"), CableType.Cat6, "Gray", 3);

        await Link(await P(acc, "Gi1/0/2"), await P(ppIdf, "2", PortFace.Front), CableType.Cat6, "Yellow", 0.5m);
        await Link(await P(ppIdf, "2", PortFace.Rear), await P(jack2, "1", PortFace.Rear), CableType.Cat6, null, 42, "Horizontal run");
        await Link(await P(jack2, "1", PortFace.Front), await P(phone, "eth0"), CableType.Cat6, "Gray", 2);

        // Server room
        await Link(await P(core, "Gi1/0/1"), await P(fw, "port1"), CableType.Cat6a, "Red", 1);
        await Link(await P(core, "Gi1/0/10"), await P(srv, "eth0"), CableType.Cat6a, "Green", 2);
    }
}
