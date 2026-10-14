using System.IO;
using System.Security.Cryptography;
using System.Text;
using Voca.Models;

namespace Voca.Services;

/// <summary>
/// A video the phone may play: its file, the .srt next to it (if any) and how far it was watched. <see cref="Group"/>
/// is what it is listed under (a film's folder, or the folder it sits in); <see cref="Film"/> the film's name when it
/// belongs to the film folder; <see cref="Label"/> its short name within the group ("E01").
/// </summary>
public sealed record PhoneVideo(string Id, string Path, string Group, string? Film, string Label, string? Srt, VideoProgress? Progress);

/// <summary>
/// The videos the phone page lists and plays. First the films of the film folder (Cài đặt → Thư mục phim): each
/// subfolder is a film named after it, its videos (also in its subfolders) are the episodes; a video lying loose in
/// the film folder is a film of its own. Then the files of every other folder a video was watched from or made a word
/// set from on this computer, plus the folders added by hand. Only these files are served — a phone cannot ask for
/// any other file of the computer. Each file is known to the phone by an id derived from its path.
/// </summary>
public static class PhoneVideos
{
    /// <summary>Formats a phone browser can play (mp4/H.264 everywhere); others are listed with a warning.</summary>
    private static readonly string[] PhoneFriendly = [".mp4", ".m4v", ".mov", ".webm", ".mp3", ".m4a", ".wav"];

    public static string Id(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToLowerInvariant())))[..16].ToLowerInvariant();

    public static bool PlaysOnPhone(string path) => PhoneFriendly.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static bool IsMedia(string path) => WatchProgress.MediaExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string Full(string folder) => System.IO.Path.GetFullPath(folder).TrimEnd('\\');

    private static bool IsInside(string path, string folder) =>
        Full(path).StartsWith(Full(folder) + "\\", StringComparison.OrdinalIgnoreCase) || string.Equals(Full(path), Full(folder), StringComparison.OrdinalIgnoreCase);

    /// <summary>Other folders to list, most recently watched first, then the ones added by hand.</summary>
    public static List<string> Folders(AppData data, WatchHistory history) =>
        history.Videos.OrderByDescending(v => v.WatchedAt).Select(v => v.Video)
            .Concat(data.VideoWordSets.OrderByDescending(s => s.CreatedAt).Select(s => s.Video))
            .Select(System.IO.Path.GetDirectoryName)
            .Concat(data.Settings.PhoneVideoFolders)
            .OfType<string>()
            .Where(f => f.Length > 0)
            .Select(Full)
            .DistinctBy(f => f.ToLowerInvariant())
            .ToList();

    /// <summary>The films and other videos, each group in episode order.</summary>
    public static List<PhoneVideo> List(AppData data, WatchHistory history)
    {
        var progress = history.Videos.GroupBy(v => v.Video.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var videos = new List<PhoneVideo>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IEnumerable<string> files, string group, string? film)
        {
            var media = files.Where(IsMedia).Order(WatchProgress.EpisodeOrder).Where(listed.Add).ToList();
            var labels = Labels(media);
            for (var i = 0; i < media.Count; i++)
            {
                var srt = System.IO.Path.ChangeExtension(media[i], ".srt");
                videos.Add(new PhoneVideo(Id(media[i]), media[i], group, film, labels[i], File.Exists(srt) ? srt : null,
                    progress.GetValueOrDefault(media[i].ToLowerInvariant())));
            }
        }

        var root = data.Settings.FilmFolder.Trim();
        if (root.Length > 0 && Directory.Exists(root))
        {
            foreach (var film in SafeDirectories(root).Order(WatchProgress.EpisodeOrder))
                Add(SafeFiles(film, recursive: true), film, System.IO.Path.GetFileName(film));
            foreach (var movie in SafeFiles(root, recursive: false).Where(IsMedia))
                Add([movie], movie, System.IO.Path.GetFileNameWithoutExtension(movie));
        }
        foreach (var folder in Folders(data, history).Where(f => root.Length == 0 || !IsInside(f, root)))
            Add(SafeFiles(folder, recursive: false), folder, null);
        return videos;
    }

    private static readonly EnumerationOptions Quiet = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

    private static string[] SafeDirectories(string folder)
    {
        try { return Directory.GetDirectories(folder, "*", Quiet); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static string[] SafeFiles(string folder, bool recursive)
    {
        try { return Directory.GetFiles(folder, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = recursive, AttributesToSkip = Quiet.AttributesToSkip }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    /// <summary>
    /// Short names for the episodes of one group: the start their file names share is left out, cut at a separator
    /// ("Naruto_Shippuden_[English]_720P_S01_E01" … → "E01"), and underscores read as spaces.
    /// </summary>
    public static List<string> Labels(IReadOnlyList<string> files)
    {
        var names = files.Select(f => System.IO.Path.GetFileNameWithoutExtension(f)).ToList();
        var cut = 0;
        if (names.Count > 1)
        {
            var shared = names.Aggregate((a, b) => a[..a.Zip(b).TakeWhile(p => char.ToLowerInvariant(p.First) == char.ToLowerInvariant(p.Second)).Count()]);
            cut = shared.LastIndexOfAny(['_', ' ', '-', '.']) + 1;
            if (names.Any(n => n.Length <= cut)) cut = 0;
        }
        return names.Select(n => n[cut..].Replace('_', ' ').Trim()).ToList();
    }

    public static string ContentType(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".wmv" => "video/x-ms-wmv",
        ".mp3" => "audio/mpeg",
        ".m4a" => "audio/mp4",
        ".wav" => "audio/wav",
        _ => "application/octet-stream"
    };

    /// <summary>The subtitles as WebVTT, for the phone's own full-screen player (which cannot show the page's subtitles).</summary>
    public static string ToVtt(IEnumerable<SubtitleLine> lines)
    {
        static string Time(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
        var vtt = new StringBuilder("WEBVTT\n\n");
        foreach (var line in lines)
            vtt.Append($"{Time(line.Start)} --> {Time(line.End)}\n{line.Text.Replace("-->", "->")}\n\n");
        return vtt.ToString();
    }

    /// <summary>
    /// Which part of the file a "Range: bytes=…" header asks for: (start, length). The whole file when there is
    /// no header; null when the range cannot be served (416).
    /// </summary>
    public static (long Start, long Length)? Range(string? header, long size)
    {
        if (string.IsNullOrWhiteSpace(header)) return (0, size);
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var spec = header[6..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return null;
        var (first, last) = (spec[..dash].Trim(), spec[(dash + 1)..].Trim());
        if (first.Length == 0)
        {
            // "bytes=-500": the last 500 bytes.
            if (!long.TryParse(last, out var suffix) || suffix <= 0) return null;
            suffix = Math.Min(suffix, size);
            return (size - suffix, suffix);
        }
        if (!long.TryParse(first, out var start) || start < 0 || start >= size) return null;
        var end = last.Length == 0 ? size - 1 : long.TryParse(last, out var e) && e >= start ? Math.Min(e, size - 1) : -1;
        return end < 0 ? null : (start, end - start + 1);
    }
}
