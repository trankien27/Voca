using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Voca.Services;

/// <summary>
/// libVLC, the engine of the video player for what Windows cannot play itself (mkv with 10-bit video, AC3 sound…)
/// and for choosing the audio track. It is not shipped inside Voca.exe: the 64-bit part of VideoLAN's official
/// NuGet package is fetched once into the data folder — only the files needed, read straight out of the package with
/// range requests (~45 MB of its 134 MB) — and checked against a fixed hash before it is used.
/// </summary>
public static class VlcRuntime
{
    public const string Version = "3.0.23.1";
    public const int DownloadMb = 45;
    private const string PackageUrl = "https://api.nuget.org/v3-flatcontainer/videolan.libvlc.windows/" + Version + "/videolan.libvlc.windows." + Version + ".nupkg";
    private const string Prefix = "build/x64/";

    /// <summary>SHA-256 of every file kept (<see cref="Hash"/>), worked out from the package as NuGet signed it.</summary>
    internal const string ExpectedHash = "b7742b1070cd0bdeed255c10af90de8b52dba36b4530be9dcd741340d396a690";

    private static readonly object InitLock = new();
    private static bool _initialized;

    public static string Folder(string dataFolder) => Path.Combine(dataFolder, "vlc", Version);

    public static bool IsInstalled(string dataFolder) => File.Exists(Path.Combine(Folder(dataFolder), "ready"));

    /// <summary>The files of the package Voca needs: the two libraries and the plugins (not the Lua web interface, headers or import libraries).</summary>
    internal static bool Needed(string entry) =>
        entry.StartsWith(Prefix, StringComparison.Ordinal) && !entry.EndsWith('/')
        && !entry.StartsWith(Prefix + "lua/", StringComparison.Ordinal)
        && !entry.StartsWith(Prefix + "include/", StringComparison.Ordinal)
        && !entry.EndsWith(".lib", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads libVLC from the data folder (once per run).</summary>
    public static void Initialize(string dataFolder)
    {
        lock (InitLock)
        {
            if (_initialized) return;
            LibVLCSharp.Shared.Core.Initialize(Folder(dataFolder));
            _initialized = true;
        }
    }

    /// <summary>
    /// Downloads and unpacks libVLC. Everything goes to a ".part" folder first and is moved into place only once
    /// the hash matches, so a cancelled or broken download leaves nothing that looks installed.
    /// </summary>
    public static async Task InstallAsync(string dataFolder, IProgress<double> progress, CancellationToken cancel)
    {
        var target = Folder(dataFolder);
        var part = target + ".part";
        if (Directory.Exists(part)) Directory.Delete(part, recursive: true);
        Directory.CreateDirectory(part);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Voca");
            await using var package = await HttpRangeStream.OpenAsync(http, PackageUrl, cancel);
            var hash = await Task.Run(() => Unpack(package, part, progress, cancel), cancel);
            if (hash != ExpectedHash)
                throw new InvalidDataException("Bộ phát VLC tải về không khớp mã kiểm tra, đã bỏ đi. Thử lại sau.");
            File.WriteAllText(Path.Combine(part, "ready"), Version);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(part, target);
            progress.Report(1);
        }
        finally
        {
            if (Directory.Exists(part)) try { Directory.Delete(part, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Writes the needed files of <paramref name="package"/> under <paramref name="folder"/> and returns their <see cref="Hash"/>.</summary>
    internal static string Unpack(Stream package, string folder, IProgress<double>? progress, CancellationToken cancel)
    {
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.Where(e => Needed(e.FullName)).OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
        var total = Math.Max(1, entries.Sum(e => e.CompressedLength));
        long done = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(folder, entry.FullName[Prefix.Length..]));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đường dẫn lạ trong gói VLC.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            sha.AppendData(Encoding.UTF8.GetBytes(entry.FullName + "\n"));
            using (var source = entry.Open())
            using (var file = File.Create(path))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = source.Read(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    file.Write(buffer, 0, read);
                }
            }
            done += entry.CompressedLength;
            progress?.Report(Math.Min(0.99, (double)done / total));
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// A read-only, seekable view of a file on a web server that answers range requests, read in blocks of 1 MB
    /// as asked (a zip is read from its end, then entry by entry), so only the parts used are downloaded.
    /// </summary>
    private sealed class HttpRangeStream(HttpClient http, string url, long length, CancellationToken cancel) : Stream
    {
        private const int BlockSize = 1 << 20;
        private long _position;
        private long _blockStart = -1;
        private byte[] _block = [];

        public static async Task<HttpRangeStream> OpenAsync(HttpClient http, string url, CancellationToken cancel)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(head, cancel);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? throw new IOException("Máy chủ không cho biết kích thước gói VLC.");
            return new HttpRangeStream(http, url, length, cancel);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = Math.Clamp(value, 0, length); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= length || count == 0) return 0;
            if (_blockStart < 0 || _position < _blockStart || _position >= _blockStart + _block.Length) Fetch(_position);
            var n = (int)Math.Min(count, _blockStart + _block.Length - _position);
            Array.Copy(_block, _position - _blockStart, buffer, offset, n);
            _position += n;
            return n;
        }

        private void Fetch(long start)
        {
            var end = Math.Min(length, start + BlockSize) - 1;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    using var response = http.Send(request, cancel);
                    if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        throw new IOException("Máy chủ không hỗ trợ tải từng phần.");
                    using var body = response.Content.ReadAsStream();
                    var block = new byte[end - start + 1];
                    body.ReadExactly(block);
                    (_blockStart, _block) = (start, block);
                    return;
                }
                catch (Exception ex) when (attempt < 3 && !cancel.IsCancellationRequested && ex is IOException or HttpRequestException or TaskCanceledException)
                {
                    Thread.Sleep(1000 * attempt);
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
