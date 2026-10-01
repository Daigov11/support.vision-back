using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Models;

namespace VisionSupport.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RemoteSession> RemoteSessions => Set<RemoteSession>();
    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PairingCode> PairingCodes => Set<PairingCode>();
    public DbSet<PairingCodeEvent> PairingCodeEvents => Set<PairingCodeEvent>();
    public DbSet<DeviceHealthSnapshot> DeviceHealthSnapshots => Set<DeviceHealthSnapshot>();
    public DbSet<DeviceDiagnosticEvent> DeviceDiagnosticEvents => Set<DeviceDiagnosticEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.Email).HasMaxLength(256).IsRequired();
            entity.Property(u => u.DisplayName).HasMaxLength(128).IsRequired();
            entity.Property(u => u.Role).HasConversion<string>().HasMaxLength(32);
            entity.Property(u => u.PasswordHash).IsRequired();
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasIndex(t => t.TokenHash).IsUnique();
            entity.HasIndex(t => t.UserId);
            entity.Property(t => t.TokenHash).HasMaxLength(128).IsRequired();

            entity.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.HasIndex(d => d.DeviceCode).IsUnique();
            entity.Property(d => d.DeviceCode).HasMaxLength(128).IsRequired();
            entity.Property(d => d.Name).HasMaxLength(128).IsRequired();
            entity.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(d => d.ConnectionId).HasMaxLength(128);
            entity.Property(d => d.PairingKey).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<RemoteSession>(entity =>
        {
            entity.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(s => s.EndReason).HasMaxLength(256);
            entity.Property(s => s.TechnicianConnectionId).HasMaxLength(128);

            entity.HasOne(s => s.Device)
                .WithMany(d => d.Sessions)
                .HasForeignKey(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(s => s.TechnicianUser)
                .WithMany()
                .HasForeignKey(s => s.TechnicianUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SessionEvent>(entity =>
        {
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);

            entity.HasOne(e => e.RemoteSession)
                .WithMany(s => s.Events)
                .HasForeignKey(e => e.RemoteSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PairingCode>(entity =>
        {
            entity.HasIndex(p => p.CodeHash).IsUnique();
            entity.Property(p => p.CodeHash).HasMaxLength(128).IsRequired();
            entity.Property(p => p.Label).HasMaxLength(128);

            entity.HasOne(p => p.CreatedByUser)
                .WithMany()
                .HasForeignKey(p => p.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(p => p.Device)
                .WithMany()
                .HasForeignKey(p => p.DeviceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<PairingCodeEvent>(entity =>
        {
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.RemoteIp).HasMaxLength(64);

            entity.HasOne(e => e.PairingCode)
                .WithMany()
                .HasForeignKey(e => e.PairingCodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<DeviceHealthSnapshot>(entity =>
        {
            entity.HasIndex(s => s.DeviceId).IsUnique();
            entity.Property(s => s.NetworkType).HasMaxLength(32);
            entity.Property(s => s.Manufacturer).HasMaxLength(64);
            entity.Property(s => s.Model).HasMaxLength(64);
            entity.Property(s => s.AndroidVersion).HasMaxLength(64);
            entity.Property(s => s.AppVersion).HasMaxLength(32);

            entity.HasOne(s => s.Device)
                .WithOne()
                .HasForeignKey<DeviceHealthSnapshot>(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceDiagnosticEvent>(entity =>
        {
            entity.HasIndex(e => e.DeviceId);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Message).HasMaxLength(500);
            entity.Property(e => e.Code).HasMaxLength(64);
            entity.Property(e => e.SourcePackage).HasMaxLength(150);
            entity.Property(e => e.SourceAppVersion).HasMaxLength(32);

            entity.HasOne(e => e.Device)
                .WithMany()
                .HasForeignKey(e => e.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
