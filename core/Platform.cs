using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCodeIndicator;

// The OS-specific services the core needs from a front end. Windows implements them
// with MCI and ConPTY (windows/); a macOS front end would use afplay and a Unix pty.

public enum SoundCue { Waiting, Done }

/// <summary>Plays the state-change chimes. Called on the UI thread; failures must be silent.</summary>
public interface ISoundPlayer
{
    void Play(SoundCue cue);
}

/// <summary>What to run inside a pseudo terminal.</summary>
public sealed record PseudoTerminalStartInfo(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Columns,
    int Rows);

/// <summary>
/// One process attached to a pseudo terminal, so that an interactive TUI (claude) draws
/// as if a person were watching. Used once: create, <see cref="Start"/>, then dispose.
/// </summary>
public interface IPseudoTerminal : IDisposable
{
    /// <summary>Starts the process. Throws if the terminal or the process can't be created.</summary>
    void Start(PseudoTerminalStartInfo info);

    /// <summary>Keystrokes to the process. Valid after <see cref="Start"/>.</summary>
    Stream Input { get; }

    /// <summary>Everything the process draws, escape sequences included. Reads end (0) when it exits.</summary>
    Stream Output { get; }

    bool IsRunning { get; }
}
