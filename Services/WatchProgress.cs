using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Voca.Models;

namespace Voca.Services;

/// <summary>What "Xem tiếp" opens: a video and the second to start at.</summary>
public sealed record ContinueWatching(string Video, TimeSpan Start, bool NextEpisode);

/// <summary>
/// Remembers where each video was left off so playing it again (or "Xem tiếp") starts at that second; once a
/// video is watched to the end, "Xem tiếp" moves on to the next episode in its folder. The player saves every
/// few seconds into watch.json (<see cref="Load"/>, <see cref="Write"/>), so a crash loses only those seconds.
/// </summary>
public static partial class WatchProgress
{
    public const string FileName = "watch.json";

    /// <summary>Files the video tab can play (audio included).</summary>
    public static readonly string[] MediaExtensions = [".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".mp3", ".m4a", ".wav"];

    /// <summary>Closer than this to the end counts as watched to the end (end credits).</summary>
    public static readonly TimeSpan EndMargin = TimeSpan.FromSeconds(30);

    /// <summary>Videos remembered; the oldest are forgotten beyond this.</summary>
    public const int Keep = 200;

    /// <summary>Records where playing stopped; the video becomes the first one for "Xem tiếp".</summary>
    public static void Save(WatchHistory history, string video, TimeSpan position, TimeSpan length, DateTime now)
    {
        var path = Path.GetFullPath(video);
        history.Videos.RemoveAll(p => Same(p.Video, path));
        var finished = length > TimeSpan.Zero && position >= length - EndMargin;
        history.Videos.Insert(0, new VideoProgress
        {
            Video = path,
            Seconds = Math.Max(0, position.TotalSeconds),
            LengthSeconds = Math.Max(0, length.TotalSeconds),
            Finished = finished,
            WatchedAt = now
        });
        if (history.Videos.Count > Keep) history.Videos.RemoveRange(Keep, history.Videos.Count - Keep);
    }

    /// <summary>Where to start this video: where it was left off, or the start when never played, barely begun or finished.</summary>
    public static TimeSpan ResumeAt(WatchHistory history, string video)
    {
        var path = Path.GetFullPath(video);
        var saved = history.Videos.FirstOrDefault(p => Same(p.Video, path));
        return saved is null || saved.Finished || saved.Seconds < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(saved.Seconds);
    }

    /// <summary>
    /// The most recently watched video that still exists, at the second it was left off; when it was watched to
    /// the end, the next episode in its folder from the start (null when there is none).
    /// </summary>
    public static ContinueWatching? Continue(WatchHistory history, Func<string, bool>? exists = null, Func<string, IEnumerable<string>>? filesIn = null)
    {
        exists ??= File.Exists;
        var last = history.Videos.FirstOrDefault(p => exists(p.Video));
        if (last is null) return null;
        if (!last.Finished) return new ContinueWatching(last.Video, TimeSpan.FromSeconds(last.Seconds), false);
        return NextEpisode(last.Video, filesIn) is { } next ? new ContinueWatching(next, ResumeAt(history, next), true) : null;
    }

    /// <summary>The media file after this one in its folder, in natural order ("E2" before "E10"); null after the last.</summary>
    public static string? NextEpisode(string video, Func<string, IEnumerable<string>>? filesIn = null)
    {
        var folder = Path.GetDirectoryName(video);
        if (folder is null) return null;
        IEnumerable<string> files;
        try { files = (filesIn ?? Directory.EnumerateFiles)(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        var media = files.Where(f => MediaExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(NaturalOrder.Instance).ToList();
        var at = media.FindIndex(f => Same(f, video));
        return at >= 0 && at + 1 < media.Count ? media[at + 1] : null;
    }

    // ---------- watch.json ----------

    /// <summary>The history saved in <paramref name="folder"/>; empty when there is none yet or it cannot be read.</summary>
    public static WatchHistory Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<WatchHistory>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>Saves the history: written to a temp file first and swapped in, so a crash mid-write keeps the previous one.</summary>
    public static void Write(string folder, WatchHistory history)
    {
        var path = Path.Combine(folder, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(history));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>Loads, records one position and writes back; false when the file could not be written (tried again next time).</summary>
    public static bool Record(string folder, string video, TimeSpan position, TimeSpan length, DateTime now)
    {
        var history = Load(folder);
        Save(history, video, position, length, now);
        try { Write(folder, history); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>File names compared with their numbers as numbers: "Ep 2" before "Ep 10".</summary>
    private sealed partial class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Chunks().Matches(Path.GetFileNameWithoutExtension(x ?? ""));
            var b = Chunks().Matches(Path.GetFileNameWithoutExtension(y ?? ""));
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var (p, q) = (a[i].Value, b[i].Value);
                var order = char.IsDigit(p[0]) && char.IsDigit(q[0])
                    ? p.TrimStart('0').Length != q.TrimStart('0').Length
                        ? p.TrimStart('0').Length.CompareTo(q.TrimStart('0').Length)
                        : string.CompareOrdinal(p.TrimStart('0'), q.TrimStart('0'))
                    : string.Compare(p, q, StringComparison.OrdinalIgnoreCase);
                if (order != 0) return order;
            }
            return a.Count != b.Count ? a.Count.CompareTo(b.Count) : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        [GeneratedRegex(@"\d+|\D+")]
        private static partial Regex Chunks();
    }
}
