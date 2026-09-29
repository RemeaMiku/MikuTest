using Microsoft.AspNetCore.Components.Forms;

namespace MikuTest.Web.Services;

public static class ImageUploadPreparation
{
    public static async Task<IBrowserFile> PrepareAsync(
        IBrowserFile original,
        CancellationToken cancellationToken
    )
    {
        var extension = Path.GetExtension(original.Name).ToLowerInvariant();
        // Preserve GIF animation. Audio/video are size-checked by the storage service.
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            return original;
        if (original.Size <= 0 || original.Size > 20L * 1024 * 1024)
            throw new InvalidOperationException("待压缩图片须小于 20 MB，请先缩小原图后重试。");
        if (extension == ".webp")
        {
            await using var stream = original.OpenReadStream(20L * 1024 * 1024, cancellationToken);
            var header = new byte[32];
            var count = await stream.ReadAtLeastAsync(header, 32, false, cancellationToken);
            if (count > 20 && header.AsSpan(12, 4).SequenceEqual("VP8X"u8) && (header[20] & 2) != 0)
                return original; // Do not turn animated WebP into a still image.
        }
        // Browser conversion keeps aspect ratio and alpha, and never enlarges a smaller image.
        foreach (var edge in new[] { 1600, 1280, 1024 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resized = await original.RequestImageFileAsync("image/webp", edge, edge);
            if (resized.Size <= 2L * 1024 * 1024)
            {
                var outputExtension = resized.ContentType switch
                {
                    "image/webp" => ".webp",
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    _ => throw new InvalidOperationException(
                        "浏览器无法转换此图片，请换用 PNG 或 JPG 图片。"
                    ),
                };
                return new ConvertedFile(resized, Path.ChangeExtension(original.Name, outputExtension));
            }
        }
        throw new InvalidOperationException("图片压缩后仍超过 2 MB，请裁剪不需要的区域后重试。");
    }

    private sealed class ConvertedFile(IBrowserFile file, string name) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => file.LastModified;
        public long Size => file.Size;
        public string ContentType => file.ContentType;

        public Stream OpenReadStream(
            long maxAllowedSize = 512000,
            CancellationToken cancellationToken = default
        ) => file.OpenReadStream(maxAllowedSize, cancellationToken);
    }
}
