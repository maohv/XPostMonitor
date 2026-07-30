using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Models;

namespace XPostMonitor.Services.ArcBridge;

// Lưu từng bước Bridge để giao dịch đang chờ không bị mất khi bot khởi động lại.
public sealed class ArcBridgeTransferService
{
    private readonly IServiceScopeFactory scopeFactory;

    public ArcBridgeTransferService(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    // Chỉ trả về giao dịch chưa hoàn thành mới nhất của user.
    public async Task<ArcBridgeTransfer?> GetPendingAsync(long chatId,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ArcBridgeTransfers.AsNoTracking()
            .Where(item => item.ChatId == chatId
                && item.Status != ArcBridgeTransferStatus.Completed
                && item.Status != ArcBridgeTransferStatus.Failed)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ArcBridgeTransfer> CreateAsync(long chatId, string walletAddress,
        string grossAmountAtomic, string receiveAmountAtomic, string maxFeeAtomic,
        string burnIntentJson, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ArcBridgeTransfer? pending = await db.ArcBridgeTransfers
            .Where(item => item.ChatId == chatId
                && item.Status != ArcBridgeTransferStatus.Completed
                && item.Status != ArcBridgeTransferStatus.Failed)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending != null)
        {
            return pending;
        }

        DateTime now = DateTime.UtcNow;
        ArcBridgeTransfer transfer = new ArcBridgeTransfer
        {
            ChatId = chatId,
            WalletAddress = walletAddress,
            GrossAmountAtomic = grossAmountAtomic,
            ReceiveAmountAtomic = receiveAmountAtomic,
            MaxFeeAtomic = maxFeeAtomic,
            BurnIntentJson = burnIntentJson,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.ArcBridgeTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        return transfer;
    }

    public async Task SaveDepositAsync(long transferId, string transactionHash,
        string blockNumber, CancellationToken cancellationToken)
    {
        await UpdateAsync(transferId, transfer =>
        {
            transfer.DepositTransactionHash = transactionHash;
            transfer.DepositBlockNumber = blockNumber;
            transfer.Status = ArcBridgeTransferStatus.WaitingCircle;
            transfer.ErrorMessage = null;
        }, cancellationToken);
    }

    // Lưu hash ngay khi transaction được phát lên Base, trước cả khi chờ receipt.
    public async Task SaveDepositSubmittedAsync(long transferId, string transactionHash,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(transferId, transfer =>
        {
            transfer.DepositTransactionHash = transactionHash;
            transfer.Status = ArcBridgeTransferStatus.Depositing;
            transfer.ErrorMessage = null;
        }, cancellationToken);
    }

    public async Task SaveMintAsync(long transferId, string transactionHash,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(transferId, transfer =>
        {
            transfer.MintTransactionHash = transactionHash;
            transfer.Status = ArcBridgeTransferStatus.Minting;
            transfer.ErrorMessage = null;
        }, cancellationToken);
    }

    public async Task CompleteAsync(long transferId, CancellationToken cancellationToken)
    {
        await UpdateAsync(transferId, transfer =>
        {
            transfer.Status = ArcBridgeTransferStatus.Completed;
            transfer.CompletedAtUtc = DateTime.UtcNow;
            transfer.ErrorMessage = null;
        }, cancellationToken);
    }

    // Mint Arc bị revert thì bỏ hash lỗi để lần Resume có thể gửi lại.
    public async Task ResetMintAsync(long transferId, string error,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(transferId, transfer =>
        {
            transfer.MintTransactionHash = null;
            transfer.Status = ArcBridgeTransferStatus.WaitingCircle;
            transfer.ErrorMessage = error.Length <= 500 ? error : error[..500];
        }, cancellationToken);
    }

    // Nếu chưa deposit thì cho phép thử giao dịch mới; nếu đã deposit thì giữ trạng thái để Resume.
    public async Task SaveErrorAsync(long transferId, bool depositWasSent, string error,
        CancellationToken cancellationToken)
    {
        await UpdateAsync(transferId, transfer =>
        {
            if (!depositWasSent)
            {
                transfer.Status = ArcBridgeTransferStatus.Failed;
                transfer.CompletedAtUtc = DateTime.UtcNow;
            }
            transfer.ErrorMessage = error.Length <= 500 ? error : error[..500];
        }, cancellationToken);
    }

    private async Task UpdateAsync(long transferId, Action<ArcBridgeTransfer> update,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ArcBridgeTransfer transfer = await db.ArcBridgeTransfers
            .FirstAsync(item => item.Id == transferId, cancellationToken);
        update(transfer);
        transfer.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}
