using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nethereum.Hex.HexTypes;
using Nethereum.Signer;
using Nethereum.Util;
using Nethereum.Web3;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;

namespace XPostMonitor.Services.Wallets;

// Mot vi EVM dung chung cho cac EVM chain.
public sealed class EvmWalletService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IDataProtector protector;
    private readonly EvmNetworksOptions networkOptions;
    private readonly TradingWorkersOptions workerOptions;
    private readonly ConcurrentDictionary<(long ChatId, int SlotNumber), SemaphoreSlim> walletLocks = new();

    public EvmWalletService(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider,
        EvmNetworksOptions networkOptions, TradingWorkersOptions workerOptions)
    {
        this.scopeFactory = scopeFactory;
        this.networkOptions = networkOptions;
        this.workerOptions = workerOptions;
        protector = protectionProvider.CreateProtector("XPostMonitor.EvmWallet.v1");
    }

    public async Task<EvmWalletCreated> CreateAsync(long chatId, CancellationToken cancellationToken)
    {
        return await CreateAsync(chatId, 1, cancellationToken);
    }

    public async Task<EvmWalletCreated> CreateAsync(long chatId, int slotNumber,
        CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        SemaphoreSlim walletLock = walletLocks.GetOrAdd((chatId, slotNumber), _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateInternalAsync(chatId, slotNumber, cancellationToken);
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Nhập private key có sẵn, tự tính địa chỉ ví và mã hóa lại cho máy đang chạy bot.
    public async Task<EvmWalletCredentials> ImportAsync(long chatId, string privateKey,
        CancellationToken cancellationToken)
    {
        return await ImportAsync(chatId, 1, privateKey, cancellationToken);
    }

    public async Task<EvmWalletCredentials> ImportAsync(long chatId, int slotNumber, string privateKey,
        CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        SemaphoreSlim walletLock = walletLocks.GetOrAdd((chatId, slotNumber), _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await ImportInternalAsync(chatId, slotNumber, privateKey, cancellationToken);
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Xóa thông tin ví và GMGN của đúng Worker, nhưng giữ lại lịch sử giao dịch trong DB.
    public async Task<bool> DeleteAsync(long chatId, int slotNumber, CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        SemaphoreSlim walletLock = walletLocks.GetOrAdd((chatId, slotNumber), _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            TradingWorker? worker = await db.TradingWorkers
                .FirstOrDefaultAsync(item => item.ChatId == chatId && item.SlotNumber == slotNumber,
                    cancellationToken);
            if (worker == null)
            {
                return false;
            }

            worker.EvmWalletAddress = string.Empty;
            worker.EncryptedEvmPrivateKey = string.Empty;
            worker.EncryptedGmgnApiKey = string.Empty;
            worker.EncryptedGmgnPrivateKey = string.Empty;
            worker.UpdatedAtUtc = DateTime.UtcNow;

            UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId],
                cancellationToken);
            if (settings != null)
            {
                settings.EnableTokenCreation = false;
            }

            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<EvmWalletCredentials> ImportInternalAsync(long chatId, int slotNumber, string privateKey,
        CancellationToken cancellationToken)
    {
        string cleanPrivateKey = privateKey.Trim();
        if (cleanPrivateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            cleanPrivateKey = cleanPrivateKey[2..];
        }

        if (cleanPrivateKey.Length != 64 || !cleanPrivateKey.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid EVM private key.", nameof(privateKey));
        }

        EthECKey key;
        try
        {
            key = new EthECKey(cleanPrivateKey);
        }
        catch (Exception exception)
        {
            throw new ArgumentException("Invalid EVM private key.", nameof(privateKey), exception);
        }

        string normalizedPrivateKey = key.GetPrivateKey();
        if (!normalizedPrivateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPrivateKey = "0x" + normalizedPrivateKey;
        }

        string address = key.GetPublicAddress();
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker worker = await GetOrCreateWorkerAsync(db, chatId, slotNumber, cancellationToken);
        worker.EvmWalletAddress = address;
        worker.EncryptedEvmPrivateKey = protector.Protect(normalizedPrivateKey);
        worker.UpdatedAtUtc = DateTime.UtcNow;
        UserTradingSettings settings = await GetOrCreateSettingsAsync(db, chatId, cancellationToken);
        settings.EnableTokenCreation = false;
        await db.SaveChangesAsync(cancellationToken);

        return new EvmWalletCredentials(address, normalizedPrivateKey);
    }

    private async Task<EvmWalletCreated> CreateInternalAsync(long chatId, int slotNumber,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker worker = await GetOrCreateWorkerAsync(db, chatId, slotNumber, cancellationToken);

        if (TryReadWallet(worker, out EvmWalletCredentials existingWallet))
        {
            return new EvmWalletCreated(existingWallet.Address, null, false);
        }
        if (!string.IsNullOrWhiteSpace(worker.EvmWalletAddress)
            || !string.IsNullOrWhiteSpace(worker.EncryptedEvmPrivateKey))
        {
            throw new InvalidOperationException("The existing EVM wallet cannot be decrypted on this machine.");
        }

        EthECKey key = EthECKey.GenerateKey();
        string privateKey = key.GetPrivateKey();
        if (!privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            privateKey = "0x" + privateKey;
        }

        string address = key.GetPublicAddress();
        worker.EvmWalletAddress = address;
        worker.EncryptedEvmPrivateKey = protector.Protect(privateKey);
        worker.UpdatedAtUtc = DateTime.UtcNow;
        UserTradingSettings settings = await GetOrCreateSettingsAsync(db, chatId, cancellationToken);
        settings.EnableTokenCreation = false;
        await db.SaveChangesAsync(cancellationToken);

        return new EvmWalletCreated(address, privateKey, true);
    }

    public async Task<EvmWalletCredentials?> GetAsync(long chatId, CancellationToken cancellationToken)
    {
        return await GetAsync(chatId, 1, cancellationToken);
    }

    public async Task<EvmWalletCredentials?> GetAsync(long chatId, int slotNumber,
        CancellationToken cancellationToken)
    {
        ValidateSlot(slotNumber);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker? worker = await db.TradingWorkers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.SlotNumber == slotNumber,
                cancellationToken);

        return TryReadWallet(worker, out EvmWalletCredentials wallet) ? wallet : null;
    }

    public async Task<TradingWorkerWallet?> GetByIdAsync(long chatId, long workerId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        TradingWorker? worker = await db.TradingWorkers.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.Id == workerId, cancellationToken);

        return TryReadWallet(worker, out EvmWalletCredentials wallet)
            ? new TradingWorkerWallet(worker!.Id, worker.SlotNumber, wallet)
            : null;
    }

    public async Task<IReadOnlyList<TradingWorkerWallet>> GetReadyWorkersAsync(long chatId, int count,
        CancellationToken cancellationToken)
    {
        if (count < 1 || count > workerOptions.MaxWorkers)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<TradingWorker> workers = await db.TradingWorkers.AsNoTracking()
            .Where(item => item.ChatId == chatId && item.IsEnabled && item.SlotNumber <= count)
            .OrderBy(item => item.SlotNumber).ToListAsync(cancellationToken);

        List<TradingWorkerWallet> result = [];
        foreach (TradingWorker worker in workers)
        {
            if (TryReadWallet(worker, out EvmWalletCredentials wallet))
            {
                result.Add(new TradingWorkerWallet(worker.Id, worker.SlotNumber, wallet));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<TradingWorkerState>> GetStatesAsync(long chatId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<TradingWorker> workers = await db.TradingWorkers.AsNoTracking()
            .Where(item => item.ChatId == chatId).OrderBy(item => item.SlotNumber)
            .ToListAsync(cancellationToken);

        return Enumerable.Range(1, workerOptions.MaxWorkers).Select(slot =>
        {
            TradingWorker? worker = workers.FirstOrDefault(item => item.SlotNumber == slot);
            return new TradingWorkerState(worker?.Id, slot, worker?.EvmWalletAddress ?? string.Empty,
                TryReadWallet(worker, out _));
        }).ToList();
    }

    public async Task<IReadOnlyList<EvmNativeBalance>> GetBalancesAsync(string address,
        CancellationToken cancellationToken)
    {
        return await Task.WhenAll(
            ReadBalanceAsync("BSC", "BNB", networkOptions.BscRpcUrl, address, cancellationToken),
            ReadBalanceAsync("Base", "ETH", networkOptions.BaseRpcUrl, address, cancellationToken),
            ReadBalanceAsync("Robinhood", "ETH", networkOptions.RobinhoodRpcUrl, address, cancellationToken),
            ReadBalanceAsync("Stable", "USDT0", networkOptions.StableRpcUrl, address, cancellationToken));
    }

    private static async Task<EvmNativeBalance> ReadBalanceAsync(string network, string currency, string rpcUrl,
        string address, CancellationToken cancellationToken)
    {
        try
        {
            Web3 web3 = new Web3(rpcUrl);
            HexBigInteger value = await web3.Eth.GetBalance.SendRequestAsync(address)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return new EvmNativeBalance(network, currency, UnitConversion.Convert.FromWei(value.Value));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new EvmNativeBalance(network, currency, null);
        }
    }

    private bool TryReadWallet(TradingWorker? worker, out EvmWalletCredentials wallet)
    {
        wallet = null!;
        if (worker == null || string.IsNullOrWhiteSpace(worker.EvmWalletAddress)
            || string.IsNullOrWhiteSpace(worker.EncryptedEvmPrivateKey))
        {
            return false;
        }

        try
        {
            wallet = new EvmWalletCredentials(worker.EvmWalletAddress,
                protector.Unprotect(worker.EncryptedEvmPrivateKey));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<TradingWorker> GetOrCreateWorkerAsync(AppDbContext db, long chatId,
        int slotNumber, CancellationToken cancellationToken)
    {
        TradingWorker? worker = await db.TradingWorkers
            .FirstOrDefaultAsync(item => item.ChatId == chatId && item.SlotNumber == slotNumber,
                cancellationToken);
        if (worker != null)
        {
            return worker;
        }

        DateTime now = DateTime.UtcNow;
        worker = new TradingWorker
        {
            ChatId = chatId,
            SlotNumber = slotNumber,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.TradingWorkers.Add(worker);
        return worker;
    }

    private static async Task<UserTradingSettings> GetOrCreateSettingsAsync(AppDbContext db, long chatId,
        CancellationToken cancellationToken)
    {
        UserTradingSettings? settings = await db.UserTradingSettings.FindAsync([chatId], cancellationToken);
        if (settings != null)
        {
            return settings;
        }

        settings = new UserTradingSettings { ChatId = chatId };
        db.UserTradingSettings.Add(settings);
        return settings;
    }

    private void ValidateSlot(int slotNumber)
    {
        if (slotNumber < 1 || slotNumber > workerOptions.MaxWorkers)
        {
            throw new ArgumentOutOfRangeException(nameof(slotNumber));
        }
    }
}

public sealed record EvmWalletCredentials(string Address, string PrivateKey);

public sealed record EvmWalletCreated(string Address, string? PrivateKey, bool IsNew);

public sealed record EvmNativeBalance(string Network, string Currency, decimal? Amount);

public sealed record TradingWorkerWallet(long WorkerId, int SlotNumber, EvmWalletCredentials Wallet);

public sealed record TradingWorkerState(long? WorkerId, int SlotNumber, string Address, bool HasWallet);
