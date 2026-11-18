using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;

namespace ClaudeCodeIndicator;

/// <summary>Mirrors the state to the optional ESP32 display. Fire-and-forget, 2 s timeout.</summary>
public sealed class EspClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// <c>http://&lt;esp&gt;/state?value=&lt;state&gt;&amp;rgb=&lt;hex&gt;</c> plus the quota gauge
    /// and Codex fields. Numbers are always formatted with the invariant culture.
    /// </summary>
    public static string BuildUrl(string address, IndicatorState state, UsageReading? usage,
                                  IndicatorState codexState, CodexUsageReading? codexUsage, DateOnly today)
    {
        (string name, string hex) = IndicatorStates.Describe(state);
        string url = $"http://{address}/state?value={name.ToLowerInvariant()}&rgb={hex}";
        // Monthly usage credits for the matrix's quota gauge (shown in the done
        // state). Left out when unknown, which tells the ESP to show the mascot.
        if (usage is { Limit: > 0 } u)
            url += string.Format(CultureInfo.InvariantCulture,
                "&spent={0:0.00}&limit={1:0.00}&allowed={2:0.00}",
                u.Spent, u.Limit, WorkDays.AllowanceToDate(u.Limit, today));
        // Codex state and usage for the round display (the matrix ignores them).
        url += $"&codex={IndicatorStates.Describe(codexState).Label.ToLowerInvariant()}";
        if (codexUsage?.GaugeUsedPercent is { } cused)
            url += string.Format(CultureInfo.InvariantCulture, "&cused={0:0.0}", cused);
        return url;
    }

    /// <summary>Sends the URL; an absent or offline ESP is silently ignored.</summary>
    public async Task PushAsync(string url)
    {
        try { using var resp = await _http.GetAsync(url); }
        catch { /* ESP offline or not present — ignore */ }
    }

    public void Dispose() => _http.Dispose();
}
