using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BingBongVoiceOverride.Patches;
using UnityEngine;
using UnityEngine.Networking;
namespace BingBongVoiceOverride;

public partial class Plugin
{
    // Returns the list of currently enabled clips, rebuilt at most every 150 ms for performance.
    // returns: List<AudioClip>
    internal static List<AudioClip> GetActiveClips()
    {
        float now = Time.unscaledTime;
        if (now < _cachedActiveClipsExpiry)
            return _cachedActiveClips;
        _cachedActiveClipsExpiry = now + 0.15f;
        _cachedActiveClips = [];
        for (int i = 0; i < CustomClips.Count; i++)
        {
            AudioClip clip = CustomClips[i];
            if (PendingSyncClipNames.Contains(clip.name)) continue;
            if (!EnabledClips.TryGetValue(clip.name, out bool enabled) || enabled)
                _cachedActiveClips.Add(clip);
        }
        return _cachedActiveClips;
    }

    // Persists the EnabledClips dictionary to selection.json in the sounds folder.
    // returns: void
    internal static void SaveSelection()
    {
        try
        {
            string path = Path.Combine(SoundsFolder, "selection.json");
            StringBuilder sb = new();
            sb.Append('{');
            bool first = true;
            foreach (KeyValuePair<string, bool> kv in EnabledClips)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"');
                sb.Append(Regex.Escape(kv.Key).Replace("\"", "\\\""));
                sb.Append("\":");
                sb.Append(kv.Value ? "true" : "false");
            }
            sb.Append('}');
            File.WriteAllText(path, sb.ToString());
        }
        catch (Exception ex)
        {
            Log.LogWarning($"SaveSelection failed: {ex.Message}");
        }
    }

    // Loads selection.json into the EnabledClips dictionary.
    // returns: void
    internal static void LoadSelection()
    {
        EnabledClips.Clear();
        string path = Path.Combine(SoundsFolder, "selection.json");
        if (!File.Exists(path)) return;
        try
        {
            string raw = File.ReadAllText(path);
            MatchCollection matches = MyRegex.Matches(raw);
            foreach (Match m in matches)
            {
                string key = Regex.Unescape(m.Groups["k"].Value);
                bool value = m.Groups["v"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                EnabledClips[key] = value;
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"LoadSelection failed: {ex.Message}");
        }
    }

    // Applies the configured spatial settings to a Bing Bong AudioSource at intercept time.
    // source (AudioSource): The AudioSource that was intercepted.
    // returns: void
    internal static void ApplySpatialSettings(AudioSource source)
    {
        source.spatialBlend = 0f;
    }

    // Registers an AudioSource that should respond to global stop actions.
    // source (AudioSource): AudioSource to track for stop controls.
    // returns: void
    internal static void RegisterManagedSource(AudioSource source)
    {
        if (source == null || ManagedSources.Contains(source)) return;
        ManagedSources.Add(source);
    }

    // Sets the active subtitle using subtitle JSON override if one exists for the clip.
    // clip (AudioClip): Clip that was selected for playback.
    // returns: void
    internal static void OnClipPlayed(AudioClip clip)
    {
        DebugLastPlayed = clip.name;
        Patches.SubtitleTextOverridePatches.BeginDiscoveryWindow(2.0f);

        bool hasSaved = SubtitleOverrides.TryGetValue(clip.name, out string savedSubtitle)
            && !string.IsNullOrWhiteSpace(savedSubtitle);

        if (TimedSubtitlesEnabled.Value
            && TimedSubtitleOverrides.TryGetValue(clip.name, out List<TimedSubtitleLine> timedLines)
            && timedLines != null && timedLines.Count > 0)
        {
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

        if (!hasSaved)
        {
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
            return;
        }

        ActiveSubtitle = savedSubtitle;
        ActiveSubtitleUntil = Time.unscaledTime + Mathf.Max(clip.length, 0.8f);
        WriteNativeSubtitleForClip(clip, ActiveSubtitle);
    }

    // Pushes the given text into PEAK's LocalizedText.mainTable for the subtitle id assigned to this clip.
    // clip (AudioClip): Clip whose assigned subtitle id receives the text.
    // text (string): Subtitle text to display in the native UI.
    // returns: void
    private static void WriteNativeSubtitleForClip(AudioClip clip, string text)
    {
        if (!UseNativeBingBongAPI.Value || clip == null) return;
        string id = NativeBingBongBridge.GetAssignedSubtitleId(clip);
        if (!string.IsNullOrEmpty(id))
            NativeBingBongBridge.WriteSubtitle(id, text ?? string.Empty);
    }

    // Scans the sounds folder, loads each wav/ogg file, and populates CustomClips with subtitle data.
    // returns: IEnumerator
    private IEnumerator LoadCustomClips()
    {
        string[] extensions = ["*.wav", "*.ogg"];
        List<string> files = [];
        foreach (string ext in extensions)
            files.AddRange(Directory.GetFiles(SoundsFolder, ext, SearchOption.TopDirectoryOnly));

        if (files.Count == 0)
        {
            Log.LogInfo("No custom audio files found - Bing Bong keeps his default SFX.");
            ClipsReady = true;
            yield break;
        }

        Log.LogInfo($"Loading {files.Count} audio file(s)...");
        foreach (string filePath in files)
        {
            if (Path.GetFileNameWithoutExtension(filePath).Equals("example", StringComparison.OrdinalIgnoreCase))
                continue;
            AudioType audioType = Path.GetExtension(filePath).Equals(".ogg", StringComparison.OrdinalIgnoreCase)
                ? AudioType.OGGVORBIS : AudioType.WAV;
            string uri = "file:///" + filePath.Replace('\\', '/');
            using UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Log.LogWarning($"Failed to load '{Path.GetFileName(filePath)}': {request.error}");
                continue;
            }
            AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
            clip.name = Path.GetFileNameWithoutExtension(filePath);
            CustomClips.Add(clip);
            string subtitle = TryReadSubtitleOverride(filePath);
            if (!string.IsNullOrWhiteSpace(subtitle))
            {
                SubtitleOverrides[clip.name] = subtitle;
                Log.LogInfo($"  Subtitle override: {clip.name}");
            }
            List<TimedSubtitleLine> timed = TryReadTimedSubtitleOverrides(filePath);
            if (timed.Count > 0)
            {
                TimedSubtitleOverrides[clip.name] = timed;
                Log.LogInfo($"  Timed subtitles: {clip.name} ({timed.Count})");
            }
            Log.LogInfo($"  Loaded: {clip.name} ({clip.length:F2}s)");
        }

        LoadSelection();
        foreach (AudioClip c in CustomClips)
        {
            if (!EnabledClips.ContainsKey(c.name))
                EnabledClips[c.name] = true;
        }
        SaveSelection();
        ClipsReady = true;

        if (CustomClips.Count > 0)
        {
            Log.LogInfo($"Override active: {CustomClips.Count} clip(s) ready.");
            Log.LogInfo($"Loaded {SubtitleOverrides.Count} subtitle override(s).");
            if (UseNativeBingBongAPI.Value)
            {
                int rewritten = NativeBingBongBridge.RewriteAllInScene();
                if (rewritten > 0)
                    Log.LogInfo($"Rewrote {rewritten} live Action_AskBingBong instance(s) with custom responses.");
            }
            if (!BingBongNetworkSync.IsConnectedAsClient)
            {
                bool wasHosting = BingBongNetworkSync.IsHosting;
                BingBongNetworkSync.StartServer();
                if (!wasHosting)
                    BingBongNetworkSync.BroadcastRefresh();
            }
        }
        else
        {
            Log.LogWarning("No clips loaded successfully - Bing Bong keeps his default SFX.");
        }
    }

    // Creates starter examples in the sounds folder and writes a short README.
    // returns: void
    private static void EnsureExampleFiles()
    {
        string readmePath = Path.Combine(SoundsFolder, "README.txt");
        if (!File.Exists(readmePath))
        {
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
