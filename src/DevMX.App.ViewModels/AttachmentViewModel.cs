using DevMX.Core.Providers;

namespace DevMX.App.ViewModels;

/// <summary>
/// A pending attachment shown as a chip above the chat input, converted to a
/// ChatAttachment when the message is sent.
/// </summary>
public sealed class AttachmentViewModel
{
    public string FileName { get; }
    public string MediaType { get; }
    public string? Base64Data { get; }
    public string? TextContent { get; }

    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Chip label, e.g. "🖼 screenshot.png (48 KB)" or "📄 Program.cs (2 KB)".</summary>
    public string DisplayLabel
    {
        get
        {
            long bytes = Base64Data != null
                ? (long)(Base64Data.Length * 0.75)
                : System.Text.Encoding.UTF8.GetByteCount(TextContent ?? "");
            string size = bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024.0):0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";
            return $"{(IsImage ? "\U0001F5BC" : "\U0001F4C4")} {FileName} ({size})";
        }
    }

    private AttachmentViewModel(string fileName, string mediaType, string? base64Data, string? textContent)
    {
        FileName = fileName;
        MediaType = mediaType;
        Base64Data = base64Data;
        TextContent = textContent;
    }

    public static AttachmentViewModel ForImage(string fileName, string mediaType, string base64Data) =>
        new(fileName, mediaType, base64Data, null);

    public static AttachmentViewModel ForTextFile(string fileName, string textContent) =>
        new(fileName, "text/plain", null, textContent);

    public ChatAttachment ToChatAttachment() =>
        new(FileName, MediaType, Base64Data, TextContent);
}
