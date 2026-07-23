using Microsoft.EntityFrameworkCore;
using XPostMonitor.Models;

namespace XPostMonitor.Data;

public sealed class AppDbContext : DbContext
{
    // Nhận cấu hình kết nối database từ dependency injection.
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<TelegramUser> TelegramUsers { get; set; } = null!;
    public DbSet<XAccount> XAccounts { get; set; } = null!;
    public DbSet<WatchlistEntry> WatchlistEntries { get; set; } = null!;
    public DbSet<XSubscription> XSubscriptions { get; set; } = null!;

    // Khai báo khóa chính, độ dài cột và quan hệ giữa các bảng.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TelegramUser>(entity =>
        {
            entity.HasKey(x => x.ChatId);
            entity.Property(x => x.ChatId).ValueGeneratedNever();
            entity.Property(x => x.Username).HasMaxLength(64);
            entity.Property(x => x.DisplayName).HasMaxLength(256);
        });

        modelBuilder.Entity<XAccount>(entity =>
        {
            entity.HasKey(x => x.XUserId);
            entity.Property(x => x.XUserId).HasMaxLength(20);
            entity.Property(x => x.Username).HasMaxLength(50);
            entity.Property(x => x.DisplayName).HasMaxLength(100);
            entity.Property(x => x.LastPostId).HasMaxLength(20);
            entity.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<WatchlistEntry>(entity =>
        {
            entity.HasKey(x => new { x.ChatId, x.XUserId });
            entity.Property(x => x.XUserId).HasMaxLength(20);
            entity.HasOne(x => x.TelegramUser).WithMany(x => x.Watchlist).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.XAccount).WithMany(x => x.Watchers).HasForeignKey(x => x.XUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<XSubscription>(entity =>
        {
            entity.HasKey(x => new { x.XUserId, x.EventType });
            entity.Property(x => x.XUserId).HasMaxLength(20);
            entity.Property(x => x.EventType).HasMaxLength(64);
            entity.Property(x => x.RemoteSubscriptionId).HasMaxLength(20);
            entity.HasOne(x => x.XAccount).WithMany(x => x.Subscriptions).HasForeignKey(x => x.XUserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
