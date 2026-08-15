using Microsoft.EntityFrameworkCore;
using XPostMonitor.Data;
using XPostMonitor.Models;

namespace XPostMonitor.Services.Launchpads;

// Chỉ đọc và lưu cấu hình Auto khi user tự ném link X vào bot.
public sealed class LinkTokenSettingsService
{
    private readonly IServiceScopeFactory scopeFactory;

    public LinkTokenSettingsService(IServiceScopeFactory scopeFactory)
    {
        this.scopeFactory = scopeFactory;
    }

    // Trả về null nếu user chưa từng lưu cấu hình. Khi đó bot dùng luồng thủ công cũ.
    public async Task<LinkTokenConfiguration?> GetAsync(long chatId, CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        LinkTokenSettings? item = await db.LinkTokenSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ChatId == chatId, cancellationToken);
        return item == null ? null : new LinkTokenConfiguration(item.EnableAutoCreate, item.Chain,
            item.Launchpad, item.Anchor, item.CreatorTaxPercent, item.FlapHolderPercent, item.EnableAutoTrading,
            ParseWorkerSlots(item.WorkerSlots), item.BuyAmount, item.SlippagePercent);
    }

    // Lưu độc lập, tuyệt đối không sửa WatchlistEntries của username đang theo dõi.
    public async Task SaveAsync(long chatId, LinkTokenConfiguration settings,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        LinkTokenSettings? entity = await db.LinkTokenSettings.FindAsync([chatId], cancellationToken);
        if (entity == null)
        {
            entity = new LinkTokenSettings { ChatId = chatId };
            db.LinkTokenSettings.Add(entity);
        }

        entity.EnableAutoCreate = settings.EnableAutoCreate;
        entity.Chain = settings.Chain;
        entity.Launchpad = settings.Launchpad;
        entity.Anchor = settings.Anchor;
        entity.CreatorTaxPercent = settings.CreatorTaxPercent;
        entity.FlapHolderPercent = Math.Clamp(settings.FlapHolderPercent, 0, 100);
        entity.EnableAutoTrading = settings.EnableAutoTrading;
        entity.WorkerSlots = string.Join(',', settings.WorkerSlots.Distinct().OrderBy(slot => slot));
        entity.BuyAmount = settings.BuyAmount;
        entity.SlippagePercent = settings.SlippagePercent;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    // DB lưu "1,3" cho gọn; khi chạy sẽ đổi lại thành danh sách số ví [1, 3].
    private static IReadOnlyList<int> ParseWorkerSlots(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => int.TryParse(item, out int slot) ? slot : 0)
            .Where(slot => slot > 0)
            .Distinct()
            .OrderBy(slot => slot)
            .ToArray();
    }
}

public sealed record LinkTokenConfiguration(bool EnableAutoCreate, string Chain, string Launchpad,
    string? Anchor, int CreatorTaxPercent, int FlapHolderPercent, bool EnableAutoTrading,
    IReadOnlyList<int> WorkerSlots,
    decimal BuyAmount, decimal SlippagePercent);
