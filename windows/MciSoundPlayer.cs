using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCodeIndicator;

/// <summary>
/// Plays the chimes from <c>.sounds\</c> (next to the exe): pop.mp3 when a prompt needs
/// attention, done.mp3 when Claude finishes. MCI plays MP3 with no extra dependencies
/// (System.Media.SoundPlayer is WAV-only, and pulling in WPF's MediaPlayer just for a
/// chime isn't worth it). Always called on the UI thread — MCI wants a message pump.
/// </summary>
internal sealed class MciSoundPlayer : ISoundPlayer, IDisposable
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? returnValue,
                                            int returnLength, IntPtr callback);

    // One clip per state transition we chime on; each is opened once, then replayed.
    private readonly SoundClip _waiting = new("pop.mp3",  "ccIndicatorWaiting");
    private readonly SoundClip _done    = new("done.mp3", "ccIndicatorDone");

    public void Play(SoundCue cue)
    {
        var clip = cue == SoundCue.Waiting ? _waiting : _done;
        if (clip.Unavailable) return;

        if (!clip.Opened)
        {
            string path = Path.Combine(AppContext.BaseDirectory, ".sounds", clip.File);
            if (!File.Exists(path)) { clip.Unavailable = true; return; }

            // "type mpegvideo" is the MCI driver that handles MP3; a couple of Windows
            // installs prefer letting MCI pick by extension, so fall back to that.
            if (mciSendString($"open \"{path}\" type mpegvideo alias {clip.Alias}", null, 0, IntPtr.Zero) != 0 &&
                mciSendString($"open \"{path}\" alias {clip.Alias}", null, 0, IntPtr.Zero) != 0)
            {
                clip.Unavailable = true;
                return;
            }
            clip.Opened = true;
        }

        // "from 0" rewinds, so a second trigger re-plays the clip instead of no-oping
        // at the end of the previous playback.
        mciSendString($"play {clip.Alias} from 0", null, 0, IntPtr.Zero);
    }

    private static void Close(SoundClip clip)
    {
        if (!clip.Opened) return;
        try { mciSendString($"close {clip.Alias}", null, 0, IntPtr.Zero); } catch { }
        clip.Opened = false;
    }

    public void Dispose()
    {
        Close(_waiting);
        Close(_done);
    }

    private sealed class SoundClip(string file, string alias)
    {
        public string File  { get; } = file;
        public string Alias { get; } = alias;
        public bool Opened;      // MCI device opened once, then replayed with "play from 0"
        public bool Unavailable; // file missing or MCI refused it — stop retrying
    }
}
