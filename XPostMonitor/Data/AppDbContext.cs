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
    public DbSet<UserTradingSettings> UserTradingSettings { get; set; } = null!;
    public DbSet<TradingWorker> TradingWorkers { get; set; } = null!;
    public DbSet<UserChainTradingSettings> UserChainTradingSettings { get; set; } = null!;
    public DbSet<TakeProfitSetting> TakeProfitSettings { get; set; } = null!;
    public DbSet<AutoTrade> AutoTrades { get; set; } = null!;
    public DbSet<AutoTradeOrder> AutoTradeOrders { get; set; } = null!;
    public DbSet<ArcBridgeWallet> ArcBridgeWallets { get; set; } = null!;
    public DbSet<ArcBridgeTransfer> ArcBridgeTransfers { get; set; } = null!;

    // Khai báo khóa chính, độ dài cột và quan hệ giữa các bảng.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TelegramUser>(entity =>
        {
            entity.HasKey(x => x.ChatId);
            entity.Property(x => x.ChatId).ValueGeneratedNever();
            entity.Property(x => x.Username).HasMaxLength(64);
            entity.Property(x => x.DisplayName).HasMaxLength(256);
            entity.Property(x => x.LanguageCode).HasMaxLength(2).HasDefaultValue("en");
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
            entity.Property(x => x.TokenChain).HasMaxLength(20);
            entity.Property(x => x.TokenDex).HasMaxLength(20);
            entity.Property(x => x.TokenAnchor).HasMaxLength(20);
            entity.Property(x => x.CreatorTaxPercent).HasDefaultValue(0);
            entity.Property(x => x.EnableAutoTrading).HasDefaultValue(false);
            entity.Property(x => x.ParallelTokenCount).HasDefaultValue(1);
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

        modelBuilder.Entity<UserTradingSettings>(entity =>
        {
            entity.HasKey(x => x.ChatId);
            entity.Property(x => x.ChatId).ValueGeneratedNever();
            entity.HasOne(x => x.TelegramUser).WithOne(x => x.TradingSettings).HasForeignKey<UserTradingSettings>(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TradingWorker>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EvmWalletAddress).HasMaxLength(42);
            entity.Property(x => x.IsEnabled).HasDefaultValue(true);
            entity.HasIndex(x => new { x.ChatId, x.SlotNumber }).IsUnique();
            entity.HasOne(x => x.TelegramUser).WithMany(x => x.TradingWorkers)
                .HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArcBridgeWallet>(entity =>
        {
            entity.HasKey(x => x.ChatId);
            entity.Property(x => x.ChatId).ValueGeneratedNever();
            entity.Property(x => x.WalletAddress).HasMaxLength(42);
            entity.HasOne(x => x.TelegramUser).WithOne(x => x.ArcBridgeWallet)
                .HasForeignKey<ArcBridgeWallet>(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArcBridgeTransfer>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.WalletAddress).HasMaxLength(42);
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.GrossAmountAtomic).HasMaxLength(78);
            entity.Property(x => x.ReceiveAmountAtomic).HasMaxLength(78);
            entity.Property(x => x.MaxFeeAtomic).HasMaxLength(78);
            entity.Property(x => x.DepositTransactionHash).HasMaxLength(66);
            entity.Property(x => x.DepositBlockNumber).HasMaxLength(78);
            entity.Property(x => x.MintTransactionHash).HasMaxLength(66);
            entity.Property(x => x.ErrorMessage).HasMaxLength(500);
            entity.HasIndex(x => new { x.ChatId, x.Status });
            entity.HasOne(x => x.Wallet).WithMany(x => x.Transfers)
                .HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserChainTradingSettings>(entity =>
        {
            entity.HasKey(x => new { x.ChatId, x.Chain });
            entity.Property(x => x.Chain).HasMaxLength(20);
            entity.Property(x => x.BuyAmount).HasColumnType("decimal(18,8)");
            entity.Property(x => x.SlippagePercent).HasColumnType("decimal(5,2)");
            entity.HasOne(x => x.TelegramUser).WithMany(x => x.ChainTradingSettings).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TakeProfitSetting>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProfitPercent).HasColumnType("decimal(10,2)");
            entity.Property(x => x.SellPercent).HasColumnType("decimal(5,2)");
            entity.HasIndex(x => new { x.ChatId, x.ProfitPercent }).IsUnique();
            entity.HasOne(x => x.TelegramUser).WithMany(x => x.TakeProfitSettings).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AutoTrade>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PostId).HasMaxLength(20);
            entity.Property(x => x.Chain).HasMaxLength(20);
            entity.Property(x => x.TokenAddress).HasMaxLength(64);
            entity.Property(x => x.TokenName).HasMaxLength(100);
            entity.Property(x => x.TokenSymbol).HasMaxLength(50);
            entity.Property(x => x.WalletAddress).HasMaxLength(64);
            entity.Property(x => x.QuoteTokenAddress).HasMaxLength(64);
            entity.Property(x => x.EntryPrice).HasColumnType("decimal(28,20)");
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.ErrorMessage).HasMaxLength(500);
            entity.HasIndex(x => x.Status);
            entity.HasOne(x => x.TelegramUser).WithMany(x => x.AutoTrades).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.TradingWorker).WithMany(x => x.AutoTrades)
                .HasForeignKey(x => x.TradingWorkerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AutoTradeOrder>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.GmgnOrderId).HasMaxLength(64);
            entity.Property(x => x.ProfitPercent).HasColumnType("decimal(10,2)");
            entity.Property(x => x.SellPercent).HasColumnType("decimal(5,2)");
            entity.Property(x => x.TargetPrice).HasColumnType("decimal(28,20)");
            entity.Property(x => x.Status).HasMaxLength(20);
            entity.Property(x => x.TransactionHash).HasMaxLength(100);
            entity.Property(x => x.RealizedProfitUsd).HasColumnType("decimal(18,8)");
            entity.HasIndex(x => x.GmgnOrderId).IsUnique();
            entity.HasOne(x => x.AutoTrade).WithMany(x => x.Orders).HasForeignKey(x => x.AutoTradeId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
