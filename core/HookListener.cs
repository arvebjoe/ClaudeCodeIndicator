using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace ClaudeCodeIndicator;

/// <summary>
/// The local HTTP endpoint the hooks POST to. Routing is by URL path only; the body
/// (which carries session_id) is drained and discarded. Events are raised on a
/// background thread: subscribers must marshal to their UI thread themselves.
/// </summary>
public sealed class HookListener : IDisposable
{
    /// <summary>
    /// Hardcoded into the hook URLs written to settings.json (<see cref="ClaudeHooks"/>),
    /// so changing it must stay consistent there.
    /// </summary>
    public const int Port = 8787;

    private readonly HttpListener _listener = new();

    /// <summary>/working, /waiting, /done — Claude Code hooks.</summary>
    public event Action<IndicatorState>? ClaudeState;

    /// <summary>/codex/working, /codex/waiting, /codex/done — optional, hand-made Codex hooks.</summary>
    public event Action<IndicatorState>? CodexState;

    /// <summary>Starts listening. Throws <see cref="HttpListenerException"/> if the port can't be bound.</summary>
    public void Start()
    {
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; } // listener stopped / disposed
            _ = HandleAsync(ctx);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = (ctx.Request.Url?.AbsolutePath ?? string.Empty)
                .Trim('/').ToLowerInvariant();

            // Claude Code POSTs the event JSON as the body. We route by path, so we
            // just drain the body. (session_id lives in here if you ever want to
            // aggregate multiple concurrent sessions instead of latest-wins.)
            using (var reader = new StreamReader(ctx.Request.InputStream))
                await reader.ReadToEndAsync();

            ctx.Response.StatusCode = 200; // 2xx empty body = success to the hook
            ctx.Response.Close();

            if (Parse(path) is { } s) ClaudeState?.Invoke(s);
            if (path.StartsWith("codex/", StringComparison.Ordinal) && Parse(path["codex/".Length..]) is { } c)
                CodexState?.Invoke(c);
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { /* ignore */ }
        }
    }

    private static IndicatorState? Parse(string path) => path switch
    {
        "working" => IndicatorState.Working,
        "waiting" => IndicatorState.Waiting,
        "done"    => IndicatorState.Done,
        _ => null
    };

    public void Dispose()
    {
        try { if (_listener.IsListening) _listener.Stop(); _listener.Close(); } catch { }
    }
}
