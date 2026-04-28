using System;
using System.IO;
using System.Text.Json;
using BepInEx.Logging;

namespace BingBongVoiceOverride.Handlers;

internal sealed class PluginJsonConfig {
    public AudioConfig Audio { get; set; } = new();
    public PlaybackConfig Playback { get; set; } = new();
    public SubtitleConfig Subtitles { get; set; } = new();

    internal sealed class AudioConfig {
        public float VolumeMultiplier { get; set; } = 0.1f;
        public bool ShortRangeOnly { get; set; } = true;
        public float ShortRangeMaxDistance { get; set; } = 25f;
    }

    internal sealed class PlaybackConfig {
        public bool AutoPlayEnabled { get; set; } = false;
        public float AutoPlayIntervalSeconds { get; set; } = 30f;
        public bool MusicMode { get; set; } = false;
    }

    internal sealed class SubtitleConfig {
        public bool TimedSubtitlesEnabled { get; set; } = false;
        public bool ShowSubtitleOverlay { get; set; } = false;
    }
}

internal static class JsonConfigHandler {
    private static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    internal static PluginJsonConfig Load(string path, ManualLogSource log) {
                 {
            if (!File.Exists(path)) {
                PluginJsonConfig created = new();
                Save(path, created, log);
                return created;
            }

            string json = File.ReadAllText(path);
            PluginJsonConfig? config = JsonSerializer.Deserialize<PluginJsonConfig>(json, JsonOptions);
            if (config != null) {
                return config;
            }
        }
        catch (Exception ex) {
            log.LogWarning($"[Config] Could not read JSON config: {ex.Message}");
        }

        PluginJsonConfig fallback = new();
        Save(path, fallback, log);
        return fallback;
    }

    internal static void Save(string path, PluginJsonConfig config, ManualLogSource log) {
                 {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex) {
            log.LogWarning($"[Config] Could not write JSON config: {ex.Message}");
        }
    }

    internal static void ApplyToRuntime(PluginJsonConfig config) {
        if (Plugin.VolumeMultiplier != null) {
            Plugin.VolumeMultiplier.Value = config.Audio.VolumeMultiplier;
        }

        if (Plugin.ShortRangeOnly != null) {
            Plugin.ShortRangeOnly.Value = config.Audio.ShortRangeOnly;
        }

        if (Plugin.ShortRangeMaxDistance != null) {
            Plugin.ShortRangeMaxDistance.Value = config.Audio.ShortRangeMaxDistance;
        }

        if (Plugin.AutoPlayEnabled != null) {
            Plugin.AutoPlayEnabled.Value = config.Playback.AutoPlayEnabled;
        }

        if (Plugin.AutoPlayIntervalSeconds != null) {
            Plugin.AutoPlayIntervalSeconds.Value = config.Playback.AutoPlayIntervalSeconds;
        }

        if (Plugin.MusicMode != null) {
            Plugin.MusicMode.Value = config.Playback.MusicMode;
        }

        if (Plugin.TimedSubtitlesEnabled != null) {
            Plugin.TimedSubtitlesEnabled.Value = config.Subtitles.TimedSubtitlesEnabled;
        }

        if (Plugin.ShowSubtitleOverlay != null) {
            Plugin.ShowSubtitleOverlay.Value = config.Subtitles.ShowSubtitleOverlay;
        }
    }
}
