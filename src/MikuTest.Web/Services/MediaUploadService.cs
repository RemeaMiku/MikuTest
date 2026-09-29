using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using MikuTest.Web.Data;
using MikuTest.Web.Models;

namespace MikuTest.Web.Services;

public sealed class MediaUploadService(IWebHostEnvironment environment, IManagementAccess access)
{
    public const string Accept = ".png,.jpg,.jpeg,.gif,.webp,.mp3,.wav,.ogg,.mp4,.webm";

    public async Task<QuestionMedia> SaveAsync(
        IBrowserFile file,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (await access.CanAsync(Permissions.QuestionCreate))
            await access.RequireAsync(Permissions.QuestionCreate);
        else
            await access.RequireAsync(Permissions.QuestionEdit);
        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        var kind = extension switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" => MediaKind.Image,
            ".mp3" or ".wav" or ".ogg" => MediaKind.Audio,
            ".mp4" or ".webm" => MediaKind.Video,
            _ => throw new InvalidOperationException(
                "不支持此格式，请上传 PNG、JPG、GIF、WebP、MP3、WAV、OGG、MP4 或 WebM。"
            ),
        };
        var limit =
            (
                kind == MediaKind.Image ? 2L
                : kind == MediaKind.Audio ? 5L
                : 15L
            )
            * 1024
            * 1024;
        if (file.Size <= 0)
            throw new InvalidOperationException("不能上传空文件。");
        if (file.Size > limit)
            throw new InvalidOperationException(
                kind == MediaKind.Image
                    ? "图片超过 2 MB，请裁剪图片或降低分辨率后重试。"
                    : $"{(kind == MediaKind.Audio ? "音频" : "视频")}超过 {limit / 1024 / 1024} MB，请截取答题所需片段或降低码率后重试。视频建议使用 720p。"
            );
        var directory = Path.Combine(environment.ContentRootPath, "App_Data", "uploads");
        Directory.CreateDirectory(directory);
        var name = Guid.NewGuid().ToString("N") + extension;
        // 先写临时文件并验证文件头，校验通过后才公开为可访问的媒体。
        var temporary = Path.Combine(directory, name + ".tmp");
        try
        {
            await using (var input = file.OpenReadStream(limit, cancellationToken))
            await using (
                var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    true
                )
            )
            {
                var header = new byte[16];
                var count = await input.ReadAtLeastAsync(header, header.Length, false, cancellationToken);
                if (!ValidHeader(extension, header.AsSpan(0, count)))
                    throw new InvalidOperationException("文件内容与格式不符，或文件已损坏。");
                await output.WriteAsync(header.AsMemory(0, count), cancellationToken);
                long total = count;
                var buffer = new byte[65536];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > limit)
                        throw new InvalidOperationException("文件超过上传大小限制。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Invoke(total);
                }
                if (total != file.Size)
                    throw new InvalidOperationException("文件传输不完整，请重新上传。");
            }
            File.Move(temporary, Path.Combine(directory, name));
            return new QuestionMedia
            {
                Kind = kind,
                Url = "/uploads/" + name,
                Description = "",
            };
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static bool ValidHeader(string extension, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
            return false;
        return extension switch
        {
            ".png" => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".jpg" or ".jpeg" => bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255,
            ".gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            ".webp" => bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            ".wav" => bytes.StartsWith("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WAVE"u8),
            ".mp3" => bytes.StartsWith("ID3"u8) || (bytes[0] == 255 && (bytes[1] & 0xE0) == 0xE0),
            ".ogg" => bytes.StartsWith("OggS"u8),
            ".mp4" => bytes.Slice(4, 4).SequenceEqual("ftyp"u8),
            ".webm" => bytes.StartsWith(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }),
            _ => false,
        };
    }
}
