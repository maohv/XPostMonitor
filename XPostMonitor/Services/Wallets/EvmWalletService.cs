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
    private readonly ConcurrentDictionary<long, SemaphoreSlim> walletLocks = new();

    public EvmWalletService(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider,
        EvmNetworksOptions networkOptions)
    {
        this.scopeFactory = scopeFactory;
        this.networkOptions = networkOptions;
        protector = protectionProvider.CreateProtector("XPostMonitor.EvmWallet.v1");
    }

    public async Task<EvmWalletCreated> CreateAsync(long chatId, CancellationToken cancellationToken)
    {
        SemaphoreSlim walletLock = walletLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await CreateInternalAsync(chatId, cancellationToken);
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
        SemaphoreSlim walletLock = walletLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            return await ImportInternalAsync(chatId, privateKey, cancellationToken);
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<EvmWalletCredentials> ImportInternalAsync(long chatId, string privateKey,
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
        UserTradingSettings settings = await GetOrCreateAsync(db, chatId, cancellationToken);
        settings.EvmWalletAddress = address;
        settings.EncryptedEvmPrivateKey = protector.Protect(normalizedPrivateKey);
        settings.EnableTokenCreation = false;
        await db.SaveChangesAsync(cancellationToken);

        return new EvmWalletCredentials(address, normalizedPrivateKey);
    }

    private async Task<EvmWalletCreated> CreateInternalAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings settings = await GetOrCreateAsync(db, chatId, cancellationToken);

        if (TryReadWallet(settings, out EvmWalletCredentials existingWallet))
        {
            return new EvmWalletCreated(existingWallet.Address, null, false);
        }
        if (!string.IsNullOrWhiteSpace(settings.EvmWalletAddress)
            || !string.IsNullOrWhiteSpace(settings.EncryptedEvmPrivateKey))
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
        settings.EvmWalletAddress = address;
        settings.EncryptedEvmPrivateKey = protector.Protect(privateKey);
        settings.EnableTokenCreation = false;
        await db.SaveChangesAsync(cancellationToken);

        return new EvmWalletCreated(address, privateKey, true);
    }

    public async Task<EvmWalletCredentials?> GetAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserTradingSettings? settings = await db.UserTradingSettings
            .AsNoTracking().FirstOrDefaultAsync(item => item.ChatId == chatId, cancellationToken);

        return TryReadWallet(settings, out EvmWalletCredentials wallet) ? wallet : null;
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

    private bool TryReadWallet(UserTradingSettings? settings, out EvmWalletCredentials wallet)
    {
        wallet = null!;
        if (settings == null || string.IsNullOrWhiteSpace(settings.EvmWalletAddress)
            || string.IsNullOrWhiteSpace(settings.EncryptedEvmPrivateKey))
        {
            return false;
        }

        try
        {
            wallet = new EvmWalletCredentials(settings.EvmWalletAddress, protector.Unprotect(settings.EncryptedEvmPrivateKey));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<UserTradingSettings> GetOrCreateAsync(AppDbContext db, long chatId,
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
}

public sealed record EvmWalletCredentials(string Address, string PrivateKey);

public sealed record EvmWalletCreated(string Address, string? PrivateKey, bool IsNew);

public sealed record EvmNativeBalance(string Network, string Currency, decimal? Amount);
