using System.IO;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace Voca.Services;

/// <summary>A Whisper model the learner can pick (English-only models: smaller and better for English).</summary>
public sealed record SpeechModel(GgmlType Type, string Label, string FileName, int SizeMb);

/// <summary>
/// Turns the speech of a video into subtitle lines, offline: the audio track is read with Windows Media
/// Foundation (mp4, mkv, mov, mp3, … whatever Windows can play), resampled to 16 kHz mono and recognised by
/// Whisper. Models are downloaded once into the data folder; Whisper's native libraries travel inside
/// Voca.exe and are unpacked next to them on first use.
/// </summary>
public static class Transcriber
{
    private const string NativeVersion = "1.9.1";
    private static readonly string[] NativeFiles = ["ggml-base-whisper.dll", "ggml-cpu-whisper.dll", "ggml-whisper.dll", "whisper.dll"];
    private static readonly object NativeLock = new();
    private static bool _nativeReady;

    public static readonly IReadOnlyList<SpeechModel> Models =
    [
        new(GgmlType.BaseEn, "Nhanh · base.en (~142 MB)", "ggml-base.en.bin", 142),
        new(GgmlType.SmallEn, "Chính xác hơn · small.en (~466 MB, chậm hơn ~3 lần)", "ggml-small.en.bin", 466)
    ];

    public static string ModelPath(string dataFolder, SpeechModel model) => Path.Combine(dataFolder, "models", model.FileName);

    public static bool IsDownloaded(string dataFolder, SpeechModel model) => File.Exists(ModelPath(dataFolder, model));

    /// <summary>Downloads the model (from Hugging Face, via Whisper.net) to a .part file first, so a cancelled download leaves nothing half-done.</summary>
    public static async Task DownloadAsync(string dataFolder, SpeechModel model, IProgress<double> progress, CancellationToken cancel)
    {
        var target = ModelPath(dataFolder, model);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var part = target + ".part";
        try
        {
            await using (var source = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(model.Type, QuantizationType.NoQuantization, cancel))
            await using (var file = File.Create(part))
            {
                var expected = model.SizeMb * 1024.0 * 1024.0;
                var buffer = new byte[1 << 16];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancel);
                    total += read;
                    progress.Report(Math.Min(0.99, total / expected));
                }
            }
            File.Move(part, target, overwrite: true);
            progress.Report(1);
        }
        finally
        {
            if (File.Exists(part)) File.Delete(part);
        }
    }

    /// <summary>
    /// Recognises the speech of <paramref name="mediaPath"/>. <paramref name="stage"/> reports what is happening,
    /// <paramref name="percent"/> the recognition progress and <paramref name="line"/> each line as it is found.
    /// </summary>
    public static async Task<List<SubtitleLine>> TranscribeAsync(string dataFolder, SpeechModel model, string mediaPath,
        IProgress<string> stage, IProgress<int> percent, IProgress<SubtitleLine> line, CancellationToken cancel)
    {
        PrepareNative(dataFolder);
        var wav = Path.Combine(Path.GetTempPath(), $"voca-{Guid.NewGuid():N}.wav");
        try
        {
            stage.Report("Đang tách âm thanh từ video…");
            await Task.Run(() => ExtractAudio(mediaPath, wav, cancel), cancel);
            stage.Report("Đang nhận dạng giọng nói…");
            return await Task.Run(async () =>
            {
                using var factory = WhisperFactory.FromPath(ModelPath(dataFolder, model));
                await using var processor = factory.CreateBuilder()
                    .WithLanguage("en")
                    .WithThreads(Math.Max(1, Environment.ProcessorCount - 1))
                    .WithProgressHandler(p => percent.Report(p))
                    .Build();
                await using var audio = File.OpenRead(wav);
                var lines = new List<SubtitleLine>();
                await foreach (var segment in processor.ProcessAsync(audio, cancel))
                {
                    var found = new SubtitleLine(segment.Start, segment.End, segment.Text.Trim());
                    lines.Add(found);
                    line.Report(found);
                }
                return Subtitles.Clean(lines);
            }, cancel);
        }
        finally
        {
            try { File.Delete(wav); } catch (IOException) { }
        }
    }

    /// <summary>Writes the audio track as the 16 kHz mono 16-bit WAV Whisper needs.</summary>
    private static void ExtractAudio(string mediaPath, string wavPath, CancellationToken cancel)
    {
        using var reader = new MediaFoundationReader(mediaPath);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        using var writer = new WaveFileWriter(wavPath, resampler.WaveFormat);
        var buffer = new byte[resampler.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            writer.Write(buffer, 0, read);
        }
        if (writer.Length == 0) throw new InvalidDataException("Video không có âm thanh.");
    }

    /// <summary>Unpacks Whisper's native libraries (embedded in Voca.exe) once per version and points Whisper.net at them.</summary>
    private static void PrepareNative(string dataFolder)
    {
        lock (NativeLock)
        {
            if (_nativeReady) return;
            // Whisper.net looks for the CPU build in runtimes\win-x64 under the folder of LibraryPath.
            var root = Path.Combine(dataFolder, "whisper", NativeVersion);
            var folder = Path.Combine(root, "runtimes", "win-x64");
            Directory.CreateDirectory(folder);
            var assembly = typeof(Transcriber).Assembly;
            foreach (var name in NativeFiles)
            {
                var target = Path.Combine(folder, name);
                using var resource = assembly.GetManifestResourceStream("Whisper." + name)
                    ?? throw new InvalidOperationException($"Thiếu thư viện {name} trong Voca.exe.");
                if (File.Exists(target) && new FileInfo(target).Length == resource.Length) continue;
                var part = target + ".part";
                using (var file = File.Create(part)) resource.CopyTo(file);
                File.Move(part, target, overwrite: true);
            }
            RuntimeOptions.LibraryPath = Path.Combine(root, "whisper.dll");
            _nativeReady = true;
        }
    }
}
