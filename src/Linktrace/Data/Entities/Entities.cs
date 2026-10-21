using System.ComponentModel.DataAnnotations;

namespace Linktrace.Data.Entities;

/// <summary>
/// Base for all tracked records. <see cref="Version"/> is a concurrency token:
/// it changes on every save, so two people editing the same record can't silently
/// overwrite each other.
/// </summary>
public abstract class EntityBase
{
    public int Id { get; set; }

    [ConcurrencyCheck]
    public Guid Version { get; set; } = Guid.NewGuid();

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    [MaxLength(256)] public string? UpdatedBy { get; set; }

    public EntityBase ShallowClone() => (EntityBase)MemberwiseClone();
}

public class Site : EntityBase
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }

    public List<Room> Rooms { get; set; } = [];
}

/// <summary>A room, closet, office or any other place inside a site.</summary>
public class Room : EntityBase
{
    public int SiteId { get; set; }
    public Site Site { get; set; } = null!;

    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(50)] public string? Floor { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }

    public List<Rack> Racks { get; set; } = [];
    public List<Device> Devices { get; set; } = [];
}

public class Rack : EntityBase
{
    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [Range(1, 60)] public int HeightU { get; set; } = 42;
    [MaxLength(2000)] public string? Notes { get; set; }

    public List<Device> Devices { get; set; } = [];
}

public enum DeviceKind
{
    Switch,
    Router,
    Firewall,
    PatchPanel,
    Server,
    AccessPoint,
    WallJack,
    Workstation,
    Phone,
    Printer,
    Camera,
    MediaConverter,
    Other
}

public class Device : EntityBase
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; } = DeviceKind.Switch;

    [MaxLength(100)] public string? Manufacturer { get; set; }
    [MaxLength(100)] public string? Model { get; set; }
    [MaxLength(100)] public string? SerialNumber { get; set; }
    [MaxLength(100)] public string? ManagementAddress { get; set; }

    public int RoomId { get; set; }
    public Room Room { get; set; } = null!;

    /// <summary>Optional: devices like wall jacks or workstations aren't in a rack.</summary>
    public int? RackId { get; set; }
    public Rack? Rack { get; set; }

    /// <summary>Lowest rack unit the device occupies (1 = bottom).</summary>
    public int? RackUnit { get; set; }
    [Range(1, 60)] public int HeightU { get; set; } = 1;

    /// <summary>Rows of ports in the automatic faceplate view (0 = choose automatically).</summary>
    [Range(0, 8)] public int FaceplateRows { get; set; }
    public PortNumbering FaceplateNumbering { get; set; } = PortNumbering.Auto;

    /// <summary>Hand-built grid layout; when null the faceplate is laid out automatically.</summary>
    public int? FaceplateTemplateId { get; set; }
    public FaceplateTemplate? FaceplateTemplate { get; set; }

    [MaxLength(2000)] public string? Notes { get; set; }

    public List<Port> Ports { get; set; } = [];
}

/// <summary>
/// A hand-built faceplate: a grid of cells, with ports (matched by name and side) and text labels
/// snapped to cells. Shared by every device that uses it, e.g. all devices of one model.
/// </summary>
public class FaceplateTemplate : EntityBase
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>Model this layout is for; new devices with this model pick it up automatically.</summary>
    [MaxLength(100)] public string? Model { get; set; }
    [Range(1, FaceplateGrid.MaxColumns)] public int Columns { get; set; } = 24;
    [Range(1, FaceplateGrid.MaxRows)] public int Rows { get; set; } = 2;

    public List<FaceplatePlacement> Placements { get; set; } = [];
    public List<FaceplateLabel> Labels { get; set; } = [];

    public List<Device> Devices { get; set; } = [];
}

/// <summary>A saved port map: which devices to show, top to bottom, and the display options.</summary>
public class PortMapView : EntityBase
{
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    [MaxLength(2000)] public string? Notes { get; set; }
    /// <summary>Device ids in display order (top first). Deleted devices are skipped when shown.</summary>
    public List<int> DeviceIds { get; set; } = [];
    public bool FollowPassThrough { get; set; } = true;
    public bool ShowExternal { get; set; } = true;
}

public static class FaceplateGrid
{
    public const int MaxColumns = 64;
    public const int MaxRows = 12;
}

/// <summary>A port placed in a grid cell (0-based column and row).</summary>
public class FaceplatePlacement
{
    public string PortName { get; set; } = "";
    public PortFace Face { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
}

/// <summary>Text spanning one or more cells in a row, e.g. "WAN" or "SFP+".</summary>
public class FaceplateLabel
{
    public string Text { get; set; } = "";
    public int Col { get; set; }
    public int Row { get; set; }
    public int Span { get; set; } = 1;
}

/// <summary>How ports are numbered across the faceplate grid.</summary>
public enum PortNumbering
{
    /// <summary>Switches/routers: odd on top, even below. Everything else: row by row.</summary>
    Auto,
    /// <summary>1, 2, 3… left to right, then the next row (typical patch panel).</summary>
    RowByRow,
    /// <summary>1 top, 2 below it, 3 top of the next column… (typical switch).</summary>
    OddTopEvenBottom
}

public enum PortFace
{
    /// <summary>Normal device port (switch, server NIC, etc.).</summary>
    Standard,
    /// <summary>Front of a patch panel / pass-through device.</summary>
    Front,
    /// <summary>Back (punch-down / termination side) of a patch panel.</summary>
    Rear
}

public enum MediaType
{
    RJ45,
    SFP,
    SFPPlus,
    SFP28,
    QSFP,
    LC,
    SC,
    ST,
    MPO,
    Coax,
    Console,
    Keystone,
    PunchDown,
    Other
}

public class Port : EntityBase
{
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;

    [Required, MaxLength(100)] public string Name { get; set; } = "";
    /// <summary>Sort position within the device.</summary>
    public int Position { get; set; }
    public PortFace Face { get; set; } = PortFace.Standard;
    public MediaType MediaType { get; set; } = MediaType.RJ45;
    [MaxLength(300)] public string? Description { get; set; }

    /// <summary>
    /// Internal pass-through partner on the same device (e.g. patch panel front 12 ↔ rear 12).
    /// Stored on both ports. Kept as a plain column (no FK) so a pair can be created in one step.
    /// </summary>
    public int? PairedPortId { get; set; }

    /// <summary>The cable plugged into this port. A port holds at most one cable.</summary>
    public int? CableId { get; set; }
    public Cable? Cable { get; set; }
}

public enum CableType
{
    Cat5e,
    Cat6,
    Cat6a,
    Cat8,
    FiberSingleMode,
    FiberMultiModeOM3,
    FiberMultiModeOM4,
    DAC,
    AOC,
    Coax,
    Console,
    Other
}

/// <summary>A physical cable joining exactly two ports.</summary>
public class Cable : EntityBase
{
    [MaxLength(100)] public string? Label { get; set; }
    public CableType CableType { get; set; } = CableType.Cat6;
    [MaxLength(50)] public string? Color { get; set; }
    public decimal? LengthMeters { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }

    public List<Port> Ports { get; set; } = [];
}

public class AuditEntry
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    [MaxLength(256)] public string UserName { get; set; } = "";
    [MaxLength(50)] public string Action { get; set; } = "";
    [MaxLength(50)] public string EntityType { get; set; } = "";
    public int? EntityId { get; set; }
    [MaxLength(1000)] public string Summary { get; set; } = "";
}
