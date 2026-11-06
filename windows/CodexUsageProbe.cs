using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCodeIndicator;

/// <summary>One rolling Codex rate-limit window (e.g. the 5-hour or the weekly one).</summary>
internal sealed record CodexWindow(int UsedPercent, long? WindowMinutes, DateTime? ResetsAt)
{
    public string Label => WindowMinutes switch
    {
        300 => "5h",
        10080 => "wk",
        >= 1440 and var m when m % 1440 == 0 => $"{m / 1440}d",
        >= 60 and var m when m % 60 == 0 => $"{m / 60}h",
        { } m => $"{m}m",
        null => "limit",
    };
}

/// <summary>What <c>account/rateLimits/read</c> told us about the Codex account.</summary>
internal sealed record CodexUsageReading(
    CodexWindow? Primary,
    CodexWindow? Secondary,
    decimal? CreditsUsed,       // workspace spend control: effective monthly credit limit
    decimal? CreditsLimit,
    DateTime? CreditsResetAt,
    string? CreditBalance,      // prepaid/flexible credits balance, raw text from the backend
    bool CreditsUnlimited,
    DateTime ReadAt)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Compact one-liner for the tooltip/menu, e.g. "8,000 / 25,000 credits · 5h 42% · wk 84%".</summary>
    public string Short
    {
        get
        {
            var parts = new List<string>();
            if (CreditsUsed is { } u && CreditsLimit is { } l)
                parts.Add(string.Format(Inv, "{0:#,0} / {1:#,0} credits", u, l));
            else if (CreditsUnlimited)
                parts.Add("unlimited credits");
            else if (!string.IsNullOrWhiteSpace(CreditBalance))
                parts.Add($"{CreditBalance} credits left");
            foreach (var w in new[] { Primary, Secondary })
                if (w is not null) parts.Add($"{w.Label} {w.UsedPercent}%");
            return parts.Count == 0 ? "no limits reported" : string.Join(" · ", parts);
        }
    }

    /// <summary>When the monthly credit limit resets, for the menu row.</summary>
    public string? ResetsText => CreditsResetAt?.ToLocalTime().ToString("MMM d", Inv);
}

/// <summary>
/// Reads Codex usage by starting <c>codex app-server</c> (JSON-RPC over stdio, one JSON
/// object per line) and calling <c>account/rateLimits/read</c>. Unlike Claude's
/// <c>/usage</c> this is a structured API, so there's no screen scraping: the process is
/// started per read, does the initialize handshake, asks once and is killed. Codex handles
/// auth from <c>~/.codex/auth.json</c>; it only works when signed in with ChatGPT (not an
/// API key). Nothing is sent to a model.
///
/// The app-server protocol is marked unstable upstream, so every field is optional here.
/// </summary>
internal sealed class CodexUsageProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private readonly string _workDir;

    public CodexUsageProbe(string workDir)
    {
        _workDir = workDir;
        Directory.CreateDirectory(_workDir);
    }

    /// <summary>True if a codex executable could be found (PATH or the npm global folder).</summary>
    public static bool IsInstalled => FindCodex() is not null;

    /// <summary>Throws <see cref="CodexUsageException"/> with a user-presentable message on failure.</summary>
    public async Task<CodexUsageReading> ReadAsync(CancellationToken ct = default)
    {
        var (file, args) = FindCodex() ?? throw new CodexUsageException("codex not found");

        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = _workDir,      // empty folder: no project config/AGENTS.md
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // No BOM: the first line we write must be a bare JSON object.
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            if (!proc.Start()) throw new CodexUsageException("couldn't start codex");
            proc.BeginErrorReadLine();
            var stdin = proc.StandardInput;
            stdin.AutoFlush = true;

            await SendAsync(stdin, new JsonObject
            {
                ["id"] = 1,
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "claude_code_indicator",
                        ["title"] = "Claude Code Indicator",
                        ["version"] = "1.0.0",
                    },
                },
            });
            await WaitForResponseAsync(proc, 1, cts.Token);
            await SendAsync(stdin, new JsonObject { ["method"] = "initialized" });

            await SendAsync(stdin, new JsonObject { ["id"] = 2, ["method"] = "account/rateLimits/read" });
            var result = await WaitForResponseAsync(proc, 2, cts.Token);
            return Parse(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CodexUsageException("codex app-server timed out");
        }
        catch (Exception ex) when (ex is not CodexUsageException and not OperationCanceledException)
        {
            string err;
            lock (stderr) err = stderr.ToString().Trim();
            throw new CodexUsageException(err.Length > 0 ? FirstLine(err) : ex.Message);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static Task SendAsync(StreamWriter stdin, JsonObject msg) =>
        stdin.WriteLineAsync(msg.ToJsonString());

    /// <summary>Reads lines until the response with <paramref name="id"/>; skips notifications.</summary>
    private static async Task<JsonNode?> WaitForResponseAsync(Process proc, int id, CancellationToken ct)
    {
        while (true)
        {
            string? line = await proc.StandardOutput.ReadLineAsync(ct);
            if (line is null) throw new IOException("codex app-server exited");
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch (JsonException) { continue; } // stray log line

            if (msg is not JsonObject obj || !obj.TryGetPropertyValue("id", out var idNode)) continue;
            if (idNode is not JsonValue v || !v.TryGetValue(out int got) || got != id) continue;

            if (obj["error"] is JsonObject error)
                throw new CodexUsageException(Str(error["message"]) ?? "codex returned an error");
            return obj["result"];
        }
    }

    private static CodexUsageReading Parse(JsonNode? result)
    {
        // Prefer the multi-bucket view's "codex" bucket; fall back to the legacy single view.
        JsonNode? snap = result?["rateLimitsByLimitId"]?["codex"] ?? result?["rateLimits"];
        if (snap is null) throw new CodexUsageException("no rate limits in response");

        decimal? used = null, limit = null;
        DateTime? creditsReset = null;
        if (snap["individualLimit"] is JsonObject il)
        {
            used = Dec(il["used"]);
            limit = Dec(il["limit"]);
            creditsReset = Unix(il["resetsAt"]);
        }

        var credits = snap["credits"] as JsonObject;
        return new CodexUsageReading(
            Window(snap["primary"]),
            Window(snap["secondary"]),
            used, limit is > 0 ? limit : null, creditsReset,
            Str(credits?["balance"]),
            credits?["unlimited"] is JsonValue un && un.TryGetValue(out bool b) && b,
            DateTime.Now);
    }

    private static CodexWindow? Window(JsonNode? n)
    {
        if (n is not JsonObject w || w["usedPercent"] is not JsonValue p || !p.TryGetValue(out double pct))
            return null;
        long? mins = w["windowDurationMins"] is JsonValue m && m.TryGetValue(out long mm) ? mm : null;
        return new CodexWindow((int)Math.Round(pct), mins, Unix(w["resetsAt"]));
    }

    private static DateTime? Unix(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out long s) && s > 0
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime
            : null;

    // The backend sends amounts as strings ("8000", "9.99"); accept numbers too.
    private static decimal? Dec(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue(out decimal d)) return d;
        return v.TryGetValue(out string? s) &&
               decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        s = (i < 0 ? s : s[..i]).Trim();
        return s.Length > 80 ? s[..80] + "…" : s;
    }

    /// <summary>
    /// Locates codex: a native codex.exe on PATH (standalone/winget install) first, else the
    /// npm shim codex.cmd, which has to go through cmd.exe. Kill(entireProcessTree) cleans up
    /// the node + native children the shim spawns.
    /// </summary>
    private static (string File, string Args)? FindCodex()
    {
        var dirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));

        foreach (string name in new[] { "codex.exe", "codex.cmd" })
            foreach (var dir in dirs)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string p = Path.Combine(dir.Trim().Trim('"'), name);
                    if (!File.Exists(p)) continue;
                    return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? (p, "app-server")
                        : (Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /s /c \"\"{p}\" app-server\"");
                }
                catch { }
            }
        return null;
    }
}

internal sealed class CodexUsageException(string message) : Exception(message);
