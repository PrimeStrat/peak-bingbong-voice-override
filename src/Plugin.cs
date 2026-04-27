using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BingBongVoiceOverride.Patches;
using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public partial class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance = null!;
    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<bool> EnableMod = null!;
    internal static ConfigEntry<float> VolumeMultiplier = null!;
    internal static ConfigEntry<KeyCode> MenuToggleKey = null!;
    internal static ConfigEntry<bool> SessionLoadOnly = null!;
    internal static ConfigEntry<bool> ForceEnableRefresh = null!;
    internal static ConfigEntry<bool> AutoPlayEnabled = null!;
    internal static ConfigEntry<float> AutoPlayIntervalSeconds = null!;
    internal static ConfigEntry<bool> MusicMode = null!;
    internal static ConfigEntry<bool> ShortRangeOnly = null!;
    internal static ConfigEntry<float> ShortRangeMaxDistance = null!;
    internal static ConfigEntry<int> MaxSyncFileSizeKb = null!;
    internal static ConfigEntry<bool> TimedSubtitlesEnabled = null!;
    internal static ConfigEntry<bool> FetchTimedSubtitlesOnImport = null!;
    internal static ConfigEntry<bool> AllowYtDlpAutoDownload = null!;
    internal static ConfigEntry<bool> ShowSubtitleOverlay = null!;
    internal static ConfigEntry<bool> UseNativeBingBongAPI = null!;
    internal static ConfigEntry<bool> UseNativeSubtitleWithCustomAudio = null!;
    internal static ConfigEntry<bool> AllowClientImports = null!;
    internal static ConfigEntry<bool> AllowClientPlayback = null!;
    internal static ConfigEntry<bool> AllowClientSubtitleEdit = null!;
    internal static ConfigEntry<bool> AllowClientSelectionEdit = null!;
    internal static ConfigEntry<bool> AllowClientSettingsChange = null!;
    internal static ConfigEntry<bool> AllowClientMenu = null!;

    internal static readonly List<AudioClip> CustomClips = [];
    internal static readonly Dictionary<string, string> SubtitleOverrides = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, List<TimedSubtitleLine>> TimedSubtitleOverrides = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, bool> EnabledClips = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> PendingSyncClipNames = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly List<AudioSource> ManagedSources = [];

    internal static string ForcedNextClipName = string.Empty;
    internal static bool SuppressPlaybackBroadcast = false;
    internal static AudioSource PluginAudioSource = null!;
    internal static UnityEngine.Transform? FollowTransform = null;
    internal static string DebugLastPlayed = "none";
    internal static bool ClipsReady = false;
    internal static bool IsHoldingBingBong = false;

    private static List<AudioClip> _cachedActiveClips = [];
    private static float _cachedActiveClipsExpiry = -1f;
    private static bool _pausedForSync = false;
    private static bool _wasSyncBusy = false;

    internal static bool IsBingBongAudioActive
    {
        get
        {
            for (int i = 0; i < ManagedSources.Count; i++)
            {
                if (ManagedSources[i] != null && ManagedSources[i].isPlaying) return true;
            }
            return PluginAudioSource != null && PluginAudioSource.isPlaying;
        }
    }

    internal static bool ShouldRunSubtitleOverride => IsHoldingBingBong || IsBingBongAudioActive;

    internal static string ActiveSubtitle = string.Empty;
    internal static float ActiveSubtitleUntil = 0f;
    internal static string ActiveTimedSubtitleClipName = string.Empty;
    internal static float ActiveTimedSubtitleStart = 0f;
    internal static float ActiveTimedSubtitleLastTick = 0f;
    internal static string ActiveNativeSubtitle = string.Empty;
    internal static AudioClip? ActiveTimedSubtitleClip = null;
    internal static bool IsTimedSubtitleActive =>
        TimedSubtitlesEnabled != null && TimedSubtitlesEnabled.Value && !string.IsNullOrEmpty(ActiveTimedSubtitleClipName);

    internal static bool MenuVisible = false;
    internal static bool IsRefreshPending => Instance?._refreshPending ?? false;
    internal static string ImportStatus = "idle";

    public static string SoundsFolder { get; private set; } = null!;

    private Harmony _harmony = null!;
    private bool _refreshPending = false;
    private static bool _menuStateCaptured = false;
    private static CursorLockMode _savedCursorLock = CursorLockMode.Locked;
    private static bool _savedCursorVisible = false;
    private static bool _menuPauseInjected = false;
    private static float _nextSubtitleImportAllowedAt = 0f;

    private const byte VK_ESCAPE = 0x1B;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const float SubtitleImportCooldownSeconds = 1.25f;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, System.UIntPtr dwExtraInfo);

    internal sealed class TimedSubtitleLine
    {
        internal float Start;
        internal float End;
        internal string Text = string.Empty;
    }

    // BepInEx entry point; binds config, applies patches, mounts the debug overlay, and starts audio loading.
    // returns: void
    private void Awake()
    {
        Instance = this;
        Log = Logger;

        EnableMod = Config.Bind("General", "Enabled", true, "Master toggle for the mod.");
        SessionLoadOnly = Config.Bind("General", "SessionLoadOnly", false,
            "When true, sounds load only when a game session starts via OnSessionStart(), not on plugin load.");
        VolumeMultiplier = Config.Bind("Audio", "VolumeMultiplier", 0.1f,
            "Volume scale applied to custom clips (0.0 = silent, 1.0 = original, 2.0 = double).");
        ShortRangeOnly = Config.Bind("Audio", "ShortRangeOnly", true,
            "When true, custom clips fall off with distance from Bing Bong. When false, they are heard everywhere.");
        ShortRangeMaxDistance = Config.Bind("Audio", "ShortRangeMaxDistance", 25f,
            "Maximum audible distance in meters when ShortRangeOnly is true.");
        AutoPlayEnabled = Config.Bind("Playback", "AutoPlayEnabled", false,
            "When true, randomly play an enabled clip every AutoPlayIntervalSeconds without needing the Bing Bong interaction.");
        AutoPlayIntervalSeconds = Config.Bind("Playback", "AutoPlayIntervalSeconds", 30f,
            "Seconds between auto-play triggers. Ignored when MusicMode is on.");
        MusicMode = Config.Bind("Playback", "MusicMode", false,
            "When true, plays through enabled clips back to back like a music player. Overrides AutoPlay.");
        MaxSyncFileSizeKb = Config.Bind("Network", "MaxSyncFileSizeKb", 0,
            "Maximum sync file size in kilobytes. Set to 0 for unlimited streaming sync.");
        TimedSubtitlesEnabled = Config.Bind("Subtitles", "TimedSubtitlesEnabled", false,
            "When true, clips with a timedSubtitles track display sing-along lines while holding Bing Bong.");
        FetchTimedSubtitlesOnImport = Config.Bind("Subtitles", "FetchTimedSubtitlesOnImport", false,
            "When true, the importer also runs yt-dlp to fetch a timed caption track for the clip.");
        AllowYtDlpAutoDownload = Config.Bind("Import", "AllowYtDlpAutoDownload", false,
            "When true, yt-dlp and ffmpeg are downloaded automatically if missing. When false, a manual install message is shown instead.");
        ShowSubtitleOverlay = Config.Bind("Subtitles", "ShowSubtitleOverlay", false,
            "When true, the mod draws its own subtitle overlay near the bottom of the screen while holding Bing Bong.");
        UseNativeBingBongAPI = Config.Bind("Subtitles", "UseNativeBingBongAPI", true,
            "When true, route playback and subtitles through PEAK's native Bing Bong response system.");
        UseNativeSubtitleWithCustomAudio = Config.Bind("Subtitles", "UseNativeSubtitleWithCustomAudio", false,
            "When true, custom clip subtitles are also pushed into PEAK's native subtitle table.");
        AllowClientImports = Config.Bind("Network", "AllowClientImports", false,
            "When true, any connected client can upload new sound files through the mod's HTTP sync server.");
        AllowClientPlayback = Config.Bind("Network", "AllowClientPlayback", false,
            "When true, non-host players can use playback controls from their menu.");
        AllowClientSubtitleEdit = Config.Bind("Network", "AllowClientSubtitleEdit", false,
            "When true, non-host players can save subtitle overrides that sync to all players.");
        AllowClientSelectionEdit = Config.Bind("Network", "AllowClientSelectionEdit", false,
            "When true, non-host players can toggle clip enabled states that sync to all players.");
        AllowClientSettingsChange = Config.Bind("Network", "AllowClientSettingsChange", false,
            "When true, non-host players can change playback settings via the menu.");
        AllowClientMenu = Config.Bind("Network", "AllowClientMenu", true,
            "When false, connected clients cannot open the mod menu at all.");
        MenuToggleKey = Config.Bind("Menu", "MenuToggleKey", KeyCode.F6, "Toggle the unified mod menu on/off.");
        ForceEnableRefresh = Config.Bind("Menu", "ForceEnableRefresh", true,
            "When true, the refresh button and key work without needing to hold Bing Bong.");

        AllowClientPlayback.SettingChanged += (_, _) => BingBongNetworkSync.BroadcastPermissions();
        AllowClientSubtitleEdit.SettingChanged += (_, _) => BingBongNetworkSync.BroadcastPermissions();
        AllowClientSelectionEdit.SettingChanged += (_, _) => BingBongNetworkSync.BroadcastPermissions();
        AllowClientSettingsChange.SettingChanged += (_, _) => BingBongNetworkSync.BroadcastPermissions();
        AllowClientMenu.SettingChanged += (_, _) => BingBongNetworkSync.BroadcastPermissions();

        if (!EnableMod.Value)
        {
            Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} is disabled via config.");
            return;
        }

        SoundsFolder = Path.Combine(Paths.PluginPath, "PrimeStrat-BingBongVoiceOverride", "sounds");
        Directory.CreateDirectory(SoundsFolder);
        EnsureExampleFiles();

        _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        _harmony.PatchAll();
        if (UseNativeBingBongAPI.Value)
            NativeBingBongBridge.TryResolve();
        SubtitleTextOverridePatches.Apply(_harmony);
        NetworkSyncPatches.TryApply(_harmony);

        PluginAudioSource = gameObject.AddComponent<AudioSource>();
        PluginAudioSource.playOnAwake = false;
        PluginAudioSource.spatialBlend = 0f;
        gameObject.AddComponent<UnifiedMenu>();

        StartCoroutine(AutoPlayLoop());
        StartCoroutine(MusicModeLoop());

        if (!SessionLoadOnly.Value)
            StartCoroutine(LoadCustomClips());

        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded.");
        Log.LogInfo($"Sounds folder: {SoundsFolder}");
    }

    // Handles per-frame plugin logic for subtitle ticks and game-state tracking.
    // returns: void
    private void Update()
    {
        if (!EnableMod.Value || _refreshPending) return;
        UpdateFollowTransform();
        UpdateTimedSubtitleState();
        if (UseNativeBingBongAPI.Value)
            TickNativeTimedSubtitle();
        SubtitleTextOverridePatches.TickForceActiveSubtitle();
        TickMirrorOnDrop();
        TickSyncPause();
    }

    // Unpatches Harmony and stops the sound sync server when the plugin unloads.
    // returns: void
    private void OnDestroy()
    {
        SetMenuVisible(false);
        StopAllPlayback();
        _harmony?.UnpatchSelf();
        BingBongNetworkSync.StopServer();
    }

    // Ensures every plugin-managed audio source is silenced when the application is quitting.
    // returns: void
    private void OnApplicationQuit()
    {
        StopAllPlayback();
        BingBongNetworkSync.StopServer();
    }
}
