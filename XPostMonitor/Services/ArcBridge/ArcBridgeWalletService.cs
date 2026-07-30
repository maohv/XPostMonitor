using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nethereum.Signer;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.ArcBridge;

// Tạo, nhập và đọc ví chỉ dành cho Bridge miễn phí.
public sealed class ArcBridgeWalletService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IDataProtector protector;
    private readonly ConcurrentDictionary<long, SemaphoreSlim> walletLocks = new();

    public ArcBridgeWalletService(IServiceScopeFactory scopeFactory,
        IDataProtectionProvider protectionProvider)
    {
        this.scopeFactory = scopeFactory;
        protector = protectionProvider.CreateProtector("XPostMonitor.ArcBridgeWallet.v1");
    }

    // Tạo một ví mới. Nếu user đã có ví Bridge thì không tạo đè.
    public async Task<EvmWalletCreated> CreateAsync(long chatId,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim walletLock = walletLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ArcBridgeWallet? existing = await db.ArcBridgeWallets.AsNoTracking()
                .FirstOrDefaultAsync(item => item.ChatId == chatId, cancellationToken);
            if (existing != null)
            {
                EvmWalletCredentials credentials = ReadCredentials(existing);
                return new EvmWalletCreated(credentials.Address, null, false);
            }

            EthECKey key = EthECKey.GenerateKey();
            string privateKey = NormalizePrivateKey(key.GetPrivateKey());
            string address = key.GetPublicAddress();
            DateTime now = DateTime.UtcNow;
            db.ArcBridgeWallets.Add(new ArcBridgeWallet
            {
                ChatId = chatId,
                WalletAddress = address,
                EncryptedPrivateKey = protector.Protect(privateKey),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            return new EvmWalletCreated(address, privateKey, true);
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Nhập private key có sẵn. Không cho phép ghi đè ví Bridge hiện tại.
    public async Task<EvmWalletCredentials> ImportAsync(long chatId, string privateKey,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim walletLock = walletLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        await walletLock.WaitAsync(cancellationToken);
        try
        {
            using IServiceScope scope = scopeFactory.CreateScope();
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.ArcBridgeWallets.AnyAsync(item => item.ChatId == chatId,
                    cancellationToken))
            {
                throw new InvalidOperationException("Arc Bridge wallet already exists.");
            }

            string normalizedPrivateKey = ValidateAndNormalize(privateKey);
            EthECKey key = new EthECKey(normalizedPrivateKey[2..]);
            string address = key.GetPublicAddress();
            DateTime now = DateTime.UtcNow;
            db.ArcBridgeWallets.Add(new ArcBridgeWallet
            {
                ChatId = chatId,
                WalletAddress = address,
                EncryptedPrivateKey = protector.Protect(normalizedPrivateKey),
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            return new EvmWalletCredentials(address, normalizedPrivateKey);
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Đọc ví Bridge đã lưu và giải mã private key trên máy đang chạy bot.
    public async Task<EvmWalletCredentials?> GetAsync(long chatId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ArcBridgeWallet? wallet = await db.ArcBridgeWallets.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ChatId == chatId, cancellationToken);
        return wallet == null ? null : ReadCredentials(wallet);
    }

    private EvmWalletCredentials ReadCredentials(ArcBridgeWallet wallet)
    {
        try
        {
            return new EvmWalletCredentials(wallet.WalletAddress,
                protector.Unprotect(wallet.EncryptedPrivateKey));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The Arc Bridge wallet cannot be decrypted on this machine.", exception);
        }
    }

    private static string ValidateAndNormalize(string privateKey)
    {
        string clean = privateKey.Trim();
        if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[2..];
        }
        if (clean.Length != 64 || !clean.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid EVM private key.", nameof(privateKey));
        }

        try
        {
            return NormalizePrivateKey(new EthECKey(clean).GetPrivateKey());
        }
        catch (Exception exception)
        {
            throw new ArgumentException("Invalid EVM private key.", nameof(privateKey), exception);
        }
    }

    private static string NormalizePrivateKey(string privateKey)
    {
        return privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? privateKey
            : "0x" + privateKey;
    }
}
