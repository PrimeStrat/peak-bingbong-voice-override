using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BingBongVoiceOverride.Patches;
using UnityEngine;
using UnityEngine.Networking;
namespace BingBongVoiceOverride;

public partial class Plugin {
    internal static List<AudioClip> GetActiveClips() {
        float now = Time.unscaledTime;
        if (now < _cachedActiveClipsExpiry)
            return _cachedActiveClips;
        _cachedActiveClipsExpiry = now + 0.15f;
        _cachedActiveClips = [];
        for (int i = 0; i < CustomClips.Count; i++) {
            AudioClip clip = CustomClips[i];
            if (PendingSyncClipNames.Contains(clip.name)) continue;
            if (!EnabledClips.TryGetValue(clip.name, out bool enabled) || enabled)
                _cachedActiveClips.Add(clip);
        }
        return _cachedActiveClips;
    }

    internal static void SaveSelection() {
        try {
            string path = Path.Combine(SoundsFolder, "selection.json");
            string json = JsonSerializer.Serialize(EnabledClips, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex) {
            Log.LogWarning($"SaveSelection failed: {ex.Message}");
        }
    }

    internal static void LoadSelection() {
        EnabledClips.Clear();
        string path = Path.Combine(SoundsFolder, "selection.json");
        if (!File.Exists(path)) return;
        try {
            string json = File.ReadAllText(path);
            Dictionary<string, bool>? loaded = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);
            if (loaded == null) {
                return;
            }

            foreach (KeyValuePair<string, bool> item in loaded) {
                if (!string.IsNullOrWhiteSpace(item.Key)) {
                    EnabledClips[item.Key] = item.Value;
                }
            }
        }
        catch (Exception ex) {
            Log.LogWarning($"LoadSelection failed: {ex.Message}");
        }
    }

    internal static void ApplySpatialSettings(AudioSource source) {
        source.spatialBlend = 0f;
    }

    internal static void RegisterManagedSource(AudioSource source) {
        if (source == null || ManagedSources.Contains(source)) return;
        ManagedSources.Add(source);
    }

    internal static void OnClipPlayed(AudioClip clip) {
        DebugLastPlayed = clip.name;
        Patches.SubtitleTextOverridePatches.BeginDiscoveryWindow(2.0f);

        bool hasSaved = SubtitleOverrides.TryGetValue(clip.name, out string savedSubtitle)
            && !string.IsNullOrWhiteSpace(savedSubtitle);

        if (TimedSubtitlesEnabled.Value
            && TimedSubtitleOverrides.TryGetValue(clip.name, out List<TimedSubtitleLine> timedLines)
            && timedLines != null && timedLines.Count > 0) {
            ActiveTimedSubtitleClipName = clip.name;
            ActiveTimedSubtitleClip = clip;
            ActiveTimedSubtitleStart = Time.unscaledTime;
            ActiveTimedSubtitleLastTick = ActiveTimedSubtitleStart;
            ActiveNativeSubtitle = hasSaved ? savedSubtitle : string.Empty;
            ActiveSubtitle = ActiveNativeSubtitle;
            ActiveSubtitleUntil = Time.unscaledTime + Mathf.Max(clip.length, 0.8f);
            return;
        }

        ActiveTimedSubtitleClipName = string.Empty;
        ActiveTimedSubtitleStart = 0f;
        ActiveTimedSubtitleLastTick = 0f;
        ActiveNativeSubtitle = string.Empty;

        if (!hasSaved) {
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
            return;
        }

        ActiveSubtitle = savedSubtitle;
        ActiveSubtitleUntil = Time.unscaledTime + Mathf.Max(clip.length, 0.8f);
        WriteNativeSubtitleForClip(clip, ActiveSubtitle);
    }

    private static void WriteNativeSubtitleForClip(AudioClip clip, string text) {
        if (!UseNativeBingBongAPI.Value || clip == null) return;
        string id = NativeBingBongHandler.GetAssignedSubtitleId(clip);
        if (!string.IsNullOrEmpty(id))
            NativeBingBongHandler.WriteSubtitle(id, text ?? string.Empty);
    }

    private IEnumerator LoadCustomClips() {
        string[] extensions = ["*.wav", "*.ogg"];
        List<string> files = [];
        foreach (string ext in extensions)
            files.AddRange(Directory.GetFiles(SoundsFolder, ext, SearchOption.TopDirectoryOnly));

        if (files.Count == 0) {
            Log.LogInfo("No custom audio files found - Bing Bong keeps his default SFX.");
            ClipsReady = true;
            yield break;
        }

        Log.LogInfo($"Loading {files.Count} audio file(s)...");
        foreach (string filePath in files) {
            if (Path.GetFileNameWithoutExtension(filePath).Equals("example", StringComparison.OrdinalIgnoreCase))
                continue;
            AudioType audioType = Path.GetExtension(filePath).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                ? AudioType.OGGVORBIS : AudioType.WAV;
            string uri = "file:///" + filePath.Replace('\\', '/');
            using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) {
                Log.LogWarning($"Failed to load '{Path.GetFileName(filePath)}': {request.error}");
                continue;
            }
            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            clip.name = Path.GetFileNameWithoutExtension(filePath);
            CustomClips.Add(clip);
            string subtitle = ReadSubtitleOverride(filePath);
            if (!string.IsNullOrWhiteSpace(subtitle)) {
                SubtitleOverrides[clip.name] = subtitle;
                Log.LogInfo($"  Subtitle override: {clip.name}");
            }
            List<TimedSubtitleLine> timed = ReadTimedSubtitleOverrides(filePath);
            if (timed.Count > 0) {
                TimedSubtitleOverrides[clip.name] = timed;
                Log.LogInfo($"  Timed subtitles: {clip.name} ({timed.Count})");
            }
            Log.LogInfo($"  Loaded: {clip.name} ({clip.length:F2}s)");
        }

        LoadSelection();
        foreach (AudioClip c in CustomClips) {
            if (!EnabledClips.ContainsKey(c.name))
                EnabledClips[c.name] = true;
        }
        SaveSelection();
        ClipsReady = true;

        if (CustomClips.Count > 0) {
            Log.LogInfo($"Override active: {CustomClips.Count} clip(s) ready.");
            Log.LogInfo($"Loaded {SubtitleOverrides.Count} subtitle override(s).");
            if (UseNativeBingBongAPI.Value) {
                int rewritten = NativeBingBongHandler.RewriteAllInScene();
                if (rewritten > 0)
                    Log.LogInfo($"Rewrote {rewritten} live Action_AskBingBong instance(s) with custom responses.");
            }
            if (!BingBongNetworkSync.IsConnectedAsClient) {
                bool wasHosting = BingBongNetworkSync.IsHosting;
                BingBongNetworkSync.StartServer();
                if (!wasHosting)
                    BingBongNetworkSync.BroadcastRefresh();
            }
        }
                 {
            Log.LogWarning("No clips loaded successfully - Bing Bong keeps his default SFX.");
        }
    }

    private static void EnsureExampleFiles() {
        string readmePath = Path.Combine(SoundsFolder, "README.txt");
        if (!File.Exists(readmePath)) {
            StringBuilder sb = new();
            sb.AppendLine("BingBong Voice Override - Sounds Folder");
            sb.AppendLine();
            sb.AppendLine("1) Put .ogg or .wav files in this folder.");
            sb.AppendLine("2) Optional: add a same-name .json file with a subtitle override.");
            sb.AppendLine("   Example: custom_laugh.ogg + custom_laugh.json");
            sb.AppendLine("   JSON format: { \"subtitle\": \"Bing Bong laughs loudly\" }");
            sb.AppendLine("3) Press the refresh key in-game to reload without restart.");
            sb.AppendLine();
            sb.AppendLine("Importer Menu:");
            sb.AppendLine("- Toggle with MenuToggleKey (default F6)");
            sb.AppendLine("- Paste a direct audio link and click Download + Refresh");
            File.WriteAllText(readmePath, sb.ToString());
        }

        string exampleJsonPath = Path.Combine(SoundsFolder, "example.json");
        if (!File.Exists(exampleJsonPath))
            File.WriteAllText(exampleJsonPath, "{\n  \"subtitle\": \"Bing Bong says hello from JSON\"\n}\n");

        string exampleOggPath = Path.Combine(SoundsFolder, "example.ogg");
        if (!File.Exists(exampleOggPath))
            File.WriteAllBytes(exampleOggPath, System.Text.Encoding.ASCII.GetBytes("OggS_example_placeholder_replace_with_real_audio"));
    }
}
