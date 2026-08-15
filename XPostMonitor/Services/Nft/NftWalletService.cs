using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Nethereum.Web3;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Nft;

public sealed class NftWalletService
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IDataProtector protector;
    private readonly OpenSeaNftOptions options;

    public NftWalletService(IServiceScopeFactory scopeFactory, IDataProtectionProvider protectionProvider,
        OpenSeaNftOptions options)
    {
        this.scopeFactory = scopeFactory;
        this.options = options;
        protector = protectionProvider.CreateProtector("XPostMonitor.NftWallet.v1");
    }

    // Tạo ví chính (slot 0) hoặc ví mint tiếp theo; không bao giờ ghi đè ví cũ.
    public async Task<EvmWalletCreated> CreateAsync(long chatId, bool main, NftWalletGroup mintGroup,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int slot = main ? 0 : await NextMintSlotAsync(db, chatId, cancellationToken);
        if (main && await db.NftWallets.AnyAsync(x => x.ChatId == chatId && x.SlotNumber == 0,
                cancellationToken))
        {
            throw new InvalidOperationException("Ví chính NFT đã tồn tại.");
        }

        EvmWalletCredentials wallet = EvmKeyHelper.Create();
        Add(db, chatId, slot, wallet.Address, wallet.PrivateKey,
            main ? NftWalletGroup.Free : mintGroup);
        await db.SaveChangesAsync(cancellationToken);
        return new EvmWalletCreated(wallet.Address, wallet.PrivateKey, true);
    }

    public async Task<EvmWalletCredentials> ImportAsync(long chatId, bool main, string privateKey,
        NftWalletGroup mintGroup,
        CancellationToken cancellationToken)
    {
        EvmWalletCredentials wallet = EvmKeyHelper.Read(privateKey);
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        int slot = main ? 0 : await NextMintSlotAsync(db, chatId, cancellationToken);
        if (await db.NftWallets.AnyAsync(x => x.ChatId == chatId
                && (x.SlotNumber == slot || x.WalletAddress == wallet.Address), cancellationToken))
        {
            throw new InvalidOperationException("Ví NFT này đã tồn tại.");
        }
        Add(db, chatId, slot, wallet.Address, wallet.PrivateKey,
            main ? NftWalletGroup.Free : mintGroup);
        await db.SaveChangesAsync(cancellationToken);
        return wallet;
    }

    public async Task<IReadOnlyList<NftWalletInfo>> ListAsync(long chatId, NftWalletGroup? mintGroup,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<NftWalletInfo> wallets = await db.NftWallets.AsNoTracking()
            .Where(x => x.ChatId == chatId
                && (mintGroup == null || x.SlotNumber == 0 || x.MintGroup == mintGroup))
            .OrderBy(x => x.SlotNumber)
            .Select(x => new NftWalletInfo(x.Id, x.SlotNumber, x.WalletAddress,
                x.IsEnabled, x.MintGroup, null))
            .ToListAsync(cancellationToken);
        Web3 web3 = new(options.RpcUrl);
        return await Task.WhenAll(wallets.Select(async wallet =>
        {
            try
            {
                decimal balance = Web3.Convert.FromWei((await web3.Eth.GetBalance
                    .SendRequestAsync(wallet.Address).WaitAsync(cancellationToken)).Value);
                return wallet with { BalanceEth = balance };
            }
            catch (Exception exception)
            {
                await NftDiagnosticLog.WriteAsync(
                    $"LỖI ĐỌC SỐ DƯ V{wallet.SlotNumber}, Wallet={wallet.Address}: {exception}");
                return wallet;
            }
        }));
    }

    public async Task<IReadOnlyList<NftMintWallet>> GetMintWalletsAsync(long chatId,
        NftWalletGroup mintGroup,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<NftWallet> rows = await db.NftWallets.AsNoTracking()
            .Where(x => x.ChatId == chatId && x.IsEnabled && x.SlotNumber > 0
                && x.MintGroup == mintGroup)
            .OrderBy(x => x.SlotNumber).ToListAsync(cancellationToken);
        return rows.Select(x => new NftMintWallet(x.Id, x.SlotNumber,
            new EvmWalletCredentials(x.WalletAddress, protector.Unprotect(x.EncryptedPrivateKey)))).ToList();
    }

    public async Task<EvmWalletCredentials?> GetMainAsync(long chatId,
        CancellationToken cancellationToken) => (await GetAsync(chatId, true, cancellationToken)).FirstOrDefault();

    // Chỉ trả private key khi ví thuộc đúng người đang dùng bot.
    public async Task<EvmWalletCredentials?> GetByIdAsync(long chatId, long id,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        NftWallet? wallet = await db.NftWallets.AsNoTracking()
            .FirstOrDefaultAsync(x => x.ChatId == chatId && x.Id == id, cancellationToken);
        return wallet == null ? null : new EvmWalletCredentials(wallet.WalletAddress,
            protector.Unprotect(wallet.EncryptedPrivateKey));
    }

    public async Task DeleteAsync(long chatId, long id, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        NftWallet? wallet = await db.NftWallets.FirstOrDefaultAsync(x => x.ChatId == chatId && x.Id == id,
            cancellationToken);
        if (wallet == null) return;
        db.NftWallets.Remove(wallet);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveToOtherGroupAsync(long chatId, long id,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        NftWallet? wallet = await db.NftWallets.FirstOrDefaultAsync(
            x => x.ChatId == chatId && x.Id == id && x.SlotNumber > 0, cancellationToken);
        if (wallet == null) return;
        wallet.MintGroup = wallet.MintGroup == NftWalletGroup.Free
            ? NftWalletGroup.Paid : NftWalletGroup.Free;
        wallet.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<EvmWalletCredentials>> GetAsync(long chatId, bool main,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<NftWallet> wallets = await db.NftWallets.AsNoTracking()
            .Where(x => x.ChatId == chatId && x.IsEnabled && (main ? x.SlotNumber == 0 : x.SlotNumber > 0))
            .OrderBy(x => x.SlotNumber).ToListAsync(cancellationToken);
        return wallets.Select(x => new EvmWalletCredentials(x.WalletAddress,
            protector.Unprotect(x.EncryptedPrivateKey))).ToList();
    }

    private void Add(AppDbContext db, long chatId, int slot, string address, string privateKey,
        NftWalletGroup mintGroup)
    {
        DateTime now = DateTime.UtcNow;
        db.NftWallets.Add(new NftWallet { ChatId = chatId, SlotNumber = slot,
            WalletAddress = address, EncryptedPrivateKey = protector.Protect(privateKey),
            MintGroup = mintGroup,
            CreatedAtUtc = now, UpdatedAtUtc = now });
    }

    private async Task<int> NextMintSlotAsync(AppDbContext db, long chatId,
        CancellationToken cancellationToken)
    {
        int count = await db.NftWallets.CountAsync(x => x.ChatId == chatId && x.SlotNumber > 0,
            cancellationToken);
        if (count >= options.MaxWallets)
            throw new InvalidOperationException($"Chỉ được tạo tối đa {options.MaxWallets} ví mint NFT.");
        return (await db.NftWallets.Where(x => x.ChatId == chatId)
            .MaxAsync(x => (int?)x.SlotNumber, cancellationToken) ?? 0) + 1;
    }
}

public sealed record NftWalletInfo(long Id, int SlotNumber, string Address, bool IsEnabled,
    NftWalletGroup MintGroup, decimal? BalanceEth = null);
public sealed record NftMintWallet(long Id, int SlotNumber, EvmWalletCredentials Credentials);
