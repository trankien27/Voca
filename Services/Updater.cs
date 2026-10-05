using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Voca.Services;

/// <summary>A release on GitHub that carries Voca.exe.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string ExeUrl, long Size, string? Sha256Url, string Notes,
    DateTime? PublishedAt = null);

/// <summary>
/// Updates from GitHub Releases (trankien27/Voca, public, so no token). The learner picks a version from
/// the list (newer, or older to go back); a release carries <c>Voca.exe</c> and <c>Voca.exe.sha256</c>
/// and the download is used only if its hash and version match. Installing swaps the running exe
/// (renamed to <c>.old</c>, which Windows allows) and starts the new one. The learner's data lives
/// elsewhere (%LOCALAPPDATA%\Voca) and is not touched.
/// </summary>
public static partial class Updater
{
    public const string Repo = "trankien27/Voca";
    public const string AssetName = "Voca.exe";
    public const string HashAssetName = "Voca.exe.sha256";
    public const string AfterUpdateArg = "--after-update";
    public static readonly string ReleasesUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=50";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";

    public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voca", "update");

    /// <summary>
    /// Only a published build updates itself; a build run from the project (bin\Debug, bin\Release)
    /// must not be overwritten by a release.
    /// </summary>
    public static bool Enabled(string? exePath = null)
    {
        exePath ??= Environment.ProcessPath;
        if (exePath is null || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var folder = Path.GetDirectoryName(exePath) ?? "";
        return !folder.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    /// <summary>"v2.7.0" or "2.7.0" → 2.7.0.0; anything else → null.</summary>
    public static Version? ParseTag(string? tag)
    {
        var text = tag?.Trim().TrimStart('v', 'V') ?? "";
        return Version.TryParse(text, out var v) && v.Major >= 0 ? Normalize(v) : null;
    }

    /// <summary>The first 64-hex-digit token (the format of sha256sum and of release.ps1).</summary>
    public static string? ParseSha256(string text)
    {
        var match = Sha256Pattern().Match(text);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{64}\b")]
    private static partial Regex Sha256Pattern();

    /// <summary>Reads GitHub's release list: usable releases only, newest version first.</summary>
    public static List<UpdateInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return doc.RootElement.EnumerateArray().Select(ParseRelease).OfType<UpdateInfo>()
            .GroupBy(r => r.Version).Select(g => g.First())
            .OrderByDescending(r => r.Version).ToList();
    }

    /// <summary>Reads one release; null when it is a draft/pre-release or has no Voca.exe.</summary>
    public static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement);
    }

    private static UpdateInfo? ParseRelease(JsonElement root)
    {
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (ParseTag(tag) is not { } version) return null;
        string? exeUrl = null, hashUrl = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    exeUrl = url;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                }
                else if (string.Equals(name, HashAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    hashUrl = url;
                }
            }
        }
        if (exeUrl is null) return null;
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        DateTime? published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                              && p.TryGetDateTime(out var at) ? at.ToLocalTime() : null;
        return new UpdateInfo(version, tag!, exeUrl, size, hashUrl, notes, published);
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static readonly Lazy<HttpClient> SharedHttp = new(CreateClient);
    public static HttpClient Http => SharedHttp.Value;

    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Voca-Updater/{Current}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>All usable releases on GitHub, newest first (empty when there are none yet).</summary>
    public static async Task<List<UpdateInfo>> ListAsync(HttpClient http, CancellationToken cancel = default)
    {
        using var response = await http.GetAsync(ReleasesUrl, cancel);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        return ParseReleases(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>
    /// Downloads the release into <paramref name="folder"/> and returns the file only if the size, SHA-256 and
    /// the exe's own version all match. <paramref name="progress"/> gets 0–1.
    /// </summary>
    public static async Task<string> DownloadAsync(HttpClient http, UpdateInfo info, IProgress<double>? progress = null,
        string? folder = null, CancellationToken cancel = default)
    {
        folder ??= DefaultFolder;
        Directory.CreateDirectory(folder);
        if (info.Sha256Url is null) throw new InvalidOperationException($"Bản {info.Tag} thiếu {HashAssetName}, không thể kiểm tra file tải về.");

        var expectedHash = ParseSha256(await http.GetStringAsync(info.Sha256Url, cancel))
                           ?? throw new InvalidOperationException($"{HashAssetName} không đúng định dạng.");
        var part = Path.Combine(folder, $"Voca-{info.Version}.exe.part");
        using (var response = await http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, cancel))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? info.Size;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = File.Create(part);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        try
        {
            Verify(part, info, expectedHash);
            var exeVersion = FileVersionInfo.GetVersionInfo(part);
            if (exeVersion.ProductName != "Voca" || ParseTag(exeVersion.FileVersion) != info.Version)
                throw new InvalidOperationException($"File tải về không phải Voca {info.Version.ToString(3)}.");
        }
        catch
        {
            File.Delete(part);
            throw;
        }
        var final = Path.Combine(folder, $"Voca-{info.Version}.exe");
        File.Move(part, final, overwrite: true);
        return final;
    }

    /// <summary>Checks size (when known) and SHA-256 of a downloaded file.</summary>
    public static void Verify(string path, UpdateInfo info, string expectedSha256)
    {
        var length = new FileInfo(path).Length;
        if (info.Size > 0 && length != info.Size)
            throw new InvalidOperationException($"Tải về chưa đủ ({length:N0}/{info.Size:N0} byte).");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (actual != expectedSha256.ToLowerInvariant())
            throw new InvalidOperationException("Mã kiểm tra (SHA-256) không khớp, file tải về có thể bị hỏng.");
    }

    /// <summary>
    /// Puts <paramref name="newExe"/> in place of <paramref name="exePath"/>. The running exe is renamed to
    /// <c>.old</c> (removed on the next start); if the copy fails the old exe is put back.
    /// </summary>
    public static void Swap(string exePath, string newExe)
    {
        var old = exePath + ".old";
        TryDelete(old);
        File.Move(exePath, old);
        try
        {
            File.Copy(newExe, exePath);
        }
        catch
        {
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>Installs a downloaded exe in place of this one and starts it; the caller then shuts down.</summary>
    public static void InstallAndRestart(string downloadedExe)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Không xác định được file Voca.exe đang chạy.");
        Swap(exePath, downloadedExe);
        Process.Start(new ProcessStartInfo(exePath, $"{AfterUpdateArg} {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>After an update: waits for the old process to exit so it can take over the single-instance lock.</summary>
    public static void WaitForPreviousProcess(string[] args)
    {
        var index = Array.IndexOf(args, AfterUpdateArg);
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid)) return;
        try
        {
            using var previous = Process.GetProcessById(pid);
            previous.WaitForExit(15000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>Removes the previous exe and old downloads (an installed download has been copied into place).</summary>
    public static void CleanUp(string? exePath = null, string? folder = null)
    {
        exePath ??= Environment.ProcessPath;
        if (exePath is not null) TryDelete(exePath + ".old");
        folder ??= DefaultFolder;
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.GetFiles(folder)) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* still in use; next start retries */ }
    }
}
