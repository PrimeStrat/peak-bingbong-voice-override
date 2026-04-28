using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BingBongVoiceOverride.Handlers;
using BingBongVoiceOverride.Patches;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class Plugin : BaseUnityPlugin {
    internal static Plugin Instance;
    internal static ManualLogSource Log;
    internal static ConfigEntry<bool> EnableMod;
    internal static ConfigEntry<float> VolumeMultiplier;
    internal static ConfigEntry<KeyCode> MenuToggleKey;
    internal static ConfigEntry<bool> UseNativeBingBongAPI;

    internal static readonly List<AudioClip> CustomClips = [];
    internal static readonly Dictionary<string, string> SubtitleOverrides = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, bool> EnabledClips = new(StringComparer.OrdinalIgnoreCase);

    internal static string ForcedNextClipName = string.Empty;
    internal static AudioSource PluginAudioSource;
    internal static bool ClipsReady = false;
    internal static bool IsHoldingBingBong = false;

    internal static bool IsBingBongAudioActive => PluginAudioSource != null && PluginAudioSource.isPlaying;

    internal static bool MenuVisible = false;

    public static string SoundsFolder { get; private set; }

    private Harmony harmony;
    private bool refreshPending = false;

    private void Awake() {
        Instance = this;
        Log = Logger;

        EnableMod = Config.Bind("General", "Enabled", true, "Master toggle for the mod.");
        VolumeMultiplier = Config.Bind("Audio", "VolumeMultiplier", 1f,
            "Volume scale applied to custom clips (0.0 = silent, 1.0 = original, 2.0 = double).");
        UseNativeBingBongAPI = Config.Bind("Subtitles", "UseNativeBingBongAPI", true,
            "When true, route subtitles through PEAK's native Bing Bong system.");
        MenuToggleKey = Config.Bind("Menu", "MenuToggleKey", KeyCode.F6, "Toggle the mod menu on/off.");

        if (!EnableMod.Value) {
            Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} is disabled via config.");
            return;
        }

        SoundsFolder = Path.Combine(Paths.PluginPath, "PrimeStrat-BingBongVoiceOverride", "sounds");
        Directory.CreateDirectory(SoundsFolder);
        EnsureExampleFiles();

        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll();

        if (UseNativeBingBongAPI.Value) {
            NativeBingBongHandler.TryResolve();
        }

        PluginAudioSource = gameObject.AddComponent<AudioSource>();
        PluginAudioSource.playOnAwake = false;
        PluginAudioSource.spatialBlend = 0f;

        gameObject.AddComponent<Menu>();

        StartCoroutine(LoadCustomClips());

        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded.");
        Log.LogInfo($"Sounds folder: {SoundsFolder}");
    }

    private void Update() {
        if (!EnableMod.Value || refreshPending) return;
    }

    private void OnDestroy() {
        SetMenuVisible(false);
        StopAllPlayback();
        harmony?.UnpatchSelf();
    }

    private void OnApplicationQuit() {
        StopAllPlayback();
    }

    private IEnumerator LoadCustomClips() {
        CustomClips.Clear();
        EnabledClips.Clear();
        ClipsReady = false;

        yield return null;

        string[] audioFiles = GetAudioFilesInFolder();
        foreach (string filePath in audioFiles) {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            AudioClip clip = null;
            string loadError = null;

            if (ext == ".ogg") {
                clip = LoadOggClip(filePath, out loadError);
            }
            else if (ext == ".wav") {
                clip = LoadWavClip(filePath, out loadError);
            }

            if (clip == null) {
                if (loadError != null) {
                    Log.LogWarning($"Failed to load '{fileName}': {loadError}");
                }
                continue;
            }

            clip.name = fileName;
            CustomClips.Add(clip);
            EnabledClips[fileName] = true;

            string subtitleOverride = ReadSubtitleOverride(filePath);
            if (!string.IsNullOrEmpty(subtitleOverride)) {
                SubtitleOverrides[fileName] = subtitleOverride;
            }

            Log.LogInfo($"Loaded audio: '{fileName}' ({clip.length:F2}s)");
        }

        ClipsReady = true;
        Log.LogInfo($"Audio loading complete: {CustomClips.Count} clip(s) loaded.");

        if (UseNativeBingBongAPI.Value && CustomClips.Count > 0) {
            int rewritten = NativeBingBongHandler.RewriteAllInScene();
            Log.LogInfo($"Rewrote {rewritten} Bing Bong instances with custom clips.");
        }
    }

    internal static void StopAllPlayback() {
        if (PluginAudioSource != null) {
            PluginAudioSource.Stop();
            PluginAudioSource.clip = null;
        }
    }

    internal static List<AudioClip> GetActiveClips() {
        List<AudioClip> active = new();
        foreach (AudioClip clip in CustomClips) {
            if (clip != null) {
                string name = clip.name;
                if (!EnabledClips.ContainsKey(name) || EnabledClips[name]) {
                    active.Add(clip);
                }
            }
        }
        return active;
    }

    internal static void PlayDetachedFromBingBong(AudioClip clip, AudioSource source) {
        if (clip == null || PluginAudioSource == null) return;

        PluginAudioSource.clip = clip;
        PluginAudioSource.volume = VolumeMultiplier.Value;
        PluginAudioSource.Play();

        if (UseNativeBingBongAPI.Value) {
            NativeBingBongHandler.RewriteAllInScene();
        }
    }

    internal static void SetMenuVisible(bool visible) {
        if (MenuVisible == visible) return;
        MenuVisible = visible;

        if (visible) {
            CaptureMenuState();
        }
        else {
            RestoreMenuState();
        }
    }

    private string ReadSubtitleOverride(string audioPath) {
        string jsonPath = Path.ChangeExtension(audioPath, ".json");
        if (!File.Exists(jsonPath)) return string.Empty;

        try {
            string raw = File.ReadAllText(jsonPath);
            var parsed = JsonSerializer.Deserialize<JsonElement>(raw, JsonOptions);
            if (parsed.TryGetProperty("subtitle", out JsonElement subtitleElement)) {
                return subtitleElement.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch (Exception ex) {
            Log.LogWarning($"Subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
        }

        return string.Empty;
    }

    private static string[] GetAudioFilesInFolder() {
        if (!Directory.Exists(SoundsFolder)) return [];

        try {
            string[] ogg = Directory.GetFiles(SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly);
            string[] wav = Directory.GetFiles(SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly);
            List<string> combined = new(ogg.Length + wav.Length);
            combined.AddRange(ogg);
            combined.AddRange(wav);
            return combined.ToArray();
        }
        catch (Exception ex) {
            Log.LogWarning($"Failed to enumerate audio files: {ex.Message}");
            return [];
        }
    }

    private AudioClip LoadOggClip(string filePath, out string error) {
        error = null;
        try {
            using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read)) {
                byte[] data = new byte[fs.Length];
                fs.Read(data, 0, (int)fs.Length);

                string fileName = Path.GetFileNameWithoutExtension(filePath);
                AudioClip clip = AudioClip.Create(fileName, data.Length, 1, 44100, false);

                float[] samples = new float[data.Length];
                for (int i = 0; i < data.Length; i++) {
                    samples[i] = (data[i] - 128) / 256f;
                }

                clip.SetData(samples, 0);
                return clip;
            }
        }
        catch (Exception ex) {
            error = $"OGG load error: {ex.Message}";
            return null;
        }
    }

    private AudioClip LoadWavClip(string filePath, out string error) {
        error = null;
        try {
            using (FileStream fs = new(filePath, FileMode.Open, FileAccess.Read)) {
                byte[] wav = new byte[fs.Length];
                fs.Read(wav, 0, (int)fs.Length);

                int sampleRate = BitConverter.ToInt32(wav, 24);
                int channels = BitConverter.ToInt16(wav, 22);
                int samples = (wav.Length - 44) / (2 * channels);

                string fileName = Path.GetFileNameWithoutExtension(filePath);
                AudioClip clip = AudioClip.Create(fileName, samples, channels, sampleRate, false);

                float[] audioData = new float[samples * channels];
                int index = 44;
                for (int i = 0; i < audioData.Length; i++) {
                    short sample = BitConverter.ToInt16(wav, index);
                    audioData[i] = sample / 32768f;
                    index += 2;
                }

                clip.SetData(audioData, 0);
                return clip;
            }
        }
        catch (Exception ex) {
            error = $"WAV load error: {ex.Message}";
            return null;
        }
    }

    private static void CaptureMenuState() {
        // Could capture cursor/input state here if needed
    }

    private static void RestoreMenuState() {
        // Could restore cursor/input state here if needed
    }

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private void EnsureExampleFiles() {
        string readmePath = Path.Combine(SoundsFolder, "README.txt");
        if (!File.Exists(readmePath)) {
            try {
                File.WriteAllText(readmePath, @"Bing Bong Voice Override - Sounds Folder

Drop .ogg or .wav audio files here to replace Bing Bong's sounds.

Subtitle Support:
- Create a .json file with the same name as your audio file to add subtitles
- Example: my_sound.ogg -> my_sound.json
- JSON format: { ""subtitle"": ""Your subtitle text here"" }

Each client must have the same audio files installed locally.
If a file is missing, Bing Bong will play the original sound.
");
            }
            catch (Exception ex) {
                Log.LogWarning($"Could not write README to sounds folder: {ex.Message}");
            }
        }
    }
}
