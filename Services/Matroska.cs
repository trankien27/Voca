using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Voca.Services;

public enum MkvTrackKind { Video, Audio, Subtitle, Other }

/// <summary>One byte of a file read changed (the file itself stays as it is).</summary>
public readonly record struct FilePatch(long Offset, byte Value);

/// <summary>A file read with a few bytes changed on the way (<see cref="Matroska.KeepAudio"/>); the file itself is not touched.</summary>
public sealed class PatchedStream(Stream inner, IReadOnlyList<FilePatch> patches) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var at = inner.Position;
        var read = inner.Read(buffer, offset, count);
        foreach (var patch in patches)
            if (patch.Offset >= at && patch.Offset < at + read) buffer[offset + patch.Offset - at] = patch.Value;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// One track of a Matroska file. <see cref="NumberOffset"/> is where its track number sits in the file
/// (<see cref="NumberLength"/> bytes), so Windows can be handed the file with other audio tracks hidden
/// (<see cref="Matroska.KeepAudio"/>).
/// </summary>
public sealed record MkvTrack(int Number, MkvTrackKind Kind, string Codec, string Language, string Name, bool Default, bool Forced,
    long NumberOffset, int NumberLength)
{
    /// <summary>Subtitles stored as text (SRT, ASS/SSA, WebVTT), which Voca can read; pictures (PGS, VobSub) cannot be.</summary>
    public bool IsText => Kind == MkvTrackKind.Subtitle && Codec is "S_TEXT/UTF8" or "S_TEXT/ASCII" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_ASS" or "S_SSA" or "S_TEXT/WEBVTT";

    public bool IsEnglish => Language is "eng" or "en" || Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Tiếng Anh · Signs & Songs", "Tiếng Nhật", "Track 3".</summary>
    public string Label
    {
        get
        {
            var language = LanguageName(Language);
            var parts = new List<string>();
            if (language.Length > 0) parts.Add(language);
            if (Name.Length > 0 && !Name.Equals(language, StringComparison.OrdinalIgnoreCase)) parts.Add(Name);
            if (Forced && Kind == MkvTrackKind.Subtitle) parts.Add("biển hiệu");
            return parts.Count > 0 ? string.Join(" · ", parts) : $"Track {Number}";
        }
    }

    /// <summary>
    /// Short names for a few tracks of one kind, for a narrow list: the language ("Tiếng Anh"), numbered when two
    /// share it ("Tiếng Anh 2"); the track's own name or number when the language is unknown.
    /// </summary>
    public static List<string> ShortLabels(IReadOnlyList<MkvTrack> tracks)
    {
        var names = tracks.Select(t => LanguageName(t.Language) is { Length: > 0 } language ? language
            : t.Name.Length > 0 ? t.Name : $"Track {t.Number}").ToList();
        var seen = new Dictionary<string, int>();
        return names.Select((name, i) =>
        {
            var n = seen[name] = seen.GetValueOrDefault(name) + 1;
            var label = names.Count(x => x == name) > 1 && n > 1 ? $"{name} {n}" : name;
            return tracks[i].Forced && tracks[i].Kind == MkvTrackKind.Subtitle ? label + " (biển hiệu)" : label;
        }).ToList();
    }

    private static string LanguageName(string code) => code.ToLowerInvariant().Split('-')[0] switch
    {
        "eng" or "en" => "Tiếng Anh",
        "jpn" or "ja" => "Tiếng Nhật",
        "vie" or "vi" => "Tiếng Việt",
        "kor" or "ko" => "Tiếng Hàn",
        "chi" or "zho" or "zh" => "Tiếng Trung",
        "fre" or "fra" or "fr" => "Tiếng Pháp",
        "ger" or "deu" or "de" => "Tiếng Đức",
        "spa" or "es" => "Tiếng Tây Ban Nha",
        "por" or "pt" => "Tiếng Bồ Đào Nha",
        "ita" or "it" => "Tiếng Ý",
        "rus" or "ru" => "Tiếng Nga",
        "ara" or "ar" => "Tiếng Ả Rập",
        "tha" or "th" => "Tiếng Thái",
        "ind" or "id" => "Tiếng Indonesia",
        "und" or "" => "",
        var other => other
    };
}

/// <summary>
/// Reads what Voca needs from a Matroska (.mkv) file without any outside tool: its tracks, and the lines of a text
/// subtitle track. Only the element headers are read while walking the file; blocks of other tracks are skipped.
/// </summary>
public static partial class Matroska
{
    private const uint EbmlHeader = 0x1A45DFA3, Segment = 0x18538067, Info = 0x1549A966, TimestampScale = 0x2AD7B1,
        Tracks = 0x1654AE6B, TrackEntry = 0xAE, TrackNumber = 0xD7, TrackType = 0x83, CodecId = 0x86, Language = 0x22B59C,
        LanguageBcp47 = 0x22B59D, Name = 0x536E, FlagDefault = 0x88, FlagForced = 0x55AA, ContentEncodings = 0x6D80,
        ContentEncoding = 0x6240, ContentCompression = 0x5034, ContentCompAlgo = 0x4254, ContentCompSettings = 0x4255,
        Crc32Element = 0xBF, Cluster = 0x1F43B675, Timestamp = 0xE7, SimpleBlock = 0xA3, BlockGroup = 0xA0, Block = 0xA1, BlockDuration = 0x9B;

    /// <summary>Level-1 elements: one of these ends a cluster of unknown size.</summary>
    private static readonly HashSet<uint> TopLevel = [Cluster, Info, Tracks, 0x1C53BB6B, 0x114D9B74, 0x1941A469, 0x1043A770, 0x1254C367];

    public static bool IsMatroska(string path) => Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    /// <summary>The tracks, in file order; empty when the file is not Matroska or cannot be read.</summary>
    public static List<MkvTrack> ReadTracks(string path)
    {
        try
        {
            using var file = Open(path);
            return ReadHead(file).Tracks.Select(t => t.Track).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException) { return []; }
    }

    /// <summary>
    /// The English text subtitles most worth reading: dialogue rather than "forced" signs, then the track marked
    /// default (the caller reads it with <see cref="ReadSubtitles"/>).
    /// </summary>
    public static MkvTrack? BestEnglishSubtitles(IReadOnlyList<MkvTrack> tracks) =>
        tracks.Where(t => t.IsText && t.IsEnglish)
            .OrderBy(t => t.Forced || SignsOnly().IsMatch(t.Name))
            .ThenByDescending(t => t.Default)
            .FirstOrDefault();

    /// <summary>
    /// The bytes to change so Windows reads audio track <paramref name="keep"/> as the only one: every other audio
    /// track gets a track number no block carries, so its sound is left out (Windows refuses a file whose track types
    /// were changed, but skips blocks of a track it does not know). The checksum of the track list, when it has one —
    /// Windows checks it — is worked out again. Nothing when there is nothing to hide.
    /// </summary>
    public static List<FilePatch> KeepAudio(string path, int keep)
    {
        using var file = Open(path);
        var head = ReadHead(file);
        var tracks = head.Tracks.Select(t => t.Track).ToList();
        var used = tracks.Select(t => (long)t.Number).ToHashSet();
        var patches = new List<FilePatch>();
        foreach (var track in tracks.Where(t => t.Kind == MkvTrackKind.Audio && t.Number != keep && t.NumberOffset >= 0))
        {
            var free = (1L << (8 * track.NumberLength)) - 1;
            while (free > 0 && used.Contains(free)) free--;
            if (free <= 0) continue;
            used.Add(free);
            for (var i = 0; i < track.NumberLength; i++)
                patches.Add(new FilePatch(track.NumberOffset + i, (byte)(free >> (8 * (track.NumberLength - 1 - i))))); // big-endian
        }
        if (patches.Count == 0 || head.TracksCrc < 0) return patches;

        // CRC-32 element first in Tracks: it covers everything after it up to the end of Tracks.
        var covered = new byte[head.TracksEnd - (head.TracksCrc + 4)];
        file.Position = head.TracksCrc + 4;
        file.ReadExactly(covered);
        foreach (var patch in patches) covered[patch.Offset - (head.TracksCrc + 4)] = patch.Value;
        var crc = Crc32(covered);
        for (var i = 0; i < 4; i++) patches.Add(new FilePatch(head.TracksCrc + i, (byte)(crc >> (8 * i)))); // little-endian
        return patches;
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32 (IEEE), as Matroska's CRC-32 element holds it.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    /// <summary>The lines of text subtitle track <paramref name="trackNumber"/>, in time order and tidied for reading.</summary>
    public static List<SubtitleLine> ReadSubtitles(string path, int trackNumber, CancellationToken cancel = default)
    {
        using var file = Open(path);
        var head = ReadHead(file);
        var track = head.Tracks.FirstOrDefault(t => t.Track.Number == trackNumber)
                    ?? throw new InvalidDataException($"Không có track phụ đề số {trackNumber}.");
        if (!track.Track.IsText) throw new InvalidDataException("Phụ đề này là hình ảnh, Voca chỉ đọc được phụ đề dạng chữ.");
        var ass = track.Track.Codec.Contains("ASS") || track.Track.Codec.Contains("SSA");
        var raw = new List<(long Time, long Duration, string Text)>();

        file.Position = head.FirstCluster;
        while (file.Position < head.SegmentEnd)
        {
            cancel.ThrowIfCancellationRequested();
            var (id, size, ok) = TryElement(file, head.SegmentEnd);
            if (!ok) break;
            if (id != Cluster) { if (size < 0) break; file.Position += size; continue; }
            var end = size < 0 ? head.SegmentEnd : file.Position + size;
            long clusterTime = 0;
            while (file.Position < end)
            {
                var at = file.Position;
                (var child, var childSize, ok) = TryElement(file, end);
                if (!ok) break;
                if (size < 0 && TopLevel.Contains(child)) { file.Position = at; break; }
                if (childSize < 0) break;
                var childEnd = file.Position + childSize;
                switch (child)
                {
                    case Timestamp:
                        clusterTime = (long)ReadUInt(file, childSize);
                        break;
                    case SimpleBlock:
                        if (ReadBlock(file, childSize, trackNumber) is { } simple)
                            raw.Add((clusterTime + simple.Rel, -1, Decode(simple.Data, track)));
                        break;
                    case BlockGroup:
                        (short Rel, byte[] Data)? block = null;
                        long duration = -1;
                        while (file.Position < childEnd)
                        {
                            var (gid, gsize, gok) = TryElement(file, childEnd);
                            if (!gok || gsize < 0) break;
                            var gend = file.Position + gsize;
                            if (gid == Block) block = ReadBlock(file, gsize, trackNumber);
                            else if (gid == BlockDuration) duration = (long)ReadUInt(file, gsize);
                            file.Position = gend;
                        }
                        if (block is { } b) raw.Add((clusterTime + b.Rel, duration, Decode(b.Data, track)));
                        break;
                }
                file.Position = childEnd;
            }
            if (size >= 0) file.Position = end;
        }

        var scale = head.TimestampScale;
        TimeSpan Time(long ticks) => TimeSpan.FromTicks(ticks * scale / 100);
        raw.Sort((a, b) => a.Time.CompareTo(b.Time));
        var events = raw.Select((r, i) =>
        {
            var start = Time(r.Time);
            // A block without a duration lasts until the next one (capped, so a last line does not stay forever).
            var end = r.Duration >= 0 ? Time(r.Time + r.Duration)
                : i + 1 < raw.Count ? Time(raw[i + 1].Time) : start + TimeSpan.FromSeconds(4);
            var (style, text) = ass ? AssText(r.Text) : ("", PlainText(r.Text));
            return (Style: style, Line: new SubtitleLine(start, end > start ? end : start + TimeSpan.FromSeconds(2), text));
        }).Where(e => e.Line.Text.Any(char.IsLetter)).ToList();
        // Songs (romaji with their translation), signs and titles are not dialogue; a track with nothing else keeps them.
        var dialogue = events.Where(e => !NotDialogue().IsMatch(e.Style)).ToList();
        return Merge((dialogue.Count > 0 ? dialogue : events).Select(e => e.Line).ToList());
    }

    /// <summary>
    /// Lines that start and end together (ASS splits one moment into several events: two speakers, a sign) become
    /// one line, so the player — which shows one line at a time — loses none of them.
    /// </summary>
    private static List<SubtitleLine> Merge(List<SubtitleLine> lines)
    {
        var merged = new List<SubtitleLine>();
        foreach (var line in lines)
        {
            if (merged.Count > 0 && merged[^1] is var last && line.Start < last.End && line.Start - last.Start < TimeSpan.FromMilliseconds(300))
            {
                if (!last.Text.Contains(line.Text)) merged[^1] = last with { End = line.End > last.End ? line.End : last.End, Text = last.Text + "\n" + line.Text };
                continue;
            }
            // An overlapping line cuts the one before short: the player shows the newest line.
            if (merged.Count > 0 && line.Start < merged[^1].End) merged[^1] = merged[^1] with { End = line.Start };
            merged.Add(line);
        }
        return merged.Where(l => l.End > l.Start).ToList();
    }

    /// <summary>
    /// The style and text of a Matroska ASS event ("ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,Effect,Text"),
    /// the text without styling.
    /// </summary>
    internal static (string Style, string Text) AssText(string ev)
    {
        var fields = ev.Split(',', 9);
        if (fields.Length < 9) return ("", "");
        var text = fields[8];
        var sb = new StringBuilder();
        var drawing = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                var close = text.IndexOf('}', i);
                if (close < 0) break;
                // "\p1" … "\p0" is a vector drawing, not words.
                foreach (Match m in DrawingTag().Matches(text[i..close])) drawing = m.Groups[1].Value != "0";
                i = close;
                continue;
            }
            if (drawing) continue;
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is 'N' or 'n') { sb.Append('\n'); i++; continue; }
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == 'h') { sb.Append(' '); i++; continue; }
            sb.Append(text[i]);
        }
        return (fields[2].Trim(), Tidy(sb.ToString()));
    }

    /// <summary>SRT/WebVTT text: HTML-like tags (&lt;i&gt;, &lt;font&gt;) and {\an8} hints dropped.</summary>
    private static string PlainText(string text) => Tidy(HtmlTag().Replace(AssHint().Replace(text, ""), ""));

    private static string Tidy(string text) =>
        string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0));

    // ---------------------------------- file structure ----------------------------------

    private sealed record TrackInfo(MkvTrack Track, int CompAlgo, byte[] CompSettings);

    /// <summary><see cref="TracksCrc"/>: where the 4 bytes of the track list's CRC-32 sit, -1 when it has none.</summary>
    private sealed record Head(List<TrackInfo> Tracks, long TimestampScale, long FirstCluster, long SegmentEnd, long TracksCrc, long TracksEnd);

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    /// <summary>Reads up to the first cluster: the timestamp scale and the tracks.</summary>
    private static Head ReadHead(Stream file)
    {
        var (id, size) = Element(file);
        if (id != EbmlHeader) throw new InvalidDataException("Không phải file Matroska.");
        file.Position += size;
        (id, size) = Element(file);
        if (id != Segment) throw new InvalidDataException("Không phải file Matroska.");
        var segmentEnd = size < 0 ? file.Length : Math.Min(file.Length, file.Position + size);
        var tracks = new List<TrackInfo>();
        long scale = 1_000_000, crc = -1, tracksEnd = -1;
        while (file.Position < segmentEnd)
        {
            var at = file.Position;
            var (child, childSize, ok) = TryElement(file, segmentEnd);
            if (!ok) break;
            if (child == Cluster) return new Head(tracks, scale, at, segmentEnd, crc, tracksEnd);
            if (childSize < 0) break;
            var end = file.Position + childSize;
            if (child == Info)
                while (file.Position < end)
                {
                    var (iid, isize) = Element(file);
                    if (iid == TimestampScale) scale = (long)ReadUInt(file, isize);
                    else file.Position += isize;
                }
            else if (child == Tracks)
            {
                tracksEnd = end;
                while (file.Position < end)
                {
                    var (tid, tsize) = Element(file);
                    var tend = file.Position + tsize;
                    if (tid == TrackEntry) tracks.Add(ReadTrack(file, tend));
                    else if (tid == Crc32Element && tsize == 4 && tracks.Count == 0) crc = file.Position;
                    file.Position = tend;
                }
            }
            file.Position = end;
        }
        return new Head(tracks, scale, segmentEnd, segmentEnd, crc, tracksEnd);
    }

    private static TrackInfo ReadTrack(Stream file, long end)
    {
        int number = 0, numberLength = 0, type = 0, algo = -1;
        long numberOffset = -1;
        string codec = "", language = "eng", bcp47 = "", name = "";
        bool isDefault = true, forced = false;
        var settings = Array.Empty<byte>();
        while (file.Position < end)
        {
            var (id, size) = Element(file);
            var next = file.Position + size;
            switch (id)
            {
                case TrackNumber: numberOffset = file.Position; numberLength = (int)size; number = (int)ReadUInt(file, size); break;
                case TrackType: type = (int)ReadUInt(file, size); break;
                case CodecId: codec = ReadString(file, size); break;
                case Language: language = ReadString(file, size); break;
                case LanguageBcp47: bcp47 = ReadString(file, size); break;
                case Name: name = ReadString(file, size); break;
                case FlagDefault: isDefault = ReadUInt(file, size) != 0; break;
                case FlagForced: forced = ReadUInt(file, size) != 0; break;
                case ContentEncodings: (algo, settings) = ReadCompression(file, next); break;
            }
            file.Position = next;
        }
        var kind = type switch { 1 => MkvTrackKind.Video, 2 => MkvTrackKind.Audio, 0x11 => MkvTrackKind.Subtitle, _ => MkvTrackKind.Other };
        return new TrackInfo(new MkvTrack(number, kind, codec, bcp47.Length > 0 ? bcp47 : language, name, isDefault, forced,
            numberLength is > 0 and <= 4 ? numberOffset : -1, numberLength), algo, settings);
    }

    /// <summary>
    /// The compression of a track's blocks (ContentEncodings › ContentEncoding › ContentCompression): -1 none,
    /// 0 zlib (also when ContentCompAlgo is left out), 3 header stripping with the bytes to put back.
    /// </summary>
    private static (int Algo, byte[] Settings) ReadCompression(Stream file, long end)
    {
        var algo = -1;
        var settings = Array.Empty<byte>();
        while (file.Position < end)
        {
            var (id, size) = Element(file);
            var next = file.Position + size;
            switch (id)
            {
                case ContentEncoding:
                    var found = ReadCompression(file, next);
                    if (found.Algo >= 0) (algo, settings) = found;
                    break;
                case ContentCompression:
                    var inner = ReadCompression(file, next);
                    (algo, settings) = (Math.Max(inner.Algo, 0), inner.Settings);
                    break;
                case ContentCompAlgo:
                    algo = (int)ReadUInt(file, size);
                    break;
                case ContentCompSettings:
                    settings = new byte[size];
                    file.ReadExactly(settings);
                    break;
            }
            file.Position = next;
        }
        return (algo, settings);
    }

    /// <summary>A block of track <paramref name="track"/>: its time relative to the cluster and its data; null for other tracks.</summary>
    private static (short Rel, byte[] Data)? ReadBlock(Stream file, long size, int track)
    {
        var start = file.Position;
        var number = ReadVint(file, out _);
        if (number != track) return null;
        Span<byte> head = stackalloc byte[3];
        file.ReadExactly(head);
        var rel = (short)(head[0] << 8 | head[1]);
        if ((head[2] & 0x06) != 0) return null; // laced blocks do not occur for subtitles
        var data = new byte[size - (file.Position - start)];
        file.ReadExactly(data);
        return (rel, data);
    }

    private static string Decode(byte[] data, TrackInfo track)
    {
        if (track.CompAlgo == 0)
        {
            using var zlib = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            data = output.ToArray();
        }
        else if (track.CompAlgo == 3) data = [.. track.CompSettings, .. data];
        return Encoding.UTF8.GetString(data).TrimEnd('\0');
    }

    private static (uint Id, long Size) Element(Stream file)
    {
        var id = ReadId(file);
        var size = ReadVint(file, out var unknown);
        return (id, unknown ? -1 : size);
    }

    /// <summary>The next element header, unless the file ends (or is cut short) before <paramref name="limit"/>.</summary>
    private static (uint Id, long Size, bool Ok) TryElement(Stream file, long limit)
    {
        if (file.Position >= limit || limit - file.Position < 2) return (0, 0, false);
        try
        {
            var (id, size) = Element(file);
            return (id, size, size < 0 || file.Position + size <= file.Length);
        }
        catch (EndOfStreamException) { return (0, 0, false); }
    }

    private static uint ReadId(Stream file)
    {
        var first = ReadByte(file);
        var length = LeadingZeros(first) + 1;
        if (length > 4) throw new InvalidDataException("Hỏng cấu trúc Matroska.");
        uint id = first;
        for (var i = 1; i < length; i++) id = id << 8 | ReadByte(file);
        return id;
    }

    private static long ReadVint(Stream file, out bool unknown)
    {
        var first = ReadByte(file);
        var length = LeadingZeros(first) + 1;
        if (length > 8) throw new InvalidDataException("Hỏng cấu trúc Matroska.");
        long value = first & (0xFF >> length);
        var allOnes = value == (0xFF >> length);
        for (var i = 1; i < length; i++)
        {
            var b = ReadByte(file);
            allOnes &= b == 0xFF;
            value = value << 8 | b;
        }
        unknown = allOnes;
        return value;
    }

    private static int LeadingZeros(byte b)
    {
        var n = 0;
        for (var mask = 0x80; mask != 0 && (b & mask) == 0; mask >>= 1) n++;
        return n;
    }

    private static byte ReadByte(Stream file)
    {
        var b = file.ReadByte();
        return b < 0 ? throw new EndOfStreamException() : (byte)b;
    }

    private static ulong ReadUInt(Stream file, long size)
    {
        ulong value = 0;
        for (var i = 0; i < size; i++) value = value << 8 | ReadByte(file);
        return value;
    }

    private static string ReadString(Stream file, long size)
    {
        var bytes = new byte[size];
        file.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    [GeneratedRegex(@"sign|song|karaoke|\bop\b|\bed\b", RegexOptions.IgnoreCase)]
    private static partial Regex SignsOnly();

    /// <summary>ASS styles of songs (OP/ED, romaji, karaoke), signs and titles.</summary>
    [GeneratedRegex(@"sign|song|karaoke|kara\b|romaji|kanji|opening|ending|\bop\b|\bed\b|title|insert", RegexOptions.IgnoreCase)]
    private static partial Regex NotDialogue();

    [GeneratedRegex(@"\\p(\d+)")]
    private static partial Regex DrawingTag();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex AssHint();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
