using Microsoft.EntityFrameworkCore;
using XPostMonitor.Configuration;
using XPostMonitor.Data;
using XPostMonitor.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Doc Telegram token va X Bearer token tu cau hinh.
BotOptions botOptions = new BotOptions();
builder.Configuration.GetSection(BotOptions.SectionName).Bind(botOptions);
builder.Services.AddSingleton(botOptions);

//Dang ky cac service cua ung dung.
builder.Services.AddHttpClient<XApiClient>(client => client.BaseAddress = new Uri("https://api.x.com/"));
builder.Services.AddHostedService<TelegramBotService>();

//Dang ky Web API va Swagger.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();