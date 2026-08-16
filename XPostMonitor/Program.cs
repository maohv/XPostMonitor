using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Models;
using XPostMonitor.Services;
using XPostMonitor.Services.ArcBridge;
using XPostMonitor.Services.BinanceAlpha;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.Gemini;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.Gmgn.Launchpads;
using XPostMonitor.Services.Gmgn.Launchpads.FourMeme;
using XPostMonitor.Services.Gmgn.Launchpads.Flap;
using XPostMonitor.Services.Gmgn.Launchpads.FlapRobinhood;
using XPostMonitor.Services.Gmgn.Launchpads.LongRobinhood;
using XPostMonitor.Services.Gmgn.Launchpads.Pons;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Launchpads.DyorStable;
using XPostMonitor.Services.Launchpads.FourMeme;
using XPostMonitor.Services.Launchpads.Flap;
using XPostMonitor.Services.Launchpads.FlapRobinhood;
using XPostMonitor.Services.Launchpads.LongRobinhood;
using XPostMonitor.Services.Launchpads.PonsRobinhood;
using XPostMonitor.Services.OpenAi;
using XPostMonitor.Services.Nft;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Tokens;
using XPostMonitor.Services.Wallets;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Channels;
using XPostMonitor.Services.X.Notifications;
using XPostMonitor.Services.ZImage;

// Kiểm tra nhanh parser mà không khởi động bot hoặc chạm vào tiền/database.
if (args.Contains("--nft-self-check", StringComparer.OrdinalIgnoreCase))
{
    const string expected = "thebull2026";
    string actual = OpenSeaNftClient.GetCollectionSlug("https://opensea.io/collection/thebull2026");
    if (actual != expected) throw new InvalidOperationException("NFT URL self-check failed.");
    if (OpenSeaNftClient.GetCollectionSlug("https://opensea.io/collection/thebull2026/overview") != expected)
        throw new InvalidOperationException("NFT overview URL self-check failed.");
    const string expiredDrop = "0x13da22f2"
        + "000000000000000000000000000000000000000000000000000000006a7ffb66"
        + "000000000000000000000000000000000000000000000000000000006a7ffb29"
        + "000000000000000000000000000000000000000000000000000000006a7ffb65";
    if (OpenSeaNftClient.ExplainRevertData(expiredDrop)?.StartsWith("Public mint không còn hoạt động") != true)
        throw new InvalidOperationException("NFT revert reason self-check failed.");
    try
    {
        OpenSeaNftClient.GetCollectionSlug("https://opensea.io/assets/robinhood/0x123/1");
        throw new InvalidOperationException("NFT URL safety self-check failed.");
    }
    catch (ArgumentException) { }
    EvmWalletCredentials key = EvmKeyHelper.Create();
    if (EvmKeyHelper.Read(key.PrivateKey).Address != key.Address)
        throw new InvalidOperationException("NFT wallet self-check failed.");
    NftMintWallet[] testWallets =
    [
        new NftMintWallet(1, 1, key),
        new NftMintWallet(2, 2, key)
    ];
    if (NftMintService.BuildItems(testWallets, NftMintMode.Round, 3).Count != 6)
        throw new InvalidOperationException("NFT round plan self-check failed.");
    if (NftMintService.SelectWalletGroup(0) != NftWalletGroup.Free
        || NftMintService.SelectWalletGroup(1) != NftWalletGroup.Paid)
        throw new InvalidOperationException("NFT wallet group self-check failed.");
    int[] selectedSlots = NftMintMenuService.ParseWalletSlots("1-3,5,8-9");
    if (NftMintMenuService.FormatWalletSlots(selectedSlots) != "1-3,5,8-9")
        throw new InvalidOperationException("NFT wallet selection self-check failed.");
    byte[] excel = NftWalletExcelExporter.Create(
        [new NftWalletExportRow("Free", 1, key.Address, key.PrivateKey)]);
    using (System.IO.Compression.ZipArchive workbook = new(new MemoryStream(excel),
        System.IO.Compression.ZipArchiveMode.Read))
    {
        System.IO.Compression.ZipArchiveEntry? sheetEntry = workbook.GetEntry("xl/worksheets/sheet1.xml");
        if (sheetEntry == null)
            throw new InvalidOperationException("NFT Excel export self-check failed.");
        using Stream sheetStream = sheetEntry.Open();
        System.Xml.Linq.XDocument sheet = System.Xml.Linq.XDocument.Load(sheetStream);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        if (sheet.Descendants(ns + "row").Count() != 2 || !sheet.ToString().Contains(key.Address))
            throw new InvalidOperationException("NFT Excel content self-check failed.");
    }
    Console.WriteLine("NFT self-check passed. No transaction was sent.");
    return;
}

// Kiểm tra read-only một link thật; không tạo ví và không gửi giao dịch.
int nftLinkIndex = Array.FindIndex(args, x => x.Equals("--nft-check-link", StringComparison.OrdinalIgnoreCase));
if (nftLinkIndex >= 0 && nftLinkIndex + 1 < args.Length)
{
    using HttpClient http = new() { BaseAddress = new Uri("https://opensea.io/"), Timeout = TimeSpan.FromSeconds(30) };
    OpenSeaNftClient client = new OpenSeaNftClient(http, new OpenSeaNftOptions());
    OpenSeaCollection collection = await client.GetCollectionAsync(args[nftLinkIndex + 1], CancellationToken.None);
    Console.WriteLine($"OpenSea OK: {collection.Name} | {collection.ContractAddress}");
    SeaDropInfo drop = await client.GetPublicDropAsync(collection, CancellationToken.None);
    Console.WriteLine($"SeaDrop OK: {Nethereum.Web3.Web3.Convert.FromWei(drop.MintPriceWei)} ETH | max {drop.MaxTotalMintableByWallet}/wallet");
    return;
}

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddDataProtection().SetApplicationName("XPostMonitor");

BotOptions botOptions = new BotOptions();
builder.Configuration.GetSection(BotOptions.SectionName).Bind(botOptions);
builder.Services.AddSingleton(botOptions);

GmgnOptions gmgnOptions = new GmgnOptions();
builder.Configuration.GetSection(GmgnOptions.SectionName).Bind(gmgnOptions);
builder.Services.AddSingleton(gmgnOptions);

OpenAiOptions openAiOptions = new OpenAiOptions();
builder.Configuration.GetSection(OpenAiOptions.SectionName).Bind(openAiOptions);
builder.Services.AddSingleton(openAiOptions);

FluxOptions fluxOptions = new FluxOptions();
builder.Configuration.GetSection(FluxOptions.SectionName).Bind(fluxOptions);
builder.Services.AddSingleton(fluxOptions);

GeminiOptions geminiOptions = new GeminiOptions();
builder.Configuration.GetSection(GeminiOptions.SectionName).Bind(geminiOptions);
builder.Services.AddSingleton(geminiOptions);

ZImageOptions zImageOptions = new ZImageOptions();
builder.Configuration.GetSection(ZImageOptions.SectionName).Bind(zImageOptions);
builder.Services.AddSingleton(zImageOptions);

ImageGenerationOptions imageGenerationOptions = new ImageGenerationOptions();
builder.Configuration.GetSection(ImageGenerationOptions.SectionName).Bind(imageGenerationOptions);
builder.Services.AddSingleton(imageGenerationOptions);

FourMemeOptions fourMemeOptions = new FourMemeOptions();
builder.Configuration.GetSection(FourMemeOptions.SectionName).Bind(fourMemeOptions);
builder.Services.AddSingleton(fourMemeOptions);

FlapOptions flapOptions = new FlapOptions();
builder.Configuration.GetSection(FlapOptions.SectionName).Bind(flapOptions);
builder.Services.AddSingleton(flapOptions);

FlapRobinhoodOptions flapRobinhoodOptions = new FlapRobinhoodOptions();
builder.Configuration.GetSection(FlapRobinhoodOptions.SectionName).Bind(flapRobinhoodOptions);
builder.Services.AddSingleton(flapRobinhoodOptions);

DyorStableOptions dyorStableOptions = new DyorStableOptions();
builder.Configuration.GetSection(DyorStableOptions.SectionName).Bind(dyorStableOptions);
builder.Services.AddSingleton(dyorStableOptions);

LongRobinhoodOptions longRobinhoodOptions = new LongRobinhoodOptions();
builder.Configuration.GetSection(LongRobinhoodOptions.SectionName).Bind(longRobinhoodOptions);
if (string.IsNullOrWhiteSpace(longRobinhoodOptions.PinataJwt))
{
    longRobinhoodOptions.PinataJwt = dyorStableOptions.PinataJwt;
}
builder.Services.AddSingleton(longRobinhoodOptions);

PonsRobinhoodOptions ponsRobinhoodOptions = new PonsRobinhoodOptions();
builder.Configuration.GetSection(PonsRobinhoodOptions.SectionName).Bind(ponsRobinhoodOptions);
if (string.IsNullOrWhiteSpace(ponsRobinhoodOptions.PinataJwt))
{
    ponsRobinhoodOptions.PinataJwt = longRobinhoodOptions.PinataJwt;
}
builder.Services.AddSingleton(ponsRobinhoodOptions);

EvmNetworksOptions evmNetworksOptions = new EvmNetworksOptions();
builder.Configuration.GetSection(EvmNetworksOptions.SectionName).Bind(evmNetworksOptions);
builder.Services.AddSingleton(evmNetworksOptions);

TradingWorkersOptions tradingWorkersOptions = new TradingWorkersOptions();
builder.Configuration.GetSection(TradingWorkersOptions.SectionName).Bind(tradingWorkersOptions);
tradingWorkersOptions.MaxWorkers = Math.Max(1, tradingWorkersOptions.MaxWorkers);
builder.Services.AddSingleton(tradingWorkersOptions);

AutoTradingOptions autoTradingOptions = new AutoTradingOptions();
builder.Configuration.GetSection(AutoTradingOptions.SectionName).Bind(autoTradingOptions);
autoTradingOptions.NoBuyerTimeoutSeconds = Math.Max(1, autoTradingOptions.NoBuyerTimeoutSeconds);
autoTradingOptions.BuyerInactivitySeconds = Math.Max(1,
    autoTradingOptions.BuyerInactivitySeconds);
builder.Services.AddSingleton(autoTradingOptions);

ArcBridgeOptions arcBridgeOptions = new ArcBridgeOptions();
builder.Configuration.GetSection(ArcBridgeOptions.SectionName).Bind(arcBridgeOptions);
arcBridgeOptions.PollIntervalSeconds = Math.Max(1, arcBridgeOptions.PollIntervalSeconds);
arcBridgeOptions.PollTimeoutMinutes = Math.Max(1, arcBridgeOptions.PollTimeoutMinutes);
builder.Services.AddSingleton(arcBridgeOptions);

BinanceAlphaOptions binanceAlphaOptions = new BinanceAlphaOptions();
builder.Configuration.GetSection(BinanceAlphaOptions.SectionName).Bind(binanceAlphaOptions);
binanceAlphaOptions.RestCheckIntervalSeconds = Math.Max(5,
    binanceAlphaOptions.RestCheckIntervalSeconds);
builder.Services.AddSingleton(binanceAlphaOptions);

OpenSeaNftOptions openSeaNftOptions = new OpenSeaNftOptions();
builder.Configuration.GetSection(OpenSeaNftOptions.SectionName).Bind(openSeaNftOptions);
openSeaNftOptions.MaxWallets = Math.Clamp(openSeaNftOptions.MaxWallets, 1, 30);
openSeaNftOptions.SendDelayMs = Math.Clamp(openSeaNftOptions.SendDelayMs, 0, 5000);
builder.Services.AddSingleton(openSeaNftOptions);

builder.Services.AddHttpClient<XApiClient>(client =>
{
    client.BaseAddress = new Uri("https://api.x.com/");
    client.Timeout = Timeout.InfiniteTimeSpan; // X Stream cần giữ kết nối liên tục.
});

builder.Services.AddHttpClient<TelegramApiClient>(client =>
{
    client.BaseAddress = new Uri("https://api.telegram.org/");
    client.Timeout = TimeSpan.FromSeconds(40);
});

builder.Services.AddHttpClient<OpenAiClient>(client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/");
    client.Timeout = TimeSpan.FromMinutes(2);
});

builder.Services.AddHttpClient<FluxClient>(client =>
{
    client.BaseAddress = new Uri("https://api.bfl.ai/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<GeminiImageClient>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<FourMemeClient>(client =>
{
    client.BaseAddress = new Uri("https://four.meme/meme-api/v1/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<FlapClient>(client =>
{
    client.BaseAddress = new Uri("https://funcs.flap.sh/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<FlapRobinhoodClient>(client =>
{
    client.BaseAddress = new Uri("https://funcs.flap.sh/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<DyorStableClient>(client =>
{
    client.BaseAddress = new Uri("https://dyorv3.org/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<LongRobinhoodClient>(client =>
{
    client.BaseAddress = new Uri("https://api.pinata.cloud/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<PonsRobinhoodClient>(client =>
{
    client.BaseAddress = new Uri("https://api.pinata.cloud/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient("FlapCatalog", client =>
{
    client.BaseAddress = new Uri("https://flap.sh/");
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddHttpClient<ZImageClient>(client =>
{
    client.BaseAddress = new Uri("https://fal.run/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<ArcBridgeClient>(client =>
{
    client.BaseAddress = new Uri("https://dyorarc.fun/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<BinanceAlphaClient>(client =>
{
    client.BaseAddress = new Uri("https://www.binance.com/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpClient<OpenSeaNftClient>(client =>
{
    client.BaseAddress = new Uri("https://opensea.io/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<NftPortfolioClient>(client =>
{
    client.BaseAddress = new Uri("https://robinhoodchain.blockscout.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Các service xử lý chức năng thông thường.
builder.Services.AddSingleton<WatchlistService>();
builder.Services.AddSingleton<BotTextService>();
builder.Services.AddSingleton<LanguageMenuService>();
builder.Services.AddSingleton<ArcBridgeMenuService>();
builder.Services.AddSingleton<ArcBridgeWalletService>();
builder.Services.AddSingleton<ArcBridgeTransferService>();
builder.Services.AddSingleton<NftWalletService>();
builder.Services.AddSingleton<NftMintService>();
builder.Services.AddSingleton<NftMintMenuService>();
builder.Services.AddSingleton<ManualTokenMenuService>();
builder.Services.AddSingleton<LinkTokenSettingsMenuService>();
builder.Services.AddSingleton<WatchlistMenuService>();
builder.Services.AddSingleton<TokenSettingsMenuService>();
builder.Services.AddSingleton<AutoTradingMenuService>();
builder.Services.AddSingleton<ChannelWatchlistService>();
builder.Services.AddSingleton<PremiumService>();
builder.Services.AddSingleton<GmgnClient>();
builder.Services.AddSingleton<AutoTradingSettingsService>();
builder.Services.AddSingleton<IAutoTradingLaunchpadHandler, FourMemeAutoTradingHandler>();
builder.Services.AddSingleton<IAutoTradingLaunchpadHandler, FlapAutoTradingHandler>();
builder.Services.AddSingleton<IAutoTradingLaunchpadHandler, FlapRobinhoodAutoTradingHandler>();
builder.Services.AddSingleton<IAutoTradingLaunchpadHandler, PonsAutoTradingHandler>();
builder.Services.AddSingleton<IAutoTradingLaunchpadHandler, LongRobinhoodAutoTradingHandler>();
builder.Services.AddSingleton<AutoTradingService>();
builder.Services.AddSingleton<EvmWalletService>();
builder.Services.AddSingleton<TokenSettingsService>();
builder.Services.AddSingleton<LinkTokenSettingsService>();
builder.Services.AddSingleton<TokenPreviewService>();
builder.Services.AddSingleton<TokenCreationService>();
builder.Services.AddSingleton<FlapRwaCatalogService>();
builder.Services.AddSingleton<PostNotificationService>();
builder.Services.AddSingleton<TelegramNotificationService>();

// Các background service chạy độc lập và không phải chờ nhau hoàn thành.
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TelegramNotificationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TokenCreationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<AutoTradingService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PostNotificationService>());
builder.Services.AddHostedService<ApiHealthMonitorService>();
builder.Services.AddHostedService<BinanceAlphaMonitorService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<FlapRwaCatalogService>());
if (botOptions.EnableXMonitoring)
{
    builder.Services.AddHostedService<XRuleSyncService>();
    builder.Services.AddHostedService<XStreamService>();
    builder.Services.AddHostedService<XActivitySubscriptionSyncService>();
    builder.Services.AddHostedService<XActivityStreamService>();
}
builder.Services.AddHostedService<TelegramBotService>();

IHost app = builder.Build();
app.Run();
