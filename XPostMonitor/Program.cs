using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Services;
using XPostMonitor.Services.Flux;
using XPostMonitor.Services.Gmgn;
using XPostMonitor.Services.Launchpads;
using XPostMonitor.Services.Launchpads.DyorStable;
using XPostMonitor.Services.Launchpads.FourMeme;
using XPostMonitor.Services.OpenAi;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.Telegram.Localization;
using XPostMonitor.Services.Tokens;
using XPostMonitor.Services.Wallets;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Channels;
using XPostMonitor.Services.X.Notifications;

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

FourMemeOptions fourMemeOptions = new FourMemeOptions();
builder.Configuration.GetSection(FourMemeOptions.SectionName).Bind(fourMemeOptions);
builder.Services.AddSingleton(fourMemeOptions);

DyorStableOptions dyorStableOptions = new DyorStableOptions();
builder.Configuration.GetSection(DyorStableOptions.SectionName).Bind(dyorStableOptions);
builder.Services.AddSingleton(dyorStableOptions);

EvmNetworksOptions evmNetworksOptions = new EvmNetworksOptions();
builder.Configuration.GetSection(EvmNetworksOptions.SectionName).Bind(evmNetworksOptions);
builder.Services.AddSingleton(evmNetworksOptions);

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

builder.Services.AddHttpClient<FourMemeClient>(client =>
{
    client.BaseAddress = new Uri("https://four.meme/meme-api/v1/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddHttpClient<DyorStableClient>(client =>
{
    client.BaseAddress = new Uri("https://api.pinata.cloud/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Các service xử lý chức năng thông thường.
builder.Services.AddSingleton<WatchlistService>();
builder.Services.AddSingleton<BotTextService>();
builder.Services.AddSingleton<LanguageMenuService>();
builder.Services.AddSingleton<ManualTokenMenuService>();
builder.Services.AddSingleton<WatchlistMenuService>();
builder.Services.AddSingleton<TokenSettingsMenuService>();
builder.Services.AddSingleton<ChannelWatchlistService>();
builder.Services.AddSingleton<PremiumService>();
builder.Services.AddSingleton<GmgnClient>();
builder.Services.AddSingleton<EvmWalletService>();
builder.Services.AddSingleton<TokenSettingsService>();
builder.Services.AddSingleton<TokenPreviewService>();
builder.Services.AddSingleton<TokenCreationService>();
builder.Services.AddSingleton<PostNotificationService>();
builder.Services.AddSingleton<TelegramNotificationService>();

// Các background service chạy độc lập và không phải chờ nhau hoàn thành.
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TelegramNotificationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TokenCreationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PostNotificationService>());
builder.Services.AddHostedService<XRuleSyncService>();
builder.Services.AddHostedService<XStreamService>();
builder.Services.AddHostedService<XActivitySubscriptionSyncService>();
builder.Services.AddHostedService<XActivityStreamService>();
builder.Services.AddHostedService<TelegramBotService>();

IHost app = builder.Build();
app.Run();
