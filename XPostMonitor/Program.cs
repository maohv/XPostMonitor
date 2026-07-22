using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Services.Telegram;
using XPostMonitor.Services.X;
using XPostMonitor.Services.X.Channels;
using XPostMonitor.Services.X.Notifications;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Ẩn log kỹ thuật dài dòng; lỗi do ứng dụng bắt được vẫn hiển thị ở console.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

BotOptions botOptions = new BotOptions();
builder.Configuration.GetSection(BotOptions.SectionName).Bind(botOptions);
builder.Services.AddSingleton(botOptions);

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

// Các service xử lý chức năng thông thường.
builder.Services.AddSingleton<WatchlistService>();
builder.Services.AddSingleton<ChannelWatchlistService>();
builder.Services.AddSingleton<PostNotificationService>();
builder.Services.AddSingleton<TelegramNotificationService>();

// Các background service chạy độc lập và không phải chờ nhau hoàn thành.
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TelegramNotificationService>());
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<PostNotificationService>());
builder.Services.AddHostedService<XRuleSyncService>();
builder.Services.AddHostedService<XStreamService>();
builder.Services.AddHostedService<TelegramBotService>();

IHost app = builder.Build();
app.Run();
