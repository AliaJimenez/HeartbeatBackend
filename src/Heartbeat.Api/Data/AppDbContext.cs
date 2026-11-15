using Heartbeat.Api.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<Astronaut> Astronauts => Set<Astronaut>();
    public DbSet<CheckUp> CheckUps => Set<CheckUp>();
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<RadiationDose> RadiationDoses => Set<RadiationDose>();
    public DbSet<EnvironmentReading> EnvironmentReadings => Set<EnvironmentReading>();
    public DbSet<WatchDevice> WatchDevices => Set<WatchDevice>();
    public DbSet<PortalUser> PortalUsers => Set<PortalUser>();
    public new DbSet<Role> Roles => Set<Role>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RolePermission>().HasKey(rp => new { rp.RoleId, rp.Permission });

        builder.Entity<Alert>().HasIndex(a => a.CheckUpId).IsUnique();

        builder.Entity<Astronaut>()
            .HasOne(a => a.Mission)
            .WithMany(m => m.Astronauts)
            .HasForeignKey(a => a.MissionId);

        builder.Entity<PortalUser>()
            .HasOne(p => p.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(p => p.RoleId);

        builder.Entity<RolePermission>()
            .HasOne(rp => rp.Role)
            .WithMany(r => r.Permissions)
            .HasForeignKey(rp => rp.RoleId);

        builder.Entity<CheckUp>()
            .HasOne(c => c.Astronaut)
            .WithMany(a => a.CheckUps)
            .HasForeignKey(c => c.AstronautId);

        builder.Entity<Reading>()
            .Property(r => r.DeviceId)
            .HasMaxLength(Reading.DeviceIdMaxLength);

        builder.Entity<Reading>()
            .HasOne(r => r.CheckUp)
            .WithMany(c => c.Readings)
            .HasForeignKey(r => r.CheckUpId);

        builder.Entity<Alert>()
            .HasOne(a => a.Astronaut)
            .WithMany()
            .HasForeignKey(a => a.AstronautId);

        builder.Entity<Alert>()
            .HasOne(a => a.CheckUp)
            .WithOne(c => c.Alert)
            .HasForeignKey<Alert>(a => a.CheckUpId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<RadiationDose>(e =>
        {
            e.Property(d => d.Note).HasMaxLength(Heartbeat.Shared.RadiationDoses.NoteMaxLength);
            e.HasIndex(d => d.AstronautId);
            e.HasOne(d => d.Astronaut).WithMany().HasForeignKey(d => d.AstronautId);
        });

        builder.Entity<EnvironmentReading>(e =>
        {
            e.Property(r => r.IndicatorId).HasMaxLength(100);
            e.Property(r => r.Unit).HasMaxLength(32);
            e.Property(r => r.Note).HasMaxLength(200);
            e.HasIndex(r => r.AstronautId);
            e.HasIndex(r => r.MissionId);
            e.HasIndex(r => r.RecordedAt);
            e.HasOne(r => r.Astronaut).WithMany().HasForeignKey(r => r.AstronautId).IsRequired(false);
            e.HasOne(r => r.Mission).WithMany().HasForeignKey(r => r.MissionId).IsRequired(false);
        });

        builder.Entity<WatchDevice>(e =>
        {
            e.Property(w => w.DeviceId).HasMaxLength(WatchDevice.DeviceIdMaxLength);
            e.Property(w => w.KeyHash).HasMaxLength(64);
            e.Property(w => w.Label).HasMaxLength(WatchDevice.LabelMaxLength);
            e.HasIndex(w => w.DeviceId).IsUnique();
            e.HasIndex(w => w.KeyHash).IsUnique();
            e.HasIndex(w => w.AstronautId);
            e.HasOne(w => w.Astronaut).WithMany().HasForeignKey(w => w.AstronautId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Message>()
            .HasOne(m => m.Astronaut)
            .WithMany(a => a.Messages)
            .HasForeignKey(m => m.AstronautId);

        builder.Entity<Message>()
            .HasOne(m => m.CheckUp)
            .WithMany()
            .HasForeignKey(m => m.CheckUpId)
            .IsRequired(false);
    }
}