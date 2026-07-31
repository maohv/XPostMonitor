namespace XPostMonitor.Configuration;

public sealed class ImageGenerationOptions
{
    public const string SectionName = "ImageGeneration";

    // Model tạo ảnh dùng chung cho cả Auto Create và tạo thủ công.
    // Giá trị hợp lệ: OpenAi, Gemini, Flux hoặc ZImage.
    public string Provider { get; set; } = "OpenAi";

    // Auto Create được phép chuẩn bị tên và ảnh trong bao nhiêu giây.
    // Đặt bằng 0 nếu không muốn giới hạn thời gian.
    public int AutoCreateTimeoutSeconds { get; set; } = 10;

    // Tắt để không gửi thêm ảnh logo chain sang model tạo ảnh.
    // Màu sắc của chain và ảnh tham chiếu khuôn mặt vẫn được giữ nguyên.
    public bool EnableChainLogo { get; set; } = true;

}
