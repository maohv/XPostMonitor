# Luồng chạy của service

## Telegram

- `TelegramApiClient`: gọi Telegram HTTP API.
- `TelegramBotService`: nhận và xử lý `/add`, `/remove`, `/list`.
- `TelegramNotificationService`: xếp hàng và gửi thông báo bằng 10 worker.
- `BotCommand`: tách nội dung tin nhắn thành lệnh và tham số.

## X

- `X/Channels/ChannelWatchlistService`: quản lý riêng các lệnh Channel, không dùng watchlist cá nhân.

- `XApiClient`: gọi X API.
- `WatchlistService`: thêm, xóa và đọc danh sách theo dõi.
- `XRuleSyncService`: đồng bộ watchlist thành Filtered Stream rules.
- `XRuleTag`: nối rule của X với account trong database.

## Thông báo Post

Luồng chính nằm trong thư mục `X/Notifications`:

```text
XStreamService
    nhận Post mới từ X
        |
        v
PostNotificationService
    chống trùng bằng LastPostId
    tìm Telegram user đang theo dõi
        |
        v
TelegramNotificationService
    gửi nhiều thông báo song song
```

Queue nằm bên trong service tương ứng. Vì vậy X Stream không phải chờ database hoặc Telegram gửi xong.
