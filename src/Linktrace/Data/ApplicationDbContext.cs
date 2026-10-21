using Linktrace.Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Linktrace.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<Rack> Racks => Set<Rack>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Port> Ports => Set<Port>();
    public DbSet<Cable> Cables => Set<Cable>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<FaceplateTemplate> FaceplateTemplates => Set<FaceplateTemplate>();
    public DbSet<PortMapView> PortMapViews => Set<PortMapView>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>().Property(u => u.ThemePreference).HasConversion<string>().HasMaxLength(10);

        builder.Entity<Site>().HasIndex(s => s.Name).IsUnique();

        builder.Entity<Room>(e =>
        {
            e.HasOne(r => r.Site).WithMany(s => s.Rooms).HasForeignKey(r => r.SiteId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.SiteId, r.Name }).IsUnique();
        });

        builder.Entity<Rack>(e =>
        {
            e.HasOne(r => r.Room).WithMany(r => r.Racks).HasForeignKey(r => r.RoomId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.RoomId, r.Name }).IsUnique();
        });

        builder.Entity<Device>(e =>
        {
            e.HasOne(d => d.Room).WithMany(r => r.Devices).HasForeignKey(d => d.RoomId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Rack).WithMany(r => r.Devices).HasForeignKey(d => d.RackId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(d => d.Name);
            e.Property(d => d.Kind).HasConversion<string>().HasMaxLength(30);
            e.Property(d => d.FaceplateNumbering).HasConversion<string>().HasMaxLength(30);
            e.HasOne(d => d.FaceplateTemplate).WithMany(t => t.Devices).HasForeignKey(d => d.FaceplateTemplateId).OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Port>(e =>
        {
            e.HasOne(p => p.Device).WithMany(d => d.Ports).HasForeignKey(p => p.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Cable).WithMany(c => c.Ports).HasForeignKey(p => p.CableId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(p => new { p.DeviceId, p.Face, p.Name }).IsUnique();
            e.HasIndex(p => p.PairedPortId);
            e.Property(p => p.Face).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.MediaType).HasConversion<string>().HasMaxLength(20);
        });

        builder.Entity<Cable>(e =>
        {
            e.HasIndex(c => c.Label);
            e.Property(c => c.CableType).HasConversion<string>().HasMaxLength(30);
            e.Property(c => c.LengthMeters).HasConversion<double?>();
        });

        builder.Entity<FaceplateTemplate>(e =>
        {
            e.HasIndex(t => t.Name).IsUnique();
            e.HasIndex(t => t.Model);
            // Cell contents are stored as JSON on the template row.
            e.OwnsMany(t => t.Placements, b => b.ToJson());
            e.OwnsMany(t => t.Labels, b => b.ToJson());
        });

        // DeviceIds is stored as a JSON array on the row.
        builder.Entity<PortMapView>().HasIndex(v => v.Name).IsUnique();

        builder.Entity<AuditEntry>(e =>
        {
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => new { a.EntityType, a.EntityId });
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampEntities();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampEntities();
        return base.SaveChanges();
    }

    /// <summary>Name of the user making the change; set by the service layer.</summary>
    public string? CurrentUserName { get; set; }

    private void StampEntities()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedUtc = now;
                entry.Entity.UpdatedUtc = now;
                entry.Entity.UpdatedBy = CurrentUserName;
                entry.Entity.Version = Guid.NewGuid();
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedUtc = now;
                entry.Entity.UpdatedBy = CurrentUserName;
                entry.Entity.Version = Guid.NewGuid();
            }
        }
    }
}
