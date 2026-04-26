using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using BingBongVoiceOverride.Patches;
using UnityEngine;
namespace BingBongVoiceOverride;

public partial class Plugin
{
    // Saves or clears the subtitle override JSON for a specific clip and updates in-memory mappings.
    // clipName (string): Clip name without extension.
    // subtitle (string): Subtitle text to persist. Empty clears the override.
    // returns: void
    internal static void SaveSubtitleOverrideForClip(string clipName, string subtitle)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return;
        string jsonPath = Path.Combine(SoundsFolder, clipName + ".json");
        string value = subtitle?.Trim() ?? string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                SubtitleOverrides.Remove(clipName);
                TimedSubtitleOverrides.Remove(clipName);
                if (ActiveTimedSubtitleClipName.Equals(clipName, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveTimedSubtitleClipName = string.Empty;
                    ActiveTimedSubtitleStart = 0f;
                    ActiveTimedSubtitleLastTick = 0f;
                    ActiveSubtitle = string.Empty;
                    ActiveSubtitleUntil = 0f;
                }
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
                return;
            }
            string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            File.WriteAllText(jsonPath, "{\n  \"subtitle\": \"" + escaped + "\"\n}\n");
            SubtitleOverrides[clipName] = value;
            TimedSubtitleOverrides.Remove(clipName);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"SaveSubtitleOverrideForClip failed for '{clipName}': {ex.Message}");
        }
    }

    // Removes the timed subtitle track for a clip from memory and rewrites its JSON to keep only the single subtitle line.
    // clipName (string): Clip name without extension.
    // returns: void
    internal static void RemoveTimedSubtitleForClip(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return;
        TimedSubtitleOverrides.Remove(clipName);
        if (ActiveTimedSubtitleClipName.Equals(clipName, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
        }
        SubtitleOverrides.TryGetValue(clipName, out string existing);
        SaveSubtitleOverrideForClip(clipName, existing ?? string.Empty);
    }

    // Updates ActiveSubtitle each frame using the active timed subtitle track, if any.
    // returns: void
    private static void UpdateTimedSubtitleState()
    {
        if (string.IsNullOrWhiteSpace(ActiveTimedSubtitleClipName)) return;

        if (!TimedSubtitlesEnabled.Value)
        {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleClip = null;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
            ActiveNativeSubtitle = string.Empty;
            return;
        }

        float now = Time.unscaledTime;
        if (ActiveTimedSubtitleLastTick <= 0f) ActiveTimedSubtitleLastTick = now;

        if (!TimedSubtitleOverrides.TryGetValue(ActiveTimedSubtitleClipName, out List<TimedSubtitleLine> lines)
            || lines == null || lines.Count == 0)
        {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleClip = null;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
            ActiveNativeSubtitle = string.Empty;
            return;
        }

        float elapsed = (PluginAudioSource != null && PluginAudioSource.isPlaying && PluginAudioSource.clip != null)
            ? PluginAudioSource.time
            : now - ActiveTimedSubtitleStart;

        TimedSubtitleLine? current = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            TimedSubtitleLine line = lines[i];
            if (elapsed >= line.Start && elapsed <= line.End) { current = line; break; }
        }

        if (current != null)
        {
            ActiveSubtitle = current.Text;
            ActiveSubtitleUntil = Time.unscaledTime + Mathf.Max(0.05f, current.End - elapsed);
            ActiveTimedSubtitleLastTick = now;
            return;
        }

        ActiveSubtitle = ActiveNativeSubtitle ?? string.Empty;
        ActiveSubtitleUntil = string.IsNullOrEmpty(ActiveSubtitle) ? 0f : Time.unscaledTime + 0.5f;
        if (elapsed > lines[lines.Count - 1].End + 0.25f)
        {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleClip = null;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveNativeSubtitle = string.Empty;
        }
        else
        {
            ActiveTimedSubtitleLastTick = now;
        }
    }

    // Continuously writes the active timed subtitle line into LocalizedText.MAIN_TABLE so the native UI stays current.
    // returns: void
    private static void TickNativeTimedSubtitle()
    {
        if (!IsTimedSubtitleActive || ActiveTimedSubtitleClip == null) return;
        string id = NativeBingBongBridge.GetAssignedSubtitleId(ActiveTimedSubtitleClip);
        if (!string.IsNullOrEmpty(id))
            NativeBingBongBridge.WriteSubtitle(id, ActiveSubtitle ?? string.Empty);
    }

    // Reads a companion JSON file and returns subtitle text, or empty string if not present/invalid.
    // audioPath (string): Absolute path to the audio file.
    // returns: string
    private string TryReadSubtitleOverride(string audioPath)
    {
        string jsonPath = Path.ChangeExtension(audioPath, ".json");
        if (!File.Exists(jsonPath)) return string.Empty;
        try
        {
            string raw = File.ReadAllText(jsonPath);
            Match match = Regex.Match(raw, "\"subtitle\"\\s*:\\s*\"(?<v>(?:\\\\.|[^\"])*)\"", RegexOptions.IgnoreCase);
            if (!match.Success) return string.Empty;
            return Regex.Unescape(match.Groups["v"].Value).Trim();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
            return string.Empty;
        }
    }

    // Reads timed subtitle entries from clip JSON using timedSubtitles/timed_subtitles array format.
    // audioPath (string): Absolute path to the audio file.
    // returns: List<TimedSubtitleLine>
    private List<TimedSubtitleLine> TryReadTimedSubtitleOverrides(string audioPath)
    {
        List<TimedSubtitleLine> lines = [];
        string jsonPath = Path.ChangeExtension(audioPath, ".json");
        if (!File.Exists(jsonPath)) return lines;
        try
        {
            string raw = File.ReadAllText(jsonPath);
            if (!Regex.IsMatch(raw, "\"(?:timedSubtitles|timed_subtitles)\"\\s*:", RegexOptions.IgnoreCase))
                return lines;

            MatchCollection entries = Regex.Matches(raw,
                "\\{\\s*\"start\"\\s*:\\s*(?<start>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"end\"\\s*:\\s*(?<end>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"text\"\\s*:\\s*\"(?<text>(?:\\\\.|[^\"])*)\"\\s*\\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match entry in entries)
            {
                if (!float.TryParse(entry.Groups["start"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float start)) continue;
                if (!float.TryParse(entry.Groups["end"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float end)) continue;
                if (end <= start) continue;
                string text = Regex.Unescape(entry.Groups["text"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(new TimedSubtitleLine { Start = start, End = end, Text = text });
            }
            lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Timed subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
        }
        return lines;
    }

    // Builds a timed subtitle JSON companion from downloaded VTT files, if present.
    // timestamp (string): Import timestamp prefix used for temporary output names.
    // finalAudioPath (string): Final saved audio path.
    // subtitleJsonPath (string): JSON output path to write.
    // returns: bool
    private bool TryWriteTimedSubtitleJsonFromVtt(string timestamp, string finalAudioPath, string subtitleJsonPath)
    {
        string[] vtts = Directory.GetFiles(SoundsFolder, $"yt_{timestamp}*.vtt", SearchOption.TopDirectoryOnly);
        if (vtts.Length == 0) return false;

        string selectedVtt = vtts[0];
        bool selectedIsAuto = LooksLikeAutoSubFile(selectedVtt);
        for (int i = 1; i < vtts.Length; i++)
        {
            bool candidateAuto = LooksLikeAutoSubFile(vtts[i]);
            if (selectedIsAuto && !candidateAuto) { selectedVtt = vtts[i]; selectedIsAuto = false; }
        }

        List<TimedSubtitleLine> best = ParseVttTimedSubtitles(selectedVtt);
        if (selectedIsAuto) best = DedupeRollingCues(best);
        foreach (string v in vtts) { try { File.Delete(v); } catch (Exception) { } }

        if (best.Count == 0) return false;

        StringBuilder sb = new();
        sb.Append("{\n  \"subtitle\": \"").Append(EscapeJson(best[0].Text)).Append("\",\n  \"timedSubtitles\": [\n");
        for (int i = 0; i < best.Count; i++)
        {
            TimedSubtitleLine line = best[i];
            sb.Append("    { \"start\": ")
                .Append(line.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"end\": ")
                .Append(line.End.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"text\": \"").Append(EscapeJson(line.Text)).Append("\" }");
            if (i < best.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}");
        File.WriteAllText(subtitleJsonPath, sb.ToString());
        return true;
    }

    // Parses a .vtt subtitle file into timed line entries.
    // vttPath (string): Absolute path to .vtt file.
    // returns: List<TimedSubtitleLine>
    private List<TimedSubtitleLine> ParseVttTimedSubtitles(string vttPath)
    {
        List<TimedSubtitleLine> lines = [];
        string[] rawLines = File.ReadAllLines(vttPath);
        int i = 0;
        while (i < rawLines.Length)
        {
            string line = rawLines[i].Trim();
            if (!line.Contains("-->")) { i++; continue; }

            string[] parts = line.Split(["-->"], StringSplitOptions.None);
            if (parts.Length != 2) { i++; continue; }

            float start = ParseVttTime(parts[0]);
            float end = ParseVttTime(parts[1]);
            i++;

            StringBuilder text = new();
            while (i < rawLines.Length && !string.IsNullOrWhiteSpace(rawLines[i]))
            {
                string t = Regex.Replace(rawLines[i], "<.*?>", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    if (text.Length > 0) text.Append(' ');
                    text.Append(t);
                }
                i++;
            }

            string finalText = WebUtility.HtmlDecode(text.ToString().Trim());
            if (start >= 0f && end > start && !string.IsNullOrWhiteSpace(finalText))
                lines.Add(new TimedSubtitleLine { Start = start, End = end, Text = finalText });
            i++;
        }
        return lines;
    }

    // Returns true when the VTT file looks like a YouTube auto-generated caption.
    // vttPath (string): Absolute path to the VTT file.
    // returns: bool
    private static bool LooksLikeAutoSubFile(string vttPath)
    {
        try
        {
            char[] buf = new char[1024];
            int n;
            using (StreamReader r = new(vttPath)) n = r.Read(buf, 0, buf.Length);
            string head = new string(buf, 0, n);
            return head.Contains("Kind: captions", StringComparison.OrdinalIgnoreCase)
                || head.Contains("<c>", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(vttPath).Contains(".auto", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    // Collapses YouTube auto-caption rolling cues where each line is a prefix-extension of the next, leaving only completed phrases.
    // lines (List<TimedSubtitleLine>): Raw cue list from the parser.
    // returns: List<TimedSubtitleLine>
    private static List<TimedSubtitleLine> DedupeRollingCues(List<TimedSubtitleLine> lines)
    {
        if (lines == null || lines.Count == 0) return lines ?? [];
        List<TimedSubtitleLine> result = [];
        for (int i = 0; i < lines.Count; i++)
        {
            TimedSubtitleLine current = lines[i];
            string normalized = NormalizeCueText(current.Text);
            if (string.IsNullOrEmpty(normalized)) continue;

            if (i + 1 < lines.Count)
            {
                string next = NormalizeCueText(lines[i + 1].Text);
                if (next.Length > normalized.Length
                    && next.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    && (lines[i + 1].Start - current.Start) < 1.5f)
                    continue;
            }

            if (result.Count > 0)
            {
                TimedSubtitleLine prev = result[result.Count - 1];
                if (NormalizeCueText(prev.Text).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    prev.End = Mathf.Max(prev.End, current.End);
                    result[result.Count - 1] = prev;
                    continue;
                }
            }
            result.Add(new TimedSubtitleLine { Start = current.Start, End = current.End, Text = current.Text });
        }
        return result;
    }

    // Normalizes cue text for prefix/dedupe comparison by collapsing whitespace and lowercasing.
    // text (string): Raw cue text.
    // returns: string
    private static string NormalizeCueText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    // Parses a VTT timestamp into seconds.
    // value (string): Raw VTT time segment.
    // returns: float
    private static float ParseVttTime(string value)
    {
        string t = value.Trim();
        int space = t.IndexOf(' ');
        if (space >= 0) t = t.Substring(0, space).Trim();

        string[] parts = t.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return -1f;

        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Float;
        if (parts.Length == 3)
        {
            if (!float.TryParse(parts[0], ns, inv, out float h)) return -1f;
            if (!float.TryParse(parts[1], ns, inv, out float m)) return -1f;
            if (!float.TryParse(parts[2], ns, inv, out float s)) return -1f;
            return h * 3600f + m * 60f + s;
        }
        else
        {
            if (!float.TryParse(parts[0], ns, inv, out float m)) return -1f;
            if (!float.TryParse(parts[1], ns, inv, out float s)) return -1f;
            return m * 60f + s;
        }
    }

    // Escapes a string for JSON output.
    // value (string): Input string to escape.
    // returns: string
    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
