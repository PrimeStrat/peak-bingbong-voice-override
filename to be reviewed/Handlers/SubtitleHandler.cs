using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BingBongVoiceOverride.Patches;
using UnityEngine;
namespace BingBongVoiceOverride;

public partial class Plugin {
    internal static void SaveSubtitleOverrideForClip(string clipName, string subtitle) {
        if (string.IsNullOrWhiteSpace(clipName)) return;
        string jsonPath = Path.Combine(SoundsFolder, clipName + ".json");
        string value = subtitle?.Trim() ?? string.Empty;
                 {
            if (string.IsNullOrWhiteSpace(value)) {
                SubtitleOverrides.Remove(clipName);
                TimedSubtitleOverrides.Remove(clipName);
                if (ActiveTimedSubtitleClipName.Equals(clipName, StringComparison.OrdinalIgnoreCase)) {
                    ActiveTimedSubtitleClipName = string.Empty;
                    ActiveTimedSubtitleStart = 0f;
                    ActiveTimedSubtitleLastTick = 0f;
                    ActiveSubtitle = string.Empty;
                    ActiveSubtitleUntil = 0f;
                }
                if (File.Exists(jsonPath)) File.Delete(jsonPath);
                return;
            }

            ClipSubtitleJson data = new() {
                subtitle = value,
            };

            File.WriteAllText(jsonPath, JsonSerializer.Serialize(data, JsonOptions));
            SubtitleOverrides[clipName] = value;
            TimedSubtitleOverrides.Remove(clipName);
        }
        catch (Exception ex) {
            Log.LogWarning($"SaveSubtitleOverrideForClip failed for '{clipName}': {ex.Message}");
        }
    }

    internal static void RemoveTimedSubtitleForClip(string clipName) {
        if (string.IsNullOrWhiteSpace(clipName)) return;
        TimedSubtitleOverrides.Remove(clipName);
        if (ActiveTimedSubtitleClipName.Equals(clipName, StringComparison.OrdinalIgnoreCase)) {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
        }
        SubtitleOverrides.TryGetValue(clipName, out string existing);
        SaveSubtitleOverrideForClip(clipName, existing ?? string.Empty);
    }

    private static void UpdateTimedSubtitleState() {
        if (string.IsNullOrWhiteSpace(ActiveTimedSubtitleClipName)) return;

        if (!TimedSubtitlesEnabled.Value) {
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
            || lines == null || lines.Count == 0) {
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
        for (int i = lines.Count - 1; i >= 0; i--) {
            TimedSubtitleLine line = lines[i];
            if (elapsed >= line.Start && elapsed <= line.End) { current = line; break; }
        }

        if (current != null) {
            ActiveSubtitle = current.Text;
            ActiveSubtitleUntil = Time.unscaledTime + Mathf.Max(0.05f, current.End - elapsed);
            ActiveTimedSubtitleLastTick = now;
            return;
        }

        ActiveSubtitle = ActiveNativeSubtitle ?? string.Empty;
        ActiveSubtitleUntil = string.IsNullOrEmpty(ActiveSubtitle) ? 0f : Time.unscaledTime + 0.5f;
        if (elapsed > lines[lines.Count - 1].End + 0.25f) {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleClip = null;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveNativeSubtitle = string.Empty;
        }
                 {
            ActiveTimedSubtitleLastTick = now;
        }
    }

    private static void TickNativeTimedSubtitle() {
        if (!IsTimedSubtitleActive || ActiveTimedSubtitleClip == null) return;
        string id = NativeBingBongHandler.GetAssignedSubtitleId(ActiveTimedSubtitleClip);
        if (!string.IsNullOrEmpty(id))
            NativeBingBongHandler.WriteSubtitle(id, ActiveSubtitle ?? string.Empty);
    }

    private string ReadSubtitleOverride(string audioPath) {
        string jsonPath = Path.ChangeExtension(audioPath, ".json");
        if (!File.Exists(jsonPath)) return string.Empty;
                 {
            string raw = File.ReadAllText(jsonPath);
            ClipSubtitleJson? parsed = JsonSerializer.Deserialize<ClipSubtitleJson>(raw, JsonOptions);
            return parsed?.subtitle?.Trim() ?? string.Empty;
        }
        catch (Exception ex) {
            Log.LogWarning($"Subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
            return string.Empty;
        }
    }

    private List<TimedSubtitleLine> ReadTimedSubtitleOverrides(string audioPath) {
        List<TimedSubtitleLine> lines = [];
        string jsonPath = Path.ChangeExtension(audioPath, ".json");
        if (!File.Exists(jsonPath)) return lines;
                 {
            string raw = File.ReadAllText(jsonPath);
            ClipSubtitleJson? parsed = JsonSerializer.Deserialize<ClipSubtitleJson>(raw, JsonOptions);
            List<TimedSubtitleJson>? timed = parsed?.timedSubtitles;
            if (timed == null || timed.Count == 0)
                return lines;

            for (int i = 0; i < timed.Count; i++) {
                TimedSubtitleJson entry = timed[i];
                float start = entry.start;
                float end = entry.end;
                if (end <= start) continue;
                string text = entry.text?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                    lines.Add(new TimedSubtitleLine { Start = start, End = end, Text = text });
            }
            lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        catch (Exception ex) {
            Log.LogWarning($"Timed subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
        }
        return lines;
    }

    private bool WriteTimedSubtitleJsonFromVtt(string timestamp, string finalAudioPath, string subtitleJsonPath) {
        string[] vtts = Directory.GetFiles(SoundsFolder, $"yt_{timestamp}*.vtt", SearchOption.TopDirectoryOnly);
        if (vtts.Length == 0) return false;

        string selectedVtt = vtts[0];
        bool selectedIsAuto = LooksLikeAutoSubFile(selectedVtt);
        for (int i = 1; i < vtts.Length; i++) {
            bool candidateAuto = LooksLikeAutoSubFile(vtts[i]);
            if (selectedIsAuto && !candidateAuto) { selectedVtt = vtts[i]; selectedIsAuto = false; }
        }

        List<TimedSubtitleLine> best = ParseVttTimedSubtitles(selectedVtt);
        if (selectedIsAuto) best = DedupeRollingCues(best);
        foreach (string v in vtts) { try { File.Delete(v); } catch (Exception) { } }

        if (best.Count == 0) return false;

        ClipSubtitleJson data = new() {
            subtitle = best[0].Text,
            timedSubtitles = new List<TimedSubtitleJson>(),
        };

        for (int i = 0; i < best.Count; i++) {
            TimedSubtitleLine line = best[i];
            data.timedSubtitles.Add(new TimedSubtitleJson
            {
                start = line.Start,
                end = line.End,
                text = line.Text,
            });
        }

        File.WriteAllText(subtitleJsonPath, JsonSerializer.Serialize(data, JsonOptions));
        return true;
    }

    private List<TimedSubtitleLine> ParseVttTimedSubtitles(string vttPath) {
        List<TimedSubtitleLine> lines = [];
        string[] rawLines = File.ReadAllLines(vttPath);
        int i = 0;
        while (i < rawLines.Length) {
            string line = rawLines[i].Trim();
            if (!line.Contains("-->")) { i++; continue; }

            string[] parts = line.Split(["-->"], StringSplitOptions.None);
            if (parts.Length != 2) { i++; continue; }

            float start = ParseVttTime(parts[0]);
            float end = ParseVttTime(parts[1]);
            i++;

            StringBuilder text = new();
            while (i < rawLines.Length && !string.IsNullOrWhiteSpace(rawLines[i])) {
                string t = Regex.Replace(rawLines[i], "<.*?>", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(t)) {
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

    private static bool LooksLikeAutoSubFile(string vttPath) {
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

    private static List<TimedSubtitleLine> DedupeRollingCues(List<TimedSubtitleLine> lines) {
        if (lines == null || lines.Count == 0) return lines ?? [];
        List<TimedSubtitleLine> result = [];
        for (int i = 0; i < lines.Count; i++) {
            TimedSubtitleLine current = lines[i];
            string normalized = NormalizeCueText(current.Text);
            if (string.IsNullOrEmpty(normalized)) continue;

            if (i + 1 < lines.Count) {
                string next = NormalizeCueText(lines[i + 1].Text);
                if (next.Length > normalized.Length
                    && next.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    && (lines[i + 1].Start - current.Start) < 1.5f)
                    continue;
            }

            if (result.Count > 0) {
                TimedSubtitleLine prev = result[result.Count - 1];
                if (NormalizeCueText(prev.Text).Equals(normalized, StringComparison.OrdinalIgnoreCase)) {
                    prev.End = Mathf.Max(prev.End, current.End);
                    result[result.Count - 1] = prev;
                    continue;
                }
            }
            result.Add(new TimedSubtitleLine { Start = current.Start, End = current.End, Text = current.Text });
        }
        return result;
    }

    private static string NormalizeCueText(string text) {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static float ParseVttTime(string value) {
        string t = value.Trim();
        int space = t.IndexOf(' ');
        if (space >= 0) t = t.Substring(0, space).Trim();

        string[] parts = t.Split(':');
        if (parts.Length < 2 || parts.Length > 3) return -1f;

        System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.NumberStyles ns = System.Globalization.NumberStyles.Float;
        if (parts.Length == 3) {
            if (!float.TryParse(parts[0], ns, inv, out float h)) return -1f;
            if (!float.TryParse(parts[1], ns, inv, out float m)) return -1f;
            if (!float.TryParse(parts[2], ns, inv, out float s)) return -1f;
            return h * 3600f + m * 60f + s;
        }
                 {
            if (!float.TryParse(parts[0], ns, inv, out float m)) return -1f;
            if (!float.TryParse(parts[1], ns, inv, out float s)) return -1f;
            return m * 60f + s;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private sealed class ClipSubtitleJson {
        public string subtitle { get; set; } = string.Empty;
        public List<TimedSubtitleJson>? timedSubtitles { get; set; }
    }

    private sealed class TimedSubtitleJson {
        public float start { get; set; }
        public float end { get; set; }
        public string text { get; set; } = string.Empty;
    }
}
