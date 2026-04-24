using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BingBongVoiceOverride.Patches;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
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
    internal static ConfigEntry<bool> ShowSubtitleOverlay = null!;
    internal static ConfigEntry<bool> UseNativeBingBongAPI = null!;
    internal static ConfigEntry<bool> UseNativeSubtitleWithCustomAudio = null!;
    internal static ConfigEntry<bool> AllowClientImports = null!;
    internal static readonly List<AudioClip> CustomClips = [];
    internal static readonly Dictionary<string, string> SubtitleOverrides =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, List<TimedSubtitleLine>> TimedSubtitleOverrides =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Dictionary<string, bool> EnabledClips =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly HashSet<string> PendingSyncClipNames =
        new(StringComparer.OrdinalIgnoreCase);
    internal static readonly List<AudioSource> ManagedSources = [];

    // When set, the next override pick uses this clip name once instead of random selection.
    internal static string ForcedNextClipName = string.Empty;

    // Suppresses outgoing playback broadcasts for the duration of a remote-triggered action
    // to prevent re-broadcast loops when applying a received signal.
    internal static bool SuppressPlaybackBroadcast = false;

    // AudioSource owned by the plugin GameObject, used for music mode and auto-play.
    internal static AudioSource PluginAudioSource = null!;

    // Transform that the plugin AudioSource follows in world space while playing detached Bing Bong audio. Null disables following.
    internal static UnityEngine.Transform? FollowTransform = null;

    // Name of the last custom clip that played, or "none".
    internal static string DebugLastPlayed = "none";

    // True once the initial or refreshed clip load coroutine has completed.
    internal static bool ClipsReady = false;

    // Set by a game-specific Harmony patch when the local player picks up or drops Bing Bong.
    internal static bool IsHoldingBingBong = false;

    // True when any tracked Bing Bong / plugin AudioSource is currently playing. Fallback hold signal when no game-side patch is wired.
    internal static bool IsBingBongAudioActive
    {
        get
        {
            for (int i = 0; i < ManagedSources.Count; i++)
            {
                AudioSource s = ManagedSources[i];
                if (s != null && s.isPlaying)
                    return true;
            }
            return PluginAudioSource != null && PluginAudioSource.isPlaying;
        }
    }

    // True when subtitle override behavior should run: holding Bing Bong, or any tracked override source is actively playing.
    internal static bool ShouldRunSubtitleOverride
    {
        get { return IsHoldingBingBong || IsBingBongAudioActive; }
    }

    // Current subtitle text chosen for the most recently played override clip.
    internal static string ActiveSubtitle = string.Empty;

    // Time.unscaledTime timestamp when the current subtitle should stop displaying.
    internal static float ActiveSubtitleUntil = 0f;
    internal static string ActiveTimedSubtitleClipName = string.Empty;
    internal static float ActiveTimedSubtitleStart = 0f;
    internal static float ActiveTimedSubtitleLastTick = 0f;

    // Saved single-line subtitle pushed to the native Bing Bong UI while a timed sing-along track plays.
    internal static string ActiveNativeSubtitle = string.Empty;

    // The AudioClip driving the current timed subtitle track. Null when no timed subtitle is active.
    internal static AudioClip? ActiveTimedSubtitleClip = null;

    // True when a timed sing-along track is currently driving subtitles for the held Bing Bong clip.
    internal static bool IsTimedSubtitleActive
    {
        get { return TimedSubtitlesEnabled != null && TimedSubtitlesEnabled.Value && !string.IsNullOrEmpty(ActiveTimedSubtitleClipName); }
    }

    // True when the importer menu window is visible.
    internal static bool MenuVisible = false;

    // True while a sound refresh or network sync operation is in progress.
    internal static bool IsRefreshPending => Instance?._refreshPending ?? false;

    // Importer status text displayed in the menu window.
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

    // BepInEx entry point; binds config, applies patches, mounts the debug overlay, and starts audio loading. returns: void
    private void Awake()
    {
        Instance = this;
        Log = Logger;

        EnableMod = Config.Bind("General", "Enabled", true,
            "Master toggle for the mod.");
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
        MaxSyncFileSizeKb = Config.Bind("Network", "MaxSyncFileSizeKb", 5120,
            "Skip syncing/serving any sound file larger than this in kilobytes. Keeps multiplayer transfer fast.");
        TimedSubtitlesEnabled = Config.Bind("Subtitles", "TimedSubtitlesEnabled", false,
            "When true, clips with a timedSubtitles track display sing-along lines while holding Bing Bong. When false, only the single subtitle line is used.");
        FetchTimedSubtitlesOnImport = Config.Bind("Subtitles", "FetchTimedSubtitlesOnImport", true,
            "When true, the importer also runs yt-dlp to fetch a timed caption track for the clip. When false, only a placeholder single-line subtitle JSON is written.");
        ShowSubtitleOverlay = Config.Bind("Subtitles", "ShowSubtitleOverlay", false,
            "When true, the mod draws its own subtitle overlay near the bottom of the screen while holding Bing Bong (recommended; the game UI does not always honor overrides).");
        UseNativeBingBongAPI = Config.Bind("Subtitles", "UseNativeBingBongAPI", true,
            "When true, route playback and subtitles through PEAK's native Bing Bong response system (rewriting Action_AskBingBong.responses and writing into LocalizedText.mainTable, mirroring the BingBongVoiceLineAPI technique). When false, fall back to the legacy AudioSource hijack and text-setter Harmony patches.");
        UseNativeSubtitleWithCustomAudio = Config.Bind("Subtitles", "UseNativeSubtitleWithCustomAudio", false,
            "When true, custom clip subtitles are also pushed into PEAK's native subtitle table. When false, native subtitle text is suppressed for custom audio.");
        AllowClientImports = Config.Bind("Network", "AllowClientImports", false,
            "When true, any connected client can upload new sound files through the mod's HTTP sync server.");
        MenuToggleKey = Config.Bind("Menu", "MenuToggleKey", KeyCode.F6,
            "Toggle the unified mod menu on/off.");
        ForceEnableRefresh = Config.Bind("Menu", "ForceEnableRefresh", true,
            "When true, the refresh button and key work without needing to hold Bing Bong.");

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

    // Handles per-frame plugin logic for subtitle ticks and game-state tracking. returns: void
    private void Update()
    {
        if (!EnableMod.Value || _refreshPending) return;

        UpdateFollowTransform();
        UpdateTimedSubtitleState();
        if (UseNativeBingBongAPI.Value)
            TickNativeTimedSubtitle();
        SubtitleTextOverridePatches.TickForceActiveSubtitle();
        TickMirrorOnDrop();
    }

    // Unpatches Harmony and stops the sound sync server when the plugin unloads. returns: void
    private void OnDestroy()
    {
        SetMenuVisible(false);
        StopAllPlayback();
        _harmony?.UnpatchSelf();
        BingBongNetworkSync.StopServer();
    }

    // Ensures every plugin-managed audio source is silenced when the application is quitting. returns: void
    private void OnApplicationQuit()
    {
        StopAllPlayback();
        BingBongNetworkSync.StopServer();
    }

    // Stops the plugin's audio source and every managed/intercepted Bing Bong source so playback ceases cleanly on quit. returns: void
    private static void StopAllPlayback()
    {
        try
        {
            if (PluginAudioSource != null)
            {
                PluginAudioSource.Stop();
                PluginAudioSource.clip = null;
            }
        }
        catch (Exception)
        {
        }

        for (int i = 0; i < ManagedSources.Count; i++)
        {
            try
            {
                AudioSource s = ManagedSources[i];
                if (s != null)
                {
                    s.Stop();
                    s.clip = null;
                }
            }
            catch (Exception)
            {
            }
        }
        ManagedSources.Clear();
        FollowTransform = null;
        ActiveSubtitle = string.Empty;
        ActiveSubtitleUntil = 0f;
        ActiveNativeSubtitle = string.Empty;
        ActiveTimedSubtitleClip = null;
    }

    // Sets menu visibility, applies cursor controls, and toggles native Escape-style game pause.
    // visible (bool): True to open menu; false to close and restore prior input state.
    // returns: void
    internal static void SetMenuVisible(bool visible)
    {
        if (MenuVisible == visible)
            return;

        MenuVisible = visible;

        if (visible)
        {
            if (!_menuStateCaptured)
            {
                _savedCursorLock = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;
                _menuStateCaptured = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            SyncEscapePauseForMenu(true);
            return;
        }

        if (_menuStateCaptured)
        {
            Cursor.lockState = _savedCursorLock;
            Cursor.visible = _savedCursorVisible;
            _menuStateCaptured = false;
        }

        SyncEscapePauseForMenu(false);
    }

    // Mirrors menu open/close to the game's native pause toggle by simulating Escape key presses.
    // menuOpen (bool): True when opening menu, false when closing.
    // returns: void
    private static void SyncEscapePauseForMenu(bool menuOpen)
    {
        if (menuOpen)
        {
            if (_menuPauseInjected)
                return;
            PulseEscapeKey();
            _menuPauseInjected = true;
            return;
        }

        if (!_menuPauseInjected)
            return;

        PulseEscapeKey();
        _menuPauseInjected = false;
    }

    // Sends an Escape key down/up pulse to trigger the same pause behavior as pressing Escape manually. returns: void
    private static void PulseEscapeKey()
    {
        try
        {
            keybd_event(VK_ESCAPE, 0, 0, System.UIntPtr.Zero);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, System.UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to send Escape key pulse: {ex.Message}");
        }
    }

    // Called by the game session start hook; loads sounds when SessionLoadOnly is enabled. returns: void
    internal static void OnSessionStart()
    {
        if (!SessionLoadOnly.Value || Instance == null) return;
        Instance.StartRefresh();
        Log.LogInfo("Session started - loading sounds.");
    }

    // Starts a sound refresh cycle if one is not already running. returns: void
    internal void StartRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        BingBongNetworkSync.AllowResync();
        StartCoroutine(RefreshSoundsCoroutine());
    }

    // Clears and reloads all custom clips, waits for network sync to complete, then releases the pending flag. returns: IEnumerator
    private IEnumerator RefreshSoundsCoroutine()
    {
        ClipsReady = false;
        CustomClips.Clear();
        SubtitleOverrides.Clear();
        TimedSubtitleOverrides.Clear();
        DebugLastPlayed = "none";
        ActiveSubtitle = string.Empty;
        ActiveSubtitleUntil = 0f;
        ActiveTimedSubtitleClipName = string.Empty;
        ActiveTimedSubtitleClip = null;
        ActiveTimedSubtitleStart = 0f;
        ActiveTimedSubtitleLastTick = 0f;

        // If a client, wait for the host download to finish before loading clips so newly synced files are included.
        if (BingBongNetworkSync.IsConnectedAsClient)
        {
            float elapsed = 0f;
            while (BingBongNetworkSync.IsSyncBusy && elapsed < 60f)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        Log.LogInfo("Refreshing sounds...");
        yield return StartCoroutine(LoadCustomClips());

        // If hosting with players in the lobby, wait up to 30 s for all clients to confirm they have the latest files.
        if (BingBongNetworkSync.IsHosting)
        {
            float elapsed = 0f;
            while (BingBongNetworkSync.IsSyncBusy && elapsed < 30f)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        _refreshPending = false;
    }

    // Queues a URL import and triggers refresh after a successful download.
    // url (string): Direct audio URL to download into the sounds folder.
    // returns: void
    internal void StartImportFromUrl(string url)
    {
        StartCoroutine(ImportFromUrlCoroutine(url));
    }

    // Sets the active subtitle using subtitle JSON override if one exists for the clip.
    // clip (AudioClip): Clip that was selected for playback.
    // returns: void
    internal static void OnClipPlayed(AudioClip clip)
    {
        DebugLastPlayed = clip.name;

        Patches.SubtitleTextOverridePatches.BeginDiscoveryWindow(2.0f);

        string savedSubtitle;
        bool hasSaved = SubtitleOverrides.TryGetValue(clip.name, out savedSubtitle) && !string.IsNullOrWhiteSpace(savedSubtitle);

        List<TimedSubtitleLine> timedLines;
        if (TimedSubtitlesEnabled.Value
            && TimedSubtitleOverrides.TryGetValue(clip.name, out timedLines)
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
        if (string.IsNullOrEmpty(id)) return;
        NativeBingBongBridge.WriteSubtitle(id, text ?? string.Empty);
    }

    // Tracks the most recent Bing Bong AudioSource playing one of our clips for the mirror-on-drop feature.
    private static AudioSource? _mirrorBbSource = null;
    private static AudioClip? _mirrorClip = null;
    private static float _mirrorStart = 0f;
    private static bool _wasHoldingBb = false;

    // Watches for a Bing Bong drop while a clip is mid-playback and continues it on the plugin's detached AudioSource. returns: void
    private static void TickMirrorOnDrop()
    {
        if (!UseNativeBingBongAPI.Value)
        {
            _wasHoldingBb = IsHoldingBingBong;
            return;
        }
        if (PluginAudioSource == null) return;

        AudioSource? bb = FindActiveBingBongSourcePlayingOurClip();
        if (bb != null)
        {
            if (_mirrorBbSource != bb || _mirrorClip != bb.clip)
            {
                _mirrorBbSource = bb;
                _mirrorClip = bb.clip;
                _mirrorStart = Time.unscaledTime;
            }
        }

        if (_wasHoldingBb && !IsHoldingBingBong && _mirrorClip != null && !PluginAudioSource.isPlaying)
        {
            float elapsed = Time.unscaledTime - _mirrorStart;
            if (elapsed >= 0f && elapsed < _mirrorClip.length - 0.1f)
            {
                UnityEngine.Transform? followTarget = _mirrorBbSource != null ? Patches.BingBongHelper.FindBingBongRoot(_mirrorBbSource) : null;
                FollowTransform = followTarget ?? FindBingBongTransform();
                if (FollowTransform != null)
                    PluginAudioSource.transform.position = FollowTransform.position;
                PluginAudioSource.spatialBlend = 0f;
                PluginAudioSource.volume = Mathf.Clamp(VolumeMultiplier.Value, 0f, 4f);
                PluginAudioSource.clip = _mirrorClip;
                PluginAudioSource.time = elapsed;
                PluginAudioSource.Play();
                RegisterManagedSource(PluginAudioSource);
                Log.LogInfo($"[Mirror] BB dropped mid-line; continuing '{_mirrorClip.name}' at {elapsed:F2}s on plugin source.");
            }
            _mirrorClip = null;
            _mirrorBbSource = null;
        }

        _wasHoldingBb = IsHoldingBingBong;
    }

    // Finds the first Bing Bong AudioSource playing one of our loaded clips. Seeds the mirror-on-drop tracker.
    // returns: AudioSource?
    private static AudioSource? FindActiveBingBongSourcePlayingOurClip()
    {
        AudioSource[] all = UnityEngine.Object.FindObjectsOfType<AudioSource>();
        for (int i = 0; i < all.Length; i++)
        {
            AudioSource s = all[i];
            if (s == null || !s.isPlaying || s.clip == null) continue;
            if (Patches.BingBongHelper.IsPluginOwnedSource(s)) continue;
            if (!Patches.BingBongHelper.IsBingBongSource(s)) continue;
            for (int j = 0; j < CustomClips.Count; j++)
            {
                if (CustomClips[j] == s.clip) return s;
            }
        }
        return null;
    }

    // Applies the configured spatial settings to a Bing Bong AudioSource at intercept time.
    // source (AudioSource): The AudioSource that was intercepted.
    // returns: void
    internal static void ApplySpatialSettings(AudioSource source)
    {
        source.spatialBlend = 0f;
    }

    // Returns the list of currently enabled clips, falling back to all loaded clips when none are enabled.
    // returns: List<AudioClip>
    internal static List<AudioClip> GetActiveClips()
    {
        List<AudioClip> active = [];
        for (int i = 0; i < CustomClips.Count; i++)
        {
            AudioClip clip = CustomClips[i];
            if (PendingSyncClipNames.Contains(clip.name))
                continue;
            bool enabled;
            if (!EnabledClips.TryGetValue(clip.name, out enabled) || enabled)
                active.Add(clip);
        }
        return active;
    }

    // Persists the EnabledClips dictionary to selection.json in the sounds folder. returns: void
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

    // Loads selection.json into the EnabledClips dictionary. returns: void
    internal static void LoadSelection()
    {
        EnabledClips.Clear();
        string path = Path.Combine(SoundsFolder, "selection.json");
        if (!File.Exists(path)) return;
        try
        {
            string raw = File.ReadAllText(path);
            MatchCollection matches = MyRegex().Matches(raw);
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

    // Plays a clip through the plugin's own AudioSource and fires subtitle hooks.
    // clip (AudioClip): Clip to play.
    // returns: void
    internal static void PlayThroughPluginSource(AudioClip clip)
    {
        if (clip == null || PluginAudioSource == null) return;
        RegisterManagedSource(PluginAudioSource);
        if (ShortRangeOnly.Value)
        {
            UnityEngine.Transform? t = FindBingBongTransform();
            if (t != null)
                PluginAudioSource.transform.position = t.position;
            ApplySpatialSettings(PluginAudioSource);
        }
        else
        {
            PluginAudioSource.spatialBlend = 0f;
        }
        PluginAudioSource.volume = Mathf.Clamp(VolumeMultiplier.Value, 0f, 4f);
        PluginAudioSource.clip = clip;
        PluginAudioSource.Play();
        OnClipPlayed(clip);
        if (!SuppressPlaybackBroadcast)
            BingBongNetworkSync.BroadcastPlay(clip.name);
    }

    // Plays a clip through the plugin's own AudioSource at the position of the given source.
    // clip (AudioClip): Clip to play.
    // originSource (AudioSource): The intercepted Bing Bong AudioSource whose transform is followed during playback.
    // returns: void
    internal static void PlayDetachedFromBingBong(AudioClip clip, AudioSource originSource)
    {
        if (clip == null || PluginAudioSource == null) return;
        RegisterManagedSource(PluginAudioSource);

        UnityEngine.Transform? followTarget = Patches.BingBongHelper.FindBingBongRoot(originSource);
        if (followTarget == null)
            followTarget = FindBingBongTransform();

        FollowTransform = followTarget;

        if (followTarget != null)
            PluginAudioSource.transform.position = followTarget.position;
        PluginAudioSource.spatialBlend = 0f;

        PluginAudioSource.volume = Mathf.Clamp(VolumeMultiplier.Value, 0f, 4f);
        PluginAudioSource.clip = clip;
        PluginAudioSource.Play();
        OnClipPlayed(clip);
    }

    // Per-frame update that applies manual 2D distance attenuation based on the listener's distance to the Bing Bong transform. returns: void
    private static void UpdateFollowTransform()
    {
        if (PluginAudioSource == null) return;
        if (!PluginAudioSource.isPlaying)
        {
            FollowTransform = null;
            return;
        }

        if (FollowTransform == null)
            FollowTransform = FindBingBongTransform();

        float baseVolume = Mathf.Clamp(VolumeMultiplier.Value, 0f, 4f);

        if (!ShortRangeOnly.Value || FollowTransform == null)
        {
            PluginAudioSource.volume = baseVolume;
            return;
        }

        PluginAudioSource.transform.position = FollowTransform.position;

        UnityEngine.Transform? listener = FindListenerTransform();
        if (listener == null)
        {
            PluginAudioSource.volume = baseVolume;
            return;
        }

        float maxDist = Mathf.Max(1f, ShortRangeMaxDistance.Value);
        float dist = Vector3.Distance(listener.position, FollowTransform.position);
        float attenuation = Mathf.Clamp01(1f - (dist / maxDist));
        PluginAudioSource.volume = baseVolume * attenuation;
    }

    // Finds the active audio listener transform (main camera or AudioListener component) for distance computations.
    // returns: Transform?
    private static UnityEngine.Transform? FindListenerTransform()
    {
        AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
        if (listener != null) return listener.transform;
        Camera cam = Camera.main;
        if (cam != null) return cam.transform;
        return null;
    }

    // Registers an AudioSource that should respond to global stop actions.
    // source (AudioSource): AudioSource to track for stop controls.
    // returns: void
    internal static void RegisterManagedSource(AudioSource source)
    {
        if (source == null)
            return;
        if (ManagedSources.Contains(source))
            return;
        ManagedSources.Add(source);
    }

    // Stops all tracked override playback sources, including plugin preview/music and intercepted Bing Bong sources. returns: void
    internal static void StopAllManagedAudio()
    {
        for (int i = ManagedSources.Count - 1; i >= 0; i--)
        {
            AudioSource src = ManagedSources[i];
            if (src == null)
            {
                ManagedSources.RemoveAt(i);
                continue;
            }
            try
            {
                src.Stop();
            }
            catch (Exception) { }
        }

        if (PluginAudioSource != null)
        {
            try
            {
                PluginAudioSource.Stop();
            }
            catch (Exception) { }
        }

        ClearTimedSubtitles();
        if (!SuppressPlaybackBroadcast)
            BingBongNetworkSync.BroadcastStop();
    }

    // Pauses the plugin AudioSource without resetting playback position. returns: void
    internal static void PausePlayback()
    {
        if (PluginAudioSource != null && PluginAudioSource.isPlaying)
        {
            PluginAudioSource.Pause();
            if (!SuppressPlaybackBroadcast)
                BingBongNetworkSync.BroadcastPause();
        }
    }

    // Resumes a paused plugin AudioSource from its saved position. returns: void
    internal static void UnpausePlayback()
    {
        if (PluginAudioSource != null && !PluginAudioSource.isPlaying && PluginAudioSource.clip != null)
        {
            PluginAudioSource.UnPause();
            if (!SuppressPlaybackBroadcast)
                BingBongNetworkSync.BroadcastResume();
        }
    }

    // Clears all active timed and single subtitle state. Safe to call from any context. returns: void
    internal static void ClearTimedSubtitles()
    {
        ActiveSubtitle = string.Empty;
        ActiveSubtitleUntil = 0f;
        ActiveTimedSubtitleClipName = string.Empty;
        ActiveTimedSubtitleClip = null;
        ActiveTimedSubtitleStart = 0f;
        ActiveTimedSubtitleLastTick = 0f;
        ActiveNativeSubtitle = string.Empty;
    }

    // Applies a remote play command received via Photon signal without re-broadcasting.
    // clipName (string): name of the clip to look up and play
    // returns: void
    internal static void ApplyRemotePlay(string clipName)
    {
        if (string.IsNullOrEmpty(clipName) || !ClipsReady) return;
        AudioClip? clip = CustomClips.Find(c => c.name.Equals(clipName, StringComparison.OrdinalIgnoreCase));
        if (clip == null) return;
        SuppressPlaybackBroadcast = true;
        try { PlayThroughPluginSource(clip); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    // Applies a remote pause command received via Photon signal without re-broadcasting. returns: void
    internal static void ApplyRemotePause()
    {
        SuppressPlaybackBroadcast = true;
        try { PausePlayback(); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    // Applies a remote resume command received via Photon signal without re-broadcasting. returns: void
    internal static void ApplyRemoteResume()
    {
        SuppressPlaybackBroadcast = true;
        try { UnpausePlayback(); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    // Applies a remote stop command received via Photon signal without re-broadcasting. returns: void
    internal static void ApplyRemoteStop()
    {
        SuppressPlaybackBroadcast = true;
        try { StopAllManagedAudio(); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    // Applies a remote force-next command received via Photon signal.
    // clipName (string): clip name to set as the forced next pick
    // returns: void
    internal static void ApplyRemoteForceNext(string clipName)
    {
        ForcedNextClipName = clipName ?? string.Empty;
    }

    // Coroutine that periodically plays a random enabled clip when AutoPlay is on and MusicMode is off.
    // returns: IEnumerator
    private IEnumerator AutoPlayLoop()
    {
        while (true)
        {
            float wait = Mathf.Max(1f, AutoPlayIntervalSeconds.Value);
            yield return new WaitForSeconds(wait);
            if (!EnableMod.Value) continue;
            if (MusicMode.Value) continue;
            if (!AutoPlayEnabled.Value) continue;
            if (!ClipsReady) continue;
            List<AudioClip> active = GetActiveClips();
            if (active.Count == 0) continue;
            AudioClip clip = active[UnityEngine.Random.Range(0, active.Count)];
            PlayThroughPluginSource(clip);
            Log.LogInfo($"[AutoPlay] {clip.name}");
        }
    }

    // Coroutine that plays clips back to back when MusicMode is enabled.
    // returns: IEnumerator
    private IEnumerator MusicModeLoop()
    {
        int index = 0;
        while (true)
        {
            if (!EnableMod.Value || !MusicMode.Value || !ClipsReady)
            {
                yield return new WaitForSeconds(0.5f);
                continue;
            }
            List<AudioClip> active = GetActiveClips();
            if (active.Count == 0)
            {
                yield return new WaitForSeconds(1f);
                continue;
            }
            if (index >= active.Count) index = 0;
            AudioClip clip = active[index++];
            PlayThroughPluginSource(clip);
            Log.LogInfo($"[Music] {clip.name}");
            float wait = Mathf.Max(0.25f, clip.length + 0.25f);
            float elapsed = 0f;
            while (elapsed < wait)
            {
                if (!MusicMode.Value) break;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }
    }

    // Scans the sounds folder, loads each wav/ogg file, and populates CustomClips with subtitle data. returns: IEnumerator
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
                ? AudioType.OGGVORBIS
                : AudioType.WAV;

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
            BingBongNetworkSync.StartServer();
            BingBongNetworkSync.BroadcastRefresh();
        }
        else
        {
            Log.LogWarning("No clips loaded successfully - Bing Bong keeps his default SFX.");
        }
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
            string escaped = match.Groups["v"].Value;
            return Regex.Unescape(escaped).Trim();
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
            return string.Empty;
        }
    }

    // Creates starter examples in the sounds folder and writes a short README. returns: void
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
        {
            string exampleJson = "{\n  \"subtitle\": \"Bing Bong says hello from JSON\"\n}\n";
            File.WriteAllText(exampleJsonPath, exampleJson);
        }

        string exampleOggPath = Path.Combine(SoundsFolder, "example.ogg");
        if (!File.Exists(exampleOggPath))
        {
            byte[] placeholder = Encoding.ASCII.GetBytes("OggS_example_placeholder_replace_with_real_audio");
            File.WriteAllBytes(exampleOggPath, placeholder);
        }
    }

    // Downloads an audio URL into the sounds folder and refreshes clips.
    // url (string): Audio URL entered from the importer menu.
    // returns: IEnumerator
    private IEnumerator ImportFromUrlCoroutine(string url)
    {
        EnsureExampleFiles();

        if (string.IsNullOrWhiteSpace(url))
        {
            ImportStatus = "error: URL is empty";
            yield break;
        }

        string trimmed = url.Trim();

        if (IsYtDlpUrl(trimmed))
        {
            yield return StartCoroutine(ImportFromYtDlpCoroutine(trimmed));
            yield break;
        }

        ImportStatus = "downloading...";
        using UnityWebRequest request = UnityWebRequest.Get(trimmed);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            ImportStatus = $"error: {request.error}";
            yield break;
        }

        byte[] data = request.downloadHandler.data;
        if (data == null || data.Length == 0)
        {
            ImportStatus = "error: no file data returned";
            yield break;
        }

        Uri uri;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri))
        {
            ImportStatus = "error: invalid URL";
            yield break;
        }

        string normalizedExt = ResolveAudioExtension(request, uri, data);
        if (string.IsNullOrWhiteSpace(normalizedExt))
        {
            ImportStatus = "error: URL did not return recognizable OGG/WAV audio";
            yield break;
        }

        string baseName = ResolveBaseNameFromLink(request, uri);
        string safeBase = NormalizeFileName(baseName);
        string finalName = $"{safeBase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{normalizedExt}";
        string finalPath = Path.Combine(SoundsFolder, finalName);

        File.WriteAllBytes(finalPath, data);

        string subtitlePath = Path.ChangeExtension(finalPath, ".json");
        if (!File.Exists(subtitlePath))
            File.WriteAllText(subtitlePath, "{\n  \"subtitle\": \"Bing Bong imported line\"\n}\n");

        yield return StartCoroutine(FinalizeImportedFile(finalName));
    }

    // Downloads audio from a YouTube or Twitch URL using yt-dlp, converts to mono OGG, and loads the clip.
    // url (string): YouTube or Twitch URL to extract audio from.
    // returns: IEnumerator
    private IEnumerator ImportFromYtDlpCoroutine(string url)
    {
        string ytDlp = FindYtDlp();
        if (string.IsNullOrEmpty(ytDlp))
        {
            ImportStatus = "error: yt-dlp not found. Install from https://github.com/yt-dlp/yt-dlp and add to PATH";
            yield break;
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string outTemplate = Path.Combine(SoundsFolder, $"yt_{timestamp}.%(ext)s");

        ProcessStartInfo psi = new()
        {
            FileName = ytDlp,
            Arguments = $"-x --no-playlist -N 4 --audio-format vorbis --audio-quality 8"
                + $" --print \"%(title)s\""
                + $" --print \"after_move:%(filepath)s\""
                + $" --postprocessor-args \"ffmpeg:-ac 1 -ar 32000\""
                + $" -o \"{outTemplate}\""
                + $" \"{url}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            ImportStatus = $"error: could not start yt-dlp: {ex.Message}";
            yield break;
        }

        float elapsed = 0f;
        while (!proc.HasExited)
        {
            elapsed += UnityEngine.Time.unscaledDeltaTime;
            ImportStatus = $"yt-dlp running... ({elapsed:F0}s)";
            yield return null;
        }

        int exitCode = proc.ExitCode;
        string stdout = proc.StandardOutput.ReadToEnd().Trim();
        string stderr = proc.StandardError.ReadToEnd().Trim();
        proc.Dispose();

        if (exitCode != 0)
        {
            string msg = stderr.Length > 120 ? stderr.Substring(stderr.Length - 120) : stderr;
            ImportStatus = $"error: yt-dlp exited {exitCode}: {msg}";
            yield break;
        }

        string detectedTitle = string.Empty;
        string outputPath = string.Empty;
        string[] outLines = stdout.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < outLines.Length; i++)
        {
            string line = outLines[i].Trim();
            if (line.StartsWith("after_move:", StringComparison.OrdinalIgnoreCase))
            {
                outputPath = line.Substring("after_move:".Length).Trim();
                continue;
            }
            if (string.IsNullOrWhiteSpace(detectedTitle))
                detectedTitle = line;
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            string[] candidates = Directory.GetFiles(SoundsFolder, $"yt_{timestamp}.*", SearchOption.TopDirectoryOnly);
            if (candidates.Length > 0)
                outputPath = candidates[0];
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            ImportStatus = "error: yt-dlp finished but no output file found in sounds folder";
            yield break;
        }

        string ext = Path.GetExtension(outputPath);
        if (string.IsNullOrWhiteSpace(ext))
            ext = ".ogg";
        string safeBase = NormalizeFileName(detectedTitle);
        if (string.IsNullOrWhiteSpace(safeBase))
            safeBase = $"yt_{timestamp}";
        string finalName = $"{safeBase}_{timestamp}{ext}";
        string finalPath = Path.Combine(SoundsFolder, finalName);
        if (!outputPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(outputPath, finalPath);
        }

        string subtitlePath = Path.ChangeExtension(finalPath, ".json");
        if (!File.Exists(subtitlePath))
            File.WriteAllText(subtitlePath, "{\n  \"subtitle\": \"Bing Bong imported line\"\n}\n");

        yield return StartCoroutine(FinalizeImportedFile(Path.GetFileName(finalPath)));
        if (FetchTimedSubtitlesOnImport.Value)
            StartCoroutine(ImportSubtitleFromYtDlpCoroutine(url, ytDlp, timestamp, finalPath));
        else
            ImportStatus = $"saved (no timed subs): {Path.GetFileName(finalPath)}";
    }

    // Runs subtitle extraction in a separate yt-dlp request after audio import completes.
    // url (string): Original media URL.
    // ytDlp (string): Resolved yt-dlp executable path.
    // timestamp (string): Import timestamp prefix used by the audio import.
    // finalAudioPath (string): Final saved audio file path for writing companion subtitle JSON.
    // returns: IEnumerator
    private IEnumerator ImportSubtitleFromYtDlpCoroutine(string url, string ytDlp, string timestamp, string finalAudioPath)
    {
        float now = Time.realtimeSinceStartup;
        if (_nextSubtitleImportAllowedAt > now)
            yield return new WaitForSecondsRealtime(_nextSubtitleImportAllowedAt - now);

        string outputTemplate = Path.Combine(SoundsFolder, $"yt_{timestamp}.%(ext)s");
        string stderr = string.Empty;
        bool fetched = false;

        yield return StartCoroutine(RunSubtitleImportPass(ytDlp, url, outputTemplate, autoSubs: false, onDone: (ok, err) =>
        {
            fetched = ok;
            stderr = err;
        }));

        if (!fetched)
        {
            yield return StartCoroutine(RunSubtitleImportPass(ytDlp, url, outputTemplate, autoSubs: true, onDone: (ok, err) =>
            {
                fetched = ok;
                stderr = err;
            }));
        }

        _nextSubtitleImportAllowedAt = Time.realtimeSinceStartup + SubtitleImportCooldownSeconds;

        if (!fetched)
        {
            if (!string.IsNullOrWhiteSpace(stderr))
                Log.LogInfo($"Subtitle import skipped: {stderr}");
            yield break;
        }

        string subtitlePath = Path.ChangeExtension(finalAudioPath, ".json");
        bool wroteTimed = TryWriteTimedSubtitleJsonFromVtt(timestamp, finalAudioPath, subtitlePath);
        if (!wroteTimed)
            yield break;

        string clipName = Path.GetFileNameWithoutExtension(finalAudioPath);
        string subtitle = TryReadSubtitleOverride(finalAudioPath);
        if (!string.IsNullOrWhiteSpace(subtitle))
            SubtitleOverrides[clipName] = subtitle;

        List<TimedSubtitleLine> timed = TryReadTimedSubtitleOverrides(finalAudioPath);
        if (timed.Count > 0)
            TimedSubtitleOverrides[clipName] = timed;

        ImportStatus = $"subtitle ready: {Path.GetFileName(finalAudioPath)}";
    }

    // Executes one yt-dlp subtitle pass and reports whether it produced any VTT files for the timestamp.
    // ytDlp (string): Resolved yt-dlp executable path.
    // url (string): Original media URL.
    // outputTemplate (string): yt-dlp output template path.
    // autoSubs (bool): True to request auto captions, false for regular subtitle tracks.
    // onDone (Action<bool, string>): Callback receiving (success, stderr).
    // returns: IEnumerator
    private IEnumerator RunSubtitleImportPass(string ytDlp, string url, string outputTemplate, bool autoSubs, Action<bool, string> onDone)
    {
        string passFlag = autoSubs ? "--write-auto-subs" : "--write-subs";
        ProcessStartInfo psi = new()
        {
            FileName = ytDlp,
            Arguments = "--skip-download"
                + $" {passFlag}"
                + " --sub-langs \"en\""
                + " --sub-format \"vtt/best\""
                + " --convert-subs vtt"
                + " --sleep-requests 0.75"
                + " --retries 5"
                + " --fragment-retries 5"
                + $" -o \"{outputTemplate}\""
                + $" \"{url}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process proc;
        try
        {
            proc = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Subtitle import start failed: {ex.Message}");
            onDone(false, ex.Message);
            yield break;
        }

        while (!proc.HasExited)
            yield return new WaitForSecondsRealtime(0.2f);

        int exitCode = proc.ExitCode;
        string stderr = proc.StandardError.ReadToEnd().Trim();
        proc.Dispose();

        bool foundAny = false;
        string vttBase = Path.GetFileNameWithoutExtension(outputTemplate);
        string[] vtts = Directory.GetFiles(SoundsFolder, vttBase + "*.vtt", SearchOption.TopDirectoryOnly);
        if (vtts.Length > 0)
            foundAny = true;

        onDone(exitCode == 0 && foundAny, stderr);
    }

    // Marks a newly imported file for network sync and refreshes once all known clients have downloaded it.
    // fileName (string): Imported audio file name (including extension).
    // returns: IEnumerator
    private IEnumerator FinalizeImportedFile(string fileName)
    {
        string clipName = Path.GetFileNameWithoutExtension(fileName);
        PendingSyncClipNames.Add(clipName);
        BingBongNetworkSync.RegisterImportedFile(fileName);
        // Notify clients immediately so the wait loop below has something to wait on.
        // Without this, clients are never signaled until after the 60s timeout fires.
        BingBongNetworkSync.AllowResync();

        if (!BingBongNetworkSync.IsHosting && !string.IsNullOrEmpty(BingBongNetworkSync.ActiveHostAddress)
            && AllowClientImports.Value)
        {
            string jsonName = Path.ChangeExtension(fileName, ".json");
            yield return StartCoroutine(BingBongNetworkSync.UploadFileToHost(fileName, BingBongNetworkSync.ActiveHostAddress));
            if (File.Exists(Path.Combine(SoundsFolder, jsonName)))
                yield return StartCoroutine(BingBongNetworkSync.UploadFileToHost(jsonName, BingBongNetworkSync.ActiveHostAddress));
        }

        float syncElapsed = 0f;
        while (!BingBongNetworkSync.IsImportedFileSynced(fileName) && syncElapsed < 60f)
        {
            int remaining = BingBongNetworkSync.GetPendingClientCount(fileName);
            ImportStatus = $"syncing to clients... {remaining} remaining";
            yield return new WaitForSecondsRealtime(0.25f);
            syncElapsed += 0.25f;
        }

        PendingSyncClipNames.Remove(clipName);
        ImportStatus = $"saved: {fileName}";
        StartRefresh();
    }

    // Saves or clears the subtitle override JSON for a specific clip and updates in-memory mappings.
    // clipName (string): Clip name without extension.
    // subtitle (string): Subtitle text to persist. Empty clears the override.
    // returns: void
    internal static void SaveSubtitleOverrideForClip(string clipName, string subtitle)
    {
        if (string.IsNullOrWhiteSpace(clipName))
            return;

        string jsonPath = Path.Combine(SoundsFolder, clipName + ".json");
        string value = subtitle == null ? string.Empty : subtitle.Trim();

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
                if (File.Exists(jsonPath))
                    File.Delete(jsonPath);
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
        if (string.IsNullOrWhiteSpace(clipName))
            return;

        TimedSubtitleOverrides.Remove(clipName);
        if (ActiveTimedSubtitleClipName.Equals(clipName, StringComparison.OrdinalIgnoreCase))
        {
            ActiveTimedSubtitleClipName = string.Empty;
            ActiveTimedSubtitleStart = 0f;
            ActiveTimedSubtitleLastTick = 0f;
            ActiveSubtitle = string.Empty;
            ActiveSubtitleUntil = 0f;
        }

        string existing;
        if (!SubtitleOverrides.TryGetValue(clipName, out existing))
            existing = string.Empty;
        SaveSubtitleOverrideForClip(clipName, existing);
    }

    // Resolves the audio extension from payload signature, response headers, and URL as fallback.
    // request (UnityWebRequest): Completed UnityWebRequest with response headers.
    // uri (Uri): Parsed source URL.
    // data (byte[]): Downloaded bytes.
    // returns: string
    private string ResolveAudioExtension(UnityWebRequest request, Uri uri, byte[] data)
    {
        string signatureExt = DetectExtensionFromSignature(data);
        if (!string.IsNullOrWhiteSpace(signatureExt))
            return signatureExt;

        string contentType = request.GetResponseHeader("Content-Type") ?? string.Empty;
        if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase))
            return ".ogg";
        if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase))
            return ".wav";
        if (contentType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            string urlExt = Path.GetExtension(uri.AbsolutePath);
            if (urlExt.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                return ".wav";
            if (urlExt.Equals(".ogg", StringComparison.OrdinalIgnoreCase))
                return ".ogg";
            return ".ogg";
        }

        return string.Empty;
    }

    // Detects container type directly from downloaded bytes.
    // data (byte[]): Downloaded file bytes.
    // returns: string
    private static string DetectExtensionFromSignature(byte[] data)
    {
        if (data.Length >= 4 && data[0] == (byte)'O' && data[1] == (byte)'g' && data[2] == (byte)'g' && data[3] == (byte)'S')
            return ".ogg";

        if (data.Length >= 12 && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
            data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E')
            return ".wav";

        return string.Empty;
    }

    // Builds a best-effort base name using response filename metadata, query params, then URL path.
    // request (UnityWebRequest): Completed UnityWebRequest with response headers.
    // uri (Uri): Parsed source URL.
    // returns: string
    private string ResolveBaseNameFromLink(UnityWebRequest request, Uri uri)
    {
        string fromHeader = TryGetFileNameFromContentDisposition(request.GetResponseHeader("Content-Disposition"));
        if (!string.IsNullOrWhiteSpace(fromHeader))
            return Path.GetFileNameWithoutExtension(fromHeader);

        string fromQuery = TryGetQueryValue(uri.Query, "title");
        if (string.IsNullOrWhiteSpace(fromQuery))
            fromQuery = TryGetQueryValue(uri.Query, "filename");
        if (string.IsNullOrWhiteSpace(fromQuery))
            fromQuery = TryGetQueryValue(uri.Query, "name");
        if (!string.IsNullOrWhiteSpace(fromQuery))
            return fromQuery;

        string pathName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        if (!string.IsNullOrWhiteSpace(pathName))
            return Uri.UnescapeDataString(pathName);

        return "imported_sound";
    }

    // Normalizes a raw title into a stable filename-safe base.
    // name (string): Raw title/name string.
    // returns: string
    private static string NormalizeFileName(string name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "imported_sound" : name.Trim();
        value = Regex.Replace(value, "\\s+", "_");
        value = Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
        value = Regex.Replace(value, "_+", "_");
        value = value.Trim('_');
        if (string.IsNullOrWhiteSpace(value))
            value = "imported_sound";
        if (value.Length > 80)
            value = value.Substring(0, 80).Trim('_');
        return value.ToLowerInvariant();
    }

    // Extracts a filename from Content-Disposition when present.
    // header (string): Raw Content-Disposition header value.
    // returns: string
    private static string TryGetFileNameFromContentDisposition(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return string.Empty;

        Match utf8Match = Regex.Match(header, "filename\\*=UTF-8''(?<v>[^;]+)", RegexOptions.IgnoreCase);
        if (utf8Match.Success)
            return Uri.UnescapeDataString(utf8Match.Groups["v"].Value.Trim('"'));

        Match plainMatch = Regex.Match(header, "filename=(?<v>[^;]+)", RegexOptions.IgnoreCase);
        if (plainMatch.Success)
            return plainMatch.Groups["v"].Value.Trim().Trim('"');

        return string.Empty;
    }

    // Returns true when the URL is a YouTube or Twitch link that requires yt-dlp.
    // url (string): URL to inspect.
    // returns: bool
    private static bool IsYtDlpUrl(string url)
    {
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
            || url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)
            || url.Contains("clips.twitch.tv", StringComparison.OrdinalIgnoreCase);
    }

    // Returns the local player's Steam display name, falling back to the OS username.
    // returns: string
    internal static string GetLocalPlayerName()
    {
        // Prefer the Photon NickName since that is what _lobbyPlayerNames tracks on the host.
        try
        {
            string photonName = Patches.NetworkSyncPatches.GetLocalPhotonNickName();
            if (!string.IsNullOrWhiteSpace(photonName))
                return photonName;
        }
        catch (Exception) { }
        try
        {
            string[] steamAssemblies = ["com.rlabrecque.steamworks.net", "Steamworks.NET", "Assembly-CSharp"];
            foreach (string asm in steamAssemblies)
            {
                Type? t = Type.GetType($"Steamworks.SteamFriends, {asm}");
                if (t == null) continue;
                System.Reflection.MethodInfo? m = t.GetMethod("GetPersonaName",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m == null) continue;
                string? name = m.Invoke(null, null) as string;
                if (!string.IsNullOrWhiteSpace(name))
                    return name!;
            }
        }
        catch (Exception) { }
        return Environment.UserName;
    }

    // Finds the yt-dlp executable by checking PATH, the plugin folder, and common install locations.
    // returns: string
    private static string FindYtDlp()
    {
        string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
        string[] candidates =
        [
            Path.Combine(pluginDir, "yt-dlp.exe"),
            Path.Combine(pluginDir, "yt-dlp"),
            "yt-dlp",
            "yt-dlp.exe",
            Path.Combine(SoundsFolder, "..", "yt-dlp.exe"),
            Path.Combine(SoundsFolder, "..", "yt-dlp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "yt-dlp", "yt-dlp.exe"),
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                ProcessStartInfo probe = new()
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using Process? p = Process.Start(probe);
                if (p != null)
                {
                    p.WaitForExit(3000);
                    if (p.ExitCode == 0)
                        return candidate;
                }
            }
            catch (Exception) { }
        }

        return string.Empty;
    }

    // Updates ActiveSubtitle each frame using the active timed subtitle track, if any. returns: void
    private static void UpdateTimedSubtitleState()
    {
        if (string.IsNullOrWhiteSpace(ActiveTimedSubtitleClipName))
            return;

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
        if (ActiveTimedSubtitleLastTick <= 0f)
            ActiveTimedSubtitleLastTick = now;

        List<TimedSubtitleLine> lines;
        if (!TimedSubtitleOverrides.TryGetValue(ActiveTimedSubtitleClipName, out lines) || lines == null || lines.Count == 0)
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

        float elapsed;
        if (PluginAudioSource != null && PluginAudioSource.isPlaying && PluginAudioSource.clip != null)
            elapsed = PluginAudioSource.time;
        else
            elapsed = now - ActiveTimedSubtitleStart;

        TimedSubtitleLine? current = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            TimedSubtitleLine line = lines[i];
            if (elapsed >= line.Start && elapsed <= line.End)
            {
                current = line;
                break;
            }
        }

        if (current != null)
        {
            ActiveSubtitle = current.Text;
            ActiveSubtitleUntil = Time.unscaledTime + Mathf.Max(0.05f, current.End - elapsed);
            ActiveTimedSubtitleLastTick = now;
            return;
        }

        ActiveSubtitle = ActiveNativeSubtitle ?? string.Empty;
        if (!string.IsNullOrEmpty(ActiveSubtitle))
            ActiveSubtitleUntil = Time.unscaledTime + 0.5f;
        else
            ActiveSubtitleUntil = 0f;
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

    // Continuously writes the active timed subtitle line into LocalizedText.MAIN_TABLE so the native UI stays current. returns: void
    private static void TickNativeTimedSubtitle()
    {
        if (!IsTimedSubtitleActive) return;
        if (ActiveTimedSubtitleClip == null) return;
        string id = NativeBingBongBridge.GetAssignedSubtitleId(ActiveTimedSubtitleClip);
        if (string.IsNullOrEmpty(id)) return;
        NativeBingBongBridge.WriteSubtitle(id, ActiveSubtitle ?? string.Empty);
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
            Match keyMatch = Regex.Match(raw, "\"(?:timedSubtitles|timed_subtitles)\"\\s*:", RegexOptions.IgnoreCase);
            if (!keyMatch.Success)
                return lines;

            MatchCollection entries = Regex.Matches(
                raw,
                "\\{\\s*\"start\"\\s*:\\s*(?<start>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"end\"\\s*:\\s*(?<end>-?[0-9]+(?:\\.[0-9]+)?)\\s*,\\s*\"text\"\\s*:\\s*\"(?<text>(?:\\\\.|[^\"])*)\"\\s*\\}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match entry in entries)
            {
                float start;
                float end;
                if (!float.TryParse(entry.Groups["start"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out start))
                    continue;
                if (!float.TryParse(entry.Groups["end"].Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out end))
                    continue;

                string text = Regex.Unescape(entry.Groups["text"].Value).Trim();
                if (string.IsNullOrWhiteSpace(text))
                    continue;
                if (end <= start)
                    continue;

                lines.Add(new TimedSubtitleLine { Start = start, End = end, Text = text });
            }

            lines.Sort((a, b) => a.Start.CompareTo(b.Start));
            return lines;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Timed subtitle JSON parse failed for '{Path.GetFileName(jsonPath)}': {ex.Message}");
            return lines;
        }
    }

    // Builds a timed subtitle JSON companion from downloaded VTT files, if present.
    // timestamp (string): Import timestamp prefix used for temporary output names.
    // finalAudioPath (string): Final saved audio path.
    // subtitleJsonPath (string): JSON output path to write.
    // returns: bool
    private bool TryWriteTimedSubtitleJsonFromVtt(string timestamp, string finalAudioPath, string subtitleJsonPath)
    {
        string[] vtts = Directory.GetFiles(SoundsFolder, $"yt_{timestamp}*.vtt", SearchOption.TopDirectoryOnly);
        if (vtts.Length == 0)
            return false;

        string selectedVtt = vtts[0];
        bool selectedIsAuto = LooksLikeAutoSubFile(selectedVtt);
        for (int i = 1; i < vtts.Length; i++)
        {
            bool candidateAuto = LooksLikeAutoSubFile(vtts[i]);
            if (selectedIsAuto && !candidateAuto)
            {
                selectedVtt = vtts[i];
                selectedIsAuto = false;
            }
        }
        List<TimedSubtitleLine> best = ParseVttTimedSubtitles(selectedVtt);
        if (selectedIsAuto)
            best = DedupeRollingCues(best);

        for (int i = 0; i < vtts.Length; i++)
        {
            try { File.Delete(vtts[i]); } catch (Exception) { }
        }

        if (best.Count == 0)
            return false;

        string first = best[0].Text;
        string escapedFirst = EscapeJson(first);
        StringBuilder sb = new();
        sb.Append("{\n");
        sb.Append("  \"subtitle\": \"").Append(escapedFirst).Append("\",\n");
        sb.Append("  \"timedSubtitles\": [\n");
        for (int i = 0; i < best.Count; i++)
        {
            TimedSubtitleLine line = best[i];
            sb.Append("    { \"start\": ")
                .Append(line.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"end\": ")
                .Append(line.End.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"text\": \"")
                .Append(EscapeJson(line.Text))
                .Append("\" }");
            if (i < best.Count - 1) sb.Append(',');
            sb.Append("\n");
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
            if (!line.Contains("-->"))
            {
                i++;
                continue;
            }

            string[] parts = line.Split(["-->"], StringSplitOptions.None);
            if (parts.Length != 2)
            {
                i++;
                continue;
            }

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
            string head = string.Empty;
            using (StreamReader r = new(vttPath))
            {
                char[] buf = new char[1024];
                int n = r.Read(buf, 0, buf.Length);
                head = new string(buf, 0, n);
            }
            if (head.Contains("Kind: captions", StringComparison.OrdinalIgnoreCase))
                return true;
            if (head.Contains("<c>", StringComparison.OrdinalIgnoreCase))
                return true;
            string name = Path.GetFileName(vttPath);
            return name.Contains(".auto", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Collapses YouTube auto-caption rolling cues where each line is a prefix-extension of the next, leaving only completed phrases.
    // lines (List<TimedSubtitleLine>): Raw cue list from the parser.
    // returns: List<TimedSubtitleLine>
    private static List<TimedSubtitleLine> DedupeRollingCues(List<TimedSubtitleLine> lines)
    {
        if (lines == null || lines.Count == 0)
            return lines ?? [];

        List<TimedSubtitleLine> result = [];
        for (int i = 0; i < lines.Count; i++)
        {
            TimedSubtitleLine current = lines[i];
            string normalized = NormalizeCueText(current.Text);
            if (string.IsNullOrEmpty(normalized))
                continue;

            bool isPrefixOfNext = false;
            if (i + 1 < lines.Count)
            {
                string next = NormalizeCueText(lines[i + 1].Text);
                if (next.Length > normalized.Length
                    && next.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                    && (lines[i + 1].Start - current.Start) < 1.5f)
                {
                    isPrefixOfNext = true;
                }
            }
            if (isPrefixOfNext)
                continue;

            if (result.Count > 0)
            {
                TimedSubtitleLine prev = result[result.Count - 1];
                string prevNorm = NormalizeCueText(prev.Text);
                if (prevNorm.Equals(normalized, StringComparison.OrdinalIgnoreCase))
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
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    // Parses a VTT timestamp into seconds.
    // value (string): Raw VTT time segment.
    // returns: float
    private static float ParseVttTime(string value)
    {
        string t = value.Trim();
        int space = t.IndexOf(' ');
        if (space >= 0)
            t = t.Substring(0, space).Trim();

        string[] parts = t.Split(':');
        if (parts.Length < 2 || parts.Length > 3)
            return -1f;

        float h = 0f;
        float m = 0f;
        float s = 0f;
        if (parts.Length == 3)
        {
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out h))
                return -1f;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out m))
                return -1f;
            if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out s))
                return -1f;
        }
        else
        {
            if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out m))
                return -1f;
            if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out s))
                return -1f;
        }

        return h * 3600f + m * 60f + s;
    }

    // Escapes a string for JSON output.
    // value (string): Input string to escape.
    // returns: string
    private static string EscapeJson(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    // Finds the best candidate transform for Bing Bong so local music/preview playback follows object location.
    // returns: Transform?
    private static UnityEngine.Transform? FindBingBongTransform()
    {
        string[] names = ["BingBong", "Bing Bong", "bing_bong", "bingbong"];
        for (int i = 0; i < names.Length; i++)
        {
            GameObject go = GameObject.Find(names[i]);
            if (go != null)
                return go.transform;
        }

        UnityEngine.Transform[] all = GameObject.FindObjectsOfType<UnityEngine.Transform>();
        for (int i = 0; i < all.Length; i++)
        {
            string lower = all[i].name.ToLowerInvariant();
            if (lower.Contains("bing") && lower.Contains("bong"))
                return all[i];
        }

        return null;
    }
    // Extracts a query parameter value from a URI query string.
    // query (string): URI query string beginning with '?' or empty.
    // key (string): Query key to locate.
    // returns: string
    private static string TryGetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        string[] pairs = trimmed.Split('&');
        foreach (string pair in pairs)
        {
            if (string.IsNullOrWhiteSpace(pair))
                continue;

            string[] parts = pair.Split(['='], 2);
            string pairKey = Uri.UnescapeDataString(parts[0]).Trim();
            if (!pairKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (parts.Length < 2)
                return string.Empty;

            string value = parts[1].Replace('+', ' ');
            return Uri.UnescapeDataString(value).Trim();
        }

        return string.Empty;
    }

    private static Regex MyRegex() =>
        new Regex("\"(?<k>(?:\\\\.|[^\"])+)\"\\s*:\\s*(?<v>true|false)");
}
