using System.Numerics;
using System.Security.Cryptography;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using XPostMonitor.Configuration;
using XPostMonitor.Models;
using XPostMonitor.Services.Wallets;

namespace XPostMonitor.Services.Nft;

public sealed class NftMintService
{
    private const long RobinhoodChainId = 4663;
    private readonly NftWalletService wallets;
    private readonly OpenSeaNftClient openSea;
    private readonly OpenSeaNftOptions options;

    public NftMintService(NftWalletService wallets, OpenSeaNftClient openSea, OpenSeaNftOptions options)
    {
        this.wallets = wallets;
        this.openSea = openSea;
        this.options = options;
    }

    public async Task<NftMintPlan> PrepareAsync(long chatId, string url, NftMintMode mode, int value,
        IReadOnlyCollection<int> selectedWalletSlots, CancellationToken cancellationToken)
    {
        if (value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        OpenSeaCollection collection = await openSea.GetCollectionAsync(url, cancellationToken);
        SeaDropInfo drop = await openSea.GetPublicDropAsync(collection, cancellationToken);
        NftWalletGroup mintGroup = SelectWalletGroup(drop.MintPriceWei);
        IReadOnlyList<NftMintWallet> mintWallets = await wallets.GetMintWalletsAsync(chatId,
            mintGroup, cancellationToken);
        HashSet<int> requestedSlots = selectedWalletSlots.ToHashSet();
        mintWallets = mintWallets.Where(x => requestedSlots.Contains(x.SlotNumber)).ToList();
        int[] unavailableSlots = requestedSlots.Except(mintWallets.Select(x => x.SlotNumber))
            .OrderBy(x => x).ToArray();
        if (unavailableSlots.Length > 0)
            throw new InvalidOperationException("Ví " + string.Join(", ", unavailableSlots)
                + $" không tồn tại hoặc không thuộc nhóm {mintGroup}.");
        if (mintWallets.Count == 0) throw new InvalidOperationException("Chưa chọn ví mint NFT.");

        IReadOnlyList<NftMintPlanItem> items = BuildItems(mintWallets, mode, value);
        foreach (IGrouping<long, NftMintPlanItem> group in items.GroupBy(x => x.Wallet.Id))
        {
            BigInteger minted = await openSea.GetMintedCountAsync(collection.ContractAddress,
                group.First().Wallet.Credentials.Address, cancellationToken);
            if (minted + group.Sum(x => x.Quantity) > drop.MaxTotalMintableByWallet)
                throw new InvalidOperationException($"Ví {group.First().Wallet.SlotNumber} vượt giới hạn mint của collection.");
        }

        int totalQuantity = items.Sum(x => x.Quantity);
        return new NftMintPlan(drop, mintGroup, mode, value, items, totalQuantity,
            drop.MintPriceWei * totalQuantity);
    }

    // Mỗi ví lỗi sẽ dừng riêng; các ví còn lại vẫn tiếp tục các vòng sau.
    public async Task<IReadOnlyList<NftMintOutcome>> ExecuteAsync(NftMintPlan plan,
        CancellationToken cancellationToken)
    {
        SeaDropInfo latest = await openSea.GetPublicDropAsync(plan.Drop.Collection, cancellationToken);
        if (latest.MintPriceWei != plan.Drop.MintPriceWei)
            throw new InvalidOperationException("Giá mint đã thay đổi sau bước xác nhận; bot không gửi giao dịch.");

        Dictionary<long, BigInteger> nonces = new();
        foreach (NftMintWallet wallet in plan.Items.Select(x => x.Wallet).DistinctBy(x => x.Id))
            nonces[wallet.Id] = await openSea.GetPendingNonceAsync(wallet.Credentials.Address,
                cancellationToken);

        return plan.Mode == NftMintMode.Round
            ? await ExecuteRoundsAsync(plan.Items, latest, nonces, cancellationToken)
            : await ExecuteBatchAsync(plan.Items, latest, nonces, cancellationToken);
    }

    private async Task<IReadOnlyList<NftMintOutcome>> ExecuteRoundsAsync(
        IReadOnlyList<NftMintPlanItem> items, SeaDropInfo drop,
        Dictionary<long, BigInteger> nonces, CancellationToken cancellationToken)
    {
        List<NftMintOutcome> results = [];
        HashSet<long> stoppedWalletIds = [];
        foreach (IGrouping<int, NftMintPlanItem> round in items.GroupBy(x => x.Round))
        {
            List<NftMintPlanItem> activeWallets = round
                .Where(x => !stoppedWalletIds.Contains(x.Wallet.Id)).ToList();
            if (activeWallets.Count == 0) break;

            IReadOnlyList<NftMintOutcome> roundResults = await ExecuteBatchAsync(
                activeWallets, drop, nonces, cancellationToken);
            results.AddRange(roundResults);

            foreach (NftMintOutcome failed in roundResults.Where(x => x.Error != null))
            {
                NftMintPlanItem item = activeWallets.First(x =>
                    x.Wallet.SlotNumber == failed.SlotNumber);
                stoppedWalletIds.Add(item.Wallet.Id);
                await NftDiagnosticLog.WriteAsync(
                    $"DỪNG RIÊNG VÍ V{failed.SlotNumber} TẠI VÒNG {round.Key}, "
                    + $"Wallet={failed.Wallet}, Hash={failed.TransactionHash ?? "-"}, "
                    + $"Lỗi={failed.Error}");
            }

            await NftDiagnosticLog.WriteAsync(
                $"VÒNG {round.Key} KẾT THÚC: "
                + $"{roundResults.Count(x => x.Error == null)} thành công, "
                + $"{roundResults.Count(x => x.Error != null)} thất bại.");
        }
        return results;
    }

    // Gửi các ví trong cùng nhóm trước, sau đó chờ xác nhận on-chain song song.
    private async Task<IReadOnlyList<NftMintOutcome>> ExecuteBatchAsync(
        IReadOnlyList<NftMintPlanItem> items, SeaDropInfo drop,
        Dictionary<long, BigInteger> nonces, CancellationToken cancellationToken)
    {
        List<NftMintOutcome> results = [];
        List<Task<NftMintOutcome>> confirmations = [];
        foreach (NftMintPlanItem item in items)
        {
            try
            {
                NftMintResult sent = await openSea.SendMintAsync(item.Wallet.Credentials, drop,
                    item.Quantity, nonces[item.Wallet.Id], cancellationToken);
                nonces[item.Wallet.Id]++;
                await NftDiagnosticLog.WriteAsync(
                    $"Đã broadcast V{item.Wallet.SlotNumber}/R{item.Round}, Wallet={sent.Wallet}, "
                    + $"Quantity={item.Quantity}, Hash={sent.TransactionHash ?? "DRY-RUN"}");
                if (sent.IsDryRun || sent.TransactionHash == null)
                    results.Add(new NftMintOutcome(item.Wallet.SlotNumber, sent.Wallet,
                        item.Round, item.Quantity, sent.TransactionHash, null, sent.IsDryRun));
                else
                    confirmations.Add(ConfirmAsync(item, sent, cancellationToken));
            }
            catch (Exception exception)
            {
                await NftDiagnosticLog.WriteAsync(
                    $"LỖI MINT V{item.Wallet.SlotNumber}/R{item.Round}, "
                    + $"Wallet={item.Wallet.Credentials.Address}: {exception}");
                results.Add(new NftMintOutcome(item.Wallet.SlotNumber, item.Wallet.Credentials.Address,
                    item.Round, item.Quantity, null, exception.Message, false));
            }
            if (options.SendDelayMs > 0)
                await Task.Delay(options.SendDelayMs, cancellationToken);
        }
        if (confirmations.Count > 0)
            results.AddRange(await Task.WhenAll(confirmations));
        return results.OrderBy(x => x.SlotNumber).ToList();
    }

    // Broadcast chưa được tính là thành công; phải có receipt status = 1.
    private async Task<NftMintOutcome> ConfirmAsync(NftMintPlanItem item, NftMintResult sent,
        CancellationToken cancellationToken)
    {
        try
        {
            NftMintReceipt receipt = await openSea.WaitForReceiptAsync(
                sent.TransactionHash!, cancellationToken);
            await NftDiagnosticLog.WriteAsync(
                $"MINT THÀNH CÔNG V{item.Wallet.SlotNumber}/R{item.Round}, Wallet={sent.Wallet}, "
                + $"Quantity={item.Quantity}, Hash={sent.TransactionHash}, "
                + $"Block={receipt.BlockNumber}, GasUsed={receipt.GasUsed}");
            return new NftMintOutcome(item.Wallet.SlotNumber, sent.Wallet,
                item.Round, item.Quantity, sent.TransactionHash, null, false);
        }
        catch (Exception exception)
        {
            await NftDiagnosticLog.WriteAsync(
                $"LỖI XÁC NHẬN V{item.Wallet.SlotNumber}/R{item.Round}, Wallet={sent.Wallet}, "
                + $"Hash={sent.TransactionHash}: {exception}");
            return new NftMintOutcome(item.Wallet.SlotNumber, sent.Wallet,
                item.Round, item.Quantity, sent.TransactionHash, exception.Message, false);
        }
    }

    public async Task<IReadOnlyList<string>> FundAllAsync(long chatId, decimal ethPerWallet,
        NftWalletGroup mintGroup, IReadOnlyCollection<long> selectedWalletIds,
        CancellationToken cancellationToken)
    {
        if (ethPerWallet <= 0 || ethPerWallet > 10) throw new ArgumentOutOfRangeException(nameof(ethPerWallet));
        EvmWalletCredentials main = await wallets.GetMainAsync(chatId, cancellationToken)
            ?? throw new InvalidOperationException("Chưa có ví chính NFT.");
        IReadOnlyList<NftMintWallet> targets = await wallets.GetMintWalletsAsync(chatId,
            mintGroup, cancellationToken);
        targets = targets.Where(x => selectedWalletIds.Contains(x.Id)).ToList();
        if (targets.Count == 0) throw new InvalidOperationException("Chưa có ví mint NFT.");

        Account account = new Account(main.PrivateKey, RobinhoodChainId);
        Web3 web3 = new Web3(account, options.RpcUrl);
        BigInteger amount = Web3.Convert.ToWei(ethPerWallet);
        BigInteger balance = (await web3.Eth.GetBalance.SendRequestAsync(main.Address)
            .WaitAsync(cancellationToken)).Value;
        BigInteger gasPrice = (await web3.Eth.GasPrice.SendRequestAsync().WaitAsync(cancellationToken)).Value;
        BigInteger transferGasPriceWei = gasPrice * 120 / 100;
        Dictionary<long, BigInteger> gasLimits = new();
        foreach (NftMintWallet target in targets)
        {
            var transaction = new Nethereum.RPC.Eth.DTOs.CallInput
            {
                From = main.Address,
                To = target.Credentials.Address,
                Value = new Nethereum.Hex.HexTypes.HexBigInteger(amount),
                GasPrice = new Nethereum.Hex.HexTypes.HexBigInteger(transferGasPriceWei)
            };
            BigInteger estimatedGas = (await web3.Eth.Transactions.EstimateGas
                .SendRequestAsync(transaction).WaitAsync(cancellationToken)).Value;
            gasLimits[target.Id] = estimatedGas * 120 / 100;
        }
        BigInteger required = amount * targets.Count
            + gasLimits.Values.Aggregate(BigInteger.Zero,
                (total, gas) => total + gas * transferGasPriceWei);
        if (balance < required) throw new InvalidOperationException("Ví chính không đủ ETH để chia và trả gas.");
        if (!options.EnableRealTransactions)
            return targets.Select(x => "DRY-RUN:" + x.Credentials.Address).ToList();

        List<string> hashes = [];
        foreach (NftMintWallet target in targets)
        {
            hashes.Add(await web3.Eth.GetEtherTransferService()
                .TransferEtherAsync(target.Credentials.Address, ethPerWallet,
                    gasPriceGwei: (decimal)transferGasPriceWei / 1_000_000_000m,
                    gas: gasLimits[target.Id])
                .WaitAsync(cancellationToken));
        }
        return hashes;
    }

    internal static IReadOnlyList<NftMintPlanItem> BuildItems(IReadOnlyList<NftMintWallet> wallets,
        NftMintMode mode, int value)
    {
        List<NftMintPlanItem> items = [];
        if (mode == NftMintMode.Round)
        {
            for (int round = 1; round <= value; round++)
                items.AddRange(wallets.Select(wallet => new NftMintPlanItem(wallet, 1, round)));
            return items;
        }
        foreach (NftMintWallet wallet in wallets)
        {
            int quantity = mode == NftMintMode.Random ? RandomNumberGenerator.GetInt32(1, value + 1) : value;
            items.Add(new NftMintPlanItem(wallet, quantity, 1));
        }
        return items;
    }

    internal static NftWalletGroup SelectWalletGroup(BigInteger mintPriceWei) =>
        mintPriceWei.IsZero ? NftWalletGroup.Free : NftWalletGroup.Paid;
}

public enum NftMintMode { Fixed, Random, Round }
public sealed record NftMintPlanItem(NftMintWallet Wallet, int Quantity, int Round);
public sealed record NftMintPlan(SeaDropInfo Drop, NftWalletGroup MintGroup,
    NftMintMode Mode, int InputValue,
    IReadOnlyList<NftMintPlanItem> Items, int TotalQuantity, BigInteger TotalValueWei);
public sealed record NftMintOutcome(int SlotNumber, string Wallet, int Round, int Quantity,
    string? TransactionHash, string? Error, bool IsDryRun);
