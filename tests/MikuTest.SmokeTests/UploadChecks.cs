using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using MikuTest.Web.Data;
using MikuTest.Web.Services;

static class UploadChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mikutest-upload-" + Guid.NewGuid().ToString("N"));
        var env = new UploadEnvironment { ContentRootPath = root };
        var service = new MediaUploadService(env, new TestManagementAccess());
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82 };
        try
        {
            var first = await service.SaveAsync(new UploadFile("../../image.png", bytes));
            var second = await service.SaveAsync(new UploadFile("../../image.png", bytes));
            var file = Path.Combine(root, "App_Data", "uploads", Path.GetFileName(first.Url));
            if (first.Url == second.Url || !(await File.ReadAllBytesAsync(file)).SequenceEqual(bytes))
                throw new Exception("Upload bytes or generated names incorrect");
            await Reject(() => service.SaveAsync(new UploadFile("bad.png", new byte[20])));
            await Reject(() => service.SaveAsync(new UploadFile("script.svg", bytes)));
            await Reject(() => service.SaveAsync(new UploadFile("huge.png", bytes, 2L * 1024 * 1024 + 1)));
            await Reject(() => service.SaveAsync(new UploadFile("huge.wav", bytes, 5L * 1024 * 1024 + 1)));
            await Reject(() => service.SaveAsync(new UploadFile("huge.mp4", bytes, 15L * 1024 * 1024 + 1)));
            await Reject(() =>
                ImageUploadPreparation.PrepareAsync(
                    new UploadFile("huge.png", bytes, 20L * 1024 * 1024 + 1),
                    CancellationToken.None
                )
            );
            var gif = new UploadFile("animation.gif", bytes);
            if (!ReferenceEquals(gif, await ImageUploadPreparation.PrepareAsync(gif, CancellationToken.None)))
                throw new Exception("GIF animation was converted");
            var animatedWebp = new byte[32];
            "RIFF"u8.CopyTo(animatedWebp);
            "WEBPVP8X"u8.CopyTo(animatedWebp.AsSpan(8));
            animatedWebp[20] = 2;
            var webp = new UploadFile("animation.webp", animatedWebp);
            if (
                !ReferenceEquals(
                    webp,
                    await ImageUploadPreparation.PrepareAsync(webp, CancellationToken.None)
                )
            )
                throw new Exception("WebP animation was converted");
            await Reject(() => service.SaveAsync(new UploadFile("truncated.png", bytes, 40)));
            await Reject(() =>
                new MediaUploadService(env, new TestManagementAccess(false)).SaveAsync(
                    new UploadFile("a.png", bytes)
                )
            );
            if (Directory.GetFiles(Path.Combine(root, "App_Data", "uploads")).Length != 2)
                throw new Exception("Failed upload left a file behind");
            foreach (var (name, limit) in new[] { ("edge.png", 2), ("edge.wav", 5), ("edge.mp4", 15) })
            {
                var content = new byte[limit * 1024 * 1024];
                if (name.EndsWith("png"))
                    bytes.CopyTo(content, 0);
                else if (name.EndsWith("wav"))
                {
                    "RIFF"u8.CopyTo(content);
                    "WAVE"u8.CopyTo(content.AsSpan(8));
                }
                else
                    "ftyp"u8.CopyTo(content.AsSpan(4));
                await service.SaveAsync(new UploadFile(name, content));
            }
            Console.WriteLine(
                "PASS: exact 2/5/15 MB boundaries accepted, one-byte-over rejected; animation preserved and source image cap enforced"
            );
            Console.WriteLine(
                "PASS: upload preserves bytes, generates unique paths, rejects bad format, oversized, truncated and unauthorized files, cleans failures"
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static async Task Reject(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Exception("Unsafe upload accepted");
    }

    private sealed class UploadAuthentication(bool administrator) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(
                new AuthenticationState(
                    new ClaimsPrincipal(
                        new ClaimsIdentity(
                            administrator
                                ? new[] { new Claim(ClaimTypes.Role, AppRoles.Administrator) }
                                : Array.Empty<Claim>(),
                            "test"
                        )
                    )
                )
            );
    }

    private sealed class UploadFile(string name, byte[] bytes, long? size = null) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => size ?? bytes.Length;
        public string ContentType => "image/png";

        public Stream OpenReadStream(
            long maxAllowedSize = 512000,
            CancellationToken cancellationToken = default
        ) => new MemoryStream(bytes);
    }

    private sealed class UploadEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
