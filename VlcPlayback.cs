using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Voca.Services;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Voca;

/// <summary>An audio track the learner can switch to.</summary>
public sealed record AudioChoice(int Id, string Label);

/// <summary>
/// Plays a video with libVLC (<see cref="VlcRuntime"/>) into a WPF bitmap, so Voca's own subtitle line can sit on top
/// of the picture as with Windows' player. Frames are written by libVLC into one of three buffers and copied to the
/// bitmap on the UI thread (three, so libVLC never writes the one being shown nor the next one waiting). libVLC's own
/// subtitles are off: Voca shows the line, with its words marked. libVLC's events arrive on its threads and are passed
/// to the UI thread; libVLC is never called from inside one of them.
/// </summary>
public sealed class VlcPlayback : IDisposable
{
    private const int MaxWidth = 1920;

    private readonly LibVLC _vlc;
    private readonly MediaPlayer _player;
    private readonly Dispatcher _ui;
    private readonly Action<WriteableBitmap> _show;
    private Media? _media;
    private bool _started, _ended, _opened, _disposed;
    /// <summary>Where to go once playing (a seek asked before the video was open or after it ended).</summary>
    private long? _pendingTime;

    // Kept in fields: libVLC calls them from native code, so they must not be collected.
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    private readonly object _gate = new();
    private readonly IntPtr[] _buffers = new IntPtr[3];
    private int _width, _height, _pitch;
    private int _ready = -1, _shown = -1;
    private bool _framePending;
    private WriteableBitmap? _bitmap;

    public event Action? Opened;
    public event Action? Ended;
    public event Action<string>? Failed;

    public VlcPlayback(string dataFolder, Dispatcher ui, Action<WriteableBitmap> show)
    {
        VlcRuntime.Initialize(dataFolder);
        _ui = ui;
        _show = show;
        // English sound first when a film has several; no subtitles, title or messages drawn on the picture.
        _vlc = new LibVLC("--audio-language=eng,en", "--no-spu", "--no-osd", "--no-video-title-show", "--no-sub-autodetect-file", "--quiet");
        _player = new MediaPlayer(_vlc);
        _format = Format;
        _cleanup = Cleanup;
        _lock = Lock;
        _display = Display;
        _player.SetVideoFormatCallbacks(_format, _cleanup);
        _player.SetVideoCallbacks(_lock, null, _display);
        _player.Playing += (_, _) => Post(OnPlaying);
        _player.EndReached += (_, _) => Post(() => { _ended = true; Ended?.Invoke(); });
        _player.EncounteredError += (_, _) => Post(() => Failed?.Invoke("VLC không phát được video này (file hỏng hoặc định dạng lạ)."));
    }

    public void Open(string path) => _media = new Media(_vlc, path, FromType.FromPath);

    public bool IsOpen => _opened;

    public TimeSpan Position
    {
        get => _pendingTime is { } pending ? TimeSpan.FromMilliseconds(pending)
            : _ended ? Length ?? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));
        set
        {
            var ms = (long)Math.Max(0, value.TotalMilliseconds);
            if (!_opened || _ended) _pendingTime = ms;
            else _player.Time = ms;
        }
    }

    public TimeSpan? Length => _player.Length > 0 ? TimeSpan.FromMilliseconds(_player.Length) : null;

    public double Speed { set => _player.SetRate((float)value); }

    public void Play()
    {
        if (_media is null) return;
        if (!_started || _ended)
        {
            if (_ended) _player.Stop();
            _started = true;
            _ended = false;
            _player.Play(_media);
        }
        else _player.SetPause(false);
    }

    public void Pause()
    {
        if (_started && !_ended) _player.SetPause(true);
    }

    /// <summary>The audio tracks, labelled from the file itself when it is an .mkv (by language), else as libVLC names them.</summary>
    public IReadOnlyList<AudioChoice> AudioTracks(IReadOnlyList<MkvTrack> fileTracks)
    {
        var tracks = (_player.AudioTrackDescription ?? []).Where(t => t.Id >= 0).ToList();
        var labels = MkvTrack.ShortLabels(fileTracks.Where(t => t.Kind == MkvTrackKind.Audio).ToList());
        return tracks.Select((t, i) => new AudioChoice(t.Id, labels.Count == tracks.Count ? labels[i] : t.Name ?? $"Track {i + 1}")).ToList();
    }

    public int AudioTrack
    {
        get => _player.AudioTrack;
        set => _player.SetAudioTrack(value);
    }

    private void OnPlaying()
    {
        if (_pendingTime is { } ms)
        {
            _pendingTime = null;
            _player.Time = ms;
        }
        if (_opened) return;
        _opened = true;
        Opened?.Invoke();
    }

    private void Post(Action action) => _ui.BeginInvoke(() => { if (!_disposed) action(); });

    // ---------------------------------- frames (libVLC's threads) ----------------------------------

    private uint Format(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var w = (int)width;
        var h = (int)height;
        if (w > MaxWidth) { h = (int)((long)h * MaxWidth / w) & ~1; w = MaxWidth; }
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4); // BGRA, as Bgr32 reads it
        width = (uint)w;
        height = (uint)h;
        pitches = (uint)(w * 4);
        lines = (uint)h;
        lock (_gate)
        {
            FreeBuffers();
            for (var i = 0; i < _buffers.Length; i++) _buffers[i] = Marshal.AllocHGlobal(w * 4 * h);
            (_width, _height, _pitch) = (w, h, w * 4);
            (_ready, _shown) = (-1, -1);
        }
        Post(() => NewBitmap(w, h));
        return 1;
    }

    /// <summary>
    /// The bitmap frames are copied into (UI thread). libVLC may hand over a picture taller than the video — rows of
    /// padding at the bottom (720 → 736) — so the bitmap keeps only the rows of the picture itself.
    /// </summary>
    private void NewBitmap(int width, int height)
    {
        uint w = 0, h = 0;
        var visible = _player.Size(0, ref w, ref h) && w > 0 && h > 0
            ? Math.Clamp((int)Math.Round((double)h * width / w), 1, height)
            : height;
        lock (_gate)
            if (width != _width || height != _height) return; // the format changed again meanwhile
        _bitmap = new WriteableBitmap(width, visible, 96, 96, PixelFormats.Bgr32, null);
        _show(_bitmap);
    }

    private void Cleanup(ref IntPtr opaque) { }

    /// <summary>libVLC asks where to write the next frame: a buffer neither on screen nor waiting to be.</summary>
    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        int index;
        lock (_gate)
        {
            index = Enumerable.Range(0, _buffers.Length).First(i => i != _ready && i != _shown);
            Marshal.WriteIntPtr(planes, _buffers[index]);
        }
        return index;
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        lock (_gate)
        {
            _ready = (int)picture;
            if (_framePending) return;
            _framePending = true;
        }
        _ui.BeginInvoke(Render, DispatcherPriority.Render);
    }

    /// <summary>Copies the newest frame into the bitmap (UI thread).</summary>
    private void Render()
    {
        lock (_gate)
        {
            _framePending = false;
            if (_disposed || _ready < 0 || _bitmap is null || _bitmap.PixelWidth != _width || _bitmap.PixelHeight > _height) return;
            _shown = _ready;
            _ready = -1;
            var rows = _bitmap.PixelHeight;
            _bitmap.WritePixels(new Int32Rect(0, 0, _width, rows), _buffers[_shown], _pitch * rows, _pitch);
        }
    }

    private void FreeBuffers()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            if (_buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_buffers[i]);
            _buffers[i] = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.Stop();
        _player.Dispose();
        _media?.Dispose();
        _vlc.Dispose();
        lock (_gate) FreeBuffers();
    }
}
