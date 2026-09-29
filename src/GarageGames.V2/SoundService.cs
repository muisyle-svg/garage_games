using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace GarageGames.V2;

// Game sounds, played by the app itself through this computer's default audio output (the
// TV when it is connected over HDMI). Browsers pause, throttle, or mute background tabs and
// block audio until someone clicks, so no game sound depends on a browser page.
public enum SoundCue
{
    // The "3, 2, 1, Go" voice at the start of a run.
    Countdown,
    // A keypad message has appeared on the TV.
    KeypadMessage,
    // The bonus speed round's first button lights.
    BonusStart
}

public interface ISoundPlayer
{
    // Starts a cue without waiting for it. Never throws: sound problems must not affect the game.
    void Play(SoundCue cue);
}

public sealed class SilentSoundPlayer : ISoundPlayer
{
    public static SilentSoundPlayer Instance { get; } = new();
    public void Play(SoundCue cue) { }
}

// Plays sound files with Windows MCI (winmm), which handles WAV and MP3, can overlap sounds,
// and needs no extra packages. All MCI calls run on one dedicated thread, in order.
public sealed class WindowsSoundPlayer : ISoundPlayer, IDisposable
{
    // Each cue's file, relative to the sounds folder. Replace a file to change a sound; add a
    // SoundCue and a line here for a new one.
    public static readonly IReadOnlyDictionary<SoundCue, string> CueFiles = new Dictionary<SoundCue, string>
    {
        [SoundCue.Countdown] = "3-seconds-countdown-deep-voice-game.mp3",
        [SoundCue.KeypadMessage] = "keypad-message.wav",
        [SoundCue.BonusStart] = "bonus-start.wav"
    };

    private readonly string _soundsDirectory;
    private readonly ILogger<WindowsSoundPlayer> _logger;
    private readonly BlockingCollection<SoundCue> _queue = new();
    private readonly List<(string Alias, DateTime CloseAtUtc)> _open = [];
    private readonly Thread _thread;
    private long _aliasCounter;

    public WindowsSoundPlayer(string soundsDirectory, ILogger<WindowsSoundPlayer> logger)
    {
        _soundsDirectory = soundsDirectory;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Garage Games sounds" };
        // MCI's MP3/WAV device is built on COM (DirectShow), which fails to load on the default
        // multithreaded apartment. Blocking waits on an STA thread still pump COM messages.
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }
        _thread.Start();
    }

    public void Play(SoundCue cue)
    {
        try
        {
            _queue.Add(cue);
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Run()
    {
        try
        {
            while (!_queue.IsCompleted)
            {
                // Wake at least every 250 ms to close sounds that have finished.
                if (_queue.TryTake(out var cue, 250))
                {
                    Start(cue);
                }
                CloseFinished(DateTime.UtcNow);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }
        finally
        {
            CloseFinished(DateTime.MaxValue);
        }
    }

    private void Start(SoundCue cue)
    {
        if (!CueFiles.TryGetValue(cue, out var fileName))
        {
            return;
        }

        var path = Path.Combine(_soundsDirectory, fileName);
        if (!File.Exists(path))
        {
            _logger.LogWarning("Sound file for {Cue} was not found at {Path}.", cue, path);
            return;
        }

        var alias = $"gg{Interlocked.Increment(ref _aliasCounter)}";
        // "mpegvideo" is the DirectShow MCI device; it plays both MP3 and WAV.
        if (!Send($"open \"{path}\" type mpegvideo alias {alias}", out _, cue) ||
            !Send($"set {alias} time format milliseconds", out _, cue))
        {
            Send($"close {alias}", out _, cue);
            return;
        }

        // MCI can over-report a variable-bitrate MP3's length; that only delays closing it.
        var lengthMs = Send($"status {alias} length", out var lengthText, cue) &&
            long.TryParse(lengthText, out var parsed) ? parsed : 10_000;
        if (!Send($"play {alias}", out _, cue))
        {
            Send($"close {alias}", out _, cue);
            return;
        }

        _open.Add((alias, DateTime.UtcNow.AddMilliseconds(lengthMs + 1_000)));
    }

    private void CloseFinished(DateTime nowUtc)
    {
        for (var index = _open.Count - 1; index >= 0; index--)
        {
            if (_open[index].CloseAtUtc <= nowUtc)
            {
                Send($"close {_open[index].Alias}", out _, null);
                _open.RemoveAt(index);
            }
        }
    }

    private bool Send(string command, out string result, SoundCue? cue)
    {
        var buffer = new StringBuilder(128);
        var error = mciSendStringW(command, buffer, buffer.Capacity, IntPtr.Zero);
        result = buffer.ToString();
        if (error == 0)
        {
            return true;
        }

        var message = new StringBuilder(256);
        mciGetErrorStringW(error, message, message.Capacity);
        _logger.LogWarning("Sound {Cue} could not play ({Command}): {Error}", cue?.ToString() ?? "cleanup",
            command.Split(' ')[0], message.ToString());
        return false;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendStringW(string command, StringBuilder? returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool mciGetErrorStringW(int error, StringBuilder text, int length);
}
