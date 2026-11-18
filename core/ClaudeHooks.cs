using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeCodeIndicator;

/// <summary>Thrown when ~/.claude/settings.json can't be read or written. The message is user-facing.</summary>
public sealed class ClaudeSettingsException(string message) : Exception(message);

/// <summary>
/// Installs and removes this app's hooks in Claude Code's user settings
/// (<c>~/.claude/settings.json</c>). Edits are surgical and idempotent: a hook is "ours"
/// purely because its handler URL points at the local listener (<see cref="IsMyHandler"/>).
/// </summary>
public static class ClaudeHooks
{
    // The hooks this app manages. Each is written as a matcher group with a single HTTP
    // handler pointing at our local listener.
    private static readonly (string Event, string Path, string? Matcher)[] HookSpec =
    {
        ("UserPromptSubmit", "working", null),
        ("PreToolUse",       "working", null),
        // Only permission_prompt means "blocked, needs you" → yellow. idle_prompt
        // fires when Claude is *done* and waiting for the next prompt, so routing it
        // to /waiting would override the green from Stop and leave the icon stuck on
        // yellow after every finished turn.
        ("Notification",     "waiting", "permission_prompt"),
        ("Stop",             "done",    null),
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string SettingsPath =>
        Path.Combine(ExecutableSearch.HomeDirectory, ".claude", "settings.json");

    public static bool IsInstalled()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return false;
            return JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && HasMyHooks(root);
        }
        catch { return false; }
    }

    /// <summary>
    /// Removes our hooks if present, else adds them. Writes <c>settings.json.bak</c> first.
    /// Returns true if the hooks are now installed. Throws <see cref="ClaudeSettingsException"/>
    /// (with nothing changed) if the file can't be read, e.g. JSONC or invalid JSON.
    /// </summary>
    public static bool Toggle()
    {
        string path = SettingsPath;
        JsonObject root;
        try
        {
            if (File.Exists(path))
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                root = new JsonObject();
            }
        }
        catch (Exception ex)
        {
            throw new ClaudeSettingsException(
                $"Could not read Claude Code settings:\n{path}\n\n{ex.Message}\n\n" +
                "If the file has comments or invalid JSON, fix it first — nothing was changed.");
        }

        bool installed = HasMyHooks(root);
        if (installed) RemoveMyHooks(root);
        else InstallMyHooks(root);

        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, root.ToJsonString(Indented));
        }
        catch (Exception ex)
        {
            throw new ClaudeSettingsException($"Could not write Claude Code settings:\n{path}\n\n{ex.Message}");
        }
        return !installed;
    }

    public static bool HasMyHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return false;
        foreach (var kvp in hooks)
        {
            if (kvp.Value is not JsonArray groups) continue;
            foreach (var g in groups)
                if (g is JsonObject group && group["hooks"] is JsonArray handlers)
                    foreach (var h in handlers)
                        if (IsMyHandler(h)) return true;
        }
        return false;
    }

    public static void InstallMyHooks(JsonObject root)
    {
        RemoveMyHooks(root); // idempotent: clear any existing indicator hooks first

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var (ev, path, matcher) in HookSpec)
        {
            if (hooks[ev] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[ev] = groups;
            }

            var handler = new JsonObject
            {
                ["type"] = "http",
                ["url"] = $"http://localhost:{HookListener.Port}/{path}"
            };
            var group = new JsonObject();
            if (matcher != null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray { handler };
            groups.Add(group);
        }
    }

    public static void RemoveMyHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;

        foreach (var ev in hooks.Select(k => k.Key).ToList())
        {
            if (hooks[ev] is not JsonArray groups) continue;

            for (int i = groups.Count - 1; i >= 0; i--)
            {
                if (groups[i] is not JsonObject group) continue;
                if (group["hooks"] is JsonArray handlers)
                {
                    for (int h = handlers.Count - 1; h >= 0; h--)
                        if (IsMyHandler(handlers[h]))
                            handlers.RemoveAt(h);

                    if (handlers.Count == 0) groups.RemoveAt(i);
                }
            }

            if (groups.Count == 0) hooks.Remove(ev);
        }

        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static bool IsMyHandler(JsonNode? handler)
    {
        if (handler is not JsonObject o) return false;
        if (o["url"] is not JsonValue v || !v.TryGetValue<string>(out var url) || url is null)
            return false;
        return url.Contains($"localhost:{HookListener.Port}/", StringComparison.OrdinalIgnoreCase)
            || url.Contains($"127.0.0.1:{HookListener.Port}/", StringComparison.OrdinalIgnoreCase);
    }
}
