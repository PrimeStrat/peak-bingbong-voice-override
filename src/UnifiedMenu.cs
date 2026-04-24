using System;
using System.Collections.Generic;
using UnityEngine;
namespace BingBongVoiceOverride;

// Single in-game IMGUI window with tabbed sections for status, sound selection, playback, network, and the URL importer.
internal class UnifiedMenu : MonoBehaviour
{
    private Rect _windowRect = new Rect(0f, 0f, 620f, 520f);
    private bool _windowRectInitialized = false;
    private Vector2 _soundsScroll = Vector2.zero;
    private string _importUrl = string.Empty;
    private int _activeTab = 0;
    private readonly string[] _tabLabels = ["Status", "Sounds", "Playback", "Network", "Importer"];
    private readonly Dictionary<string, string> _subtitleDrafts = new(StringComparer.OrdinalIgnoreCase);

    private Vector2 _playbackScroll = Vector2.zero;
    private int _playerQueueIndex = 0;
    private bool _playbackSettingsExpanded = false;
    private GUIStyle? _overlayStyle;
    private Font? _overlayStyleFont;
    private int _overlayStyleFontSize;
    private Font? _gameFont;

    private static readonly Color TimedFillColor = new Color(0.96f, 0.97f, 0.55f, 1f);
    private static readonly Color TimedStrokeColor = new Color(0.08f, 0.08f, 0.04f, 1f);
    private const float TimedStrokeWidth = 3f;
    private const int TimedFontSizeDefault = 28;

    // Polls the menu toggle key each frame. returns: void
    private void Update()
    {
        if (Input.GetKeyDown(Plugin.MenuToggleKey.Value))
            Plugin.SetMenuVisible(!Plugin.MenuVisible);
    }

    // Draws the menu window when visible. returns: void
    private void OnGUI()
    {
        DrawSubtitleOverlay();

        if (!Plugin.MenuVisible) return;
        if (!_windowRectInitialized)
        {
            _windowRect = new Rect(
                (Screen.width - 620f) * 0.5f,
                (Screen.height - 520f) * 0.35f,
                620f, 520f);
            _windowRectInitialized = true;
        }
        GUILayout.Window(9875, _windowRect, DrawWindow,
            $"BingBong Voice Override  v{MyPluginInfo.PLUGIN_VERSION}  [{Plugin.MenuToggleKey.Value} to close]");
    }

    // Draws timed subtitle fallback text with a hardcoded PEAK-like look. returns: void
    private void DrawSubtitleOverlay()
    {
        bool forceOverlay = Plugin.IsTimedSubtitleActive
            && Plugin.UseNativeBingBongAPI.Value
            && !Plugin.UseNativeSubtitleWithCustomAudio.Value;
        if (!Plugin.ShowSubtitleOverlay.Value && !forceOverlay) return;
        if (Plugin.MenuVisible) return;
        if (!Plugin.IsTimedSubtitleActive) return;
        if (string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle)) return;
        if (Time.unscaledTime > Plugin.ActiveSubtitleUntil) return;

        int fontSize = TimedFontSizeDefault;
        Font? font = ResolveTimedSubtitleFont();

        if (_overlayStyle == null
            || _overlayStyleFontSize != fontSize
            || _overlayStyleFont != font)
        {
            _overlayStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
                clipping = TextClipping.Clip,
                fontStyle = FontStyle.Bold,
                richText = false,
                stretchWidth = false,
                stretchHeight = false,
            };
            _overlayStyle.padding = new RectOffset(0, 0, 0, 0);
            _overlayStyle.border = new RectOffset(0, 0, 0, 0);
            _overlayStyle.overflow = new RectOffset(0, 0, 0, 0);
            _overlayStyle.contentOffset = Vector2.zero;
            _overlayStyle.normal.background = null;
            _overlayStyle.font = font ?? GUI.skin.font;
            _overlayStyleFont = font;
            _overlayStyleFontSize = fontSize;
        }

        Matrix4x4 savedMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;

        string line = Plugin.ActiveSubtitle;
        int nl = line.IndexOf('\n');
        if (nl >= 0) line = line.Substring(0, nl).TrimEnd('\r');

        float sw = Screen.width;
        float sh = Screen.height;
        float w = Mathf.Min(sw * 0.72f, 1100f);
        float h = 60f;
        float x = (sw - w) * 0.5f;
        float y = sh * 0.70f;
        Rect text = new Rect(x, y, w, h);

        _overlayStyle.normal.textColor = TimedStrokeColor;
        float o = TimedStrokeWidth;
        GUI.Label(new Rect(text.x - o, text.y, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x + o, text.y, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x, text.y - o, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x, text.y + o, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x - o, text.y - o, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x + o, text.y - o, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x - o, text.y + o, text.width, text.height), line, _overlayStyle);
        GUI.Label(new Rect(text.x + o, text.y + o, text.width, text.height), line, _overlayStyle);

        _overlayStyle.normal.textColor = TimedFillColor;
        GUI.Label(text, line, _overlayStyle);

        GUI.matrix = savedMatrix;
    }

    // Resolves a font for timed subtitles by scanning fonts loaded by the game. returns: Font - loaded game font, or null to use default
    private Font? ResolveTimedSubtitleFont()
    {
        if (_gameFont != null)
            return _gameFont;

        Font[] loaded = Resources.FindObjectsOfTypeAll<Font>();
        for (int i = 0; i < loaded.Length; i++)
        {
            Font f = loaded[i];
            if (f != null && f != GUI.skin.font)
            {
                _gameFont = f;
                Plugin.Log.LogInfo($"Timed subtitle font captured from game: {f.name}");
                return _gameFont;
            }
        }

        return null;
    }

    // Renders the tabbed window contents.
    // windowId (int): Unity window identifier
    // returns: void
    private void DrawWindow(int windowId)
    {
        _activeTab = GUILayout.Toolbar(_activeTab, _tabLabels);
        GUILayout.Space(6f);

        switch (_activeTab)
        {
            case 0: DrawStatusTab(); break;
            case 1: DrawSoundsTab(); break;
            case 2: DrawPlaybackTab(); break;
            case 3: DrawNetworkTab(); break;
            case 4: DrawImporterTab(); break;
        }

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Close")) Plugin.SetMenuVisible(false);
        if (GUILayout.Button("Open Sounds Folder")) OpenSoundsFolder();
        GUILayout.EndHorizontal();
    }

    // Draws the Status tab. returns: void
    private void DrawStatusTab()
    {
        GUI.color = new Color(0.7f, 1f, 1f);
        GUILayout.Label($"Tip: While in-game, pause (Escape) then press [{Plugin.MenuToggleKey.Value}] to open this menu.");
        GUI.color = Color.white;
        GUILayout.Space(4f);

        if (BingBongNetworkSync.IsConnectedAsClient)
        {
            GUI.color = new Color(1f, 1f, 0.5f);
            GUILayout.Label("Connected as client -- sound selection and sync settings are controlled by the host.");
            string conn = BingBongNetworkSync.HasReachedHost
                ? "Host link: connected (Photon)"
                : (BingBongNetworkSync.ClientSyncFailed
                    ? "Host link: no reply from host (host may not have the mod, or sync is still loading)"
                    : "Host link: waiting for first reply over Photon...");
            GUI.color = BingBongNetworkSync.HasReachedHost ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.6f, 0.4f);
            GUILayout.Label(conn);
            GUI.color = Color.white;
            GUILayout.Space(4f);
        }

        GUILayout.Label($"Status:   {(Plugin.ClipsReady ? "ready" : "loading...")}");
        GUILayout.Label($"Loaded:   {Plugin.CustomClips.Count} clip(s)");
        GUILayout.Label($"Active:   {Plugin.GetActiveClips().Count} enabled");
        GUILayout.Label($"Last:     {Plugin.DebugLastPlayed}");
        GUILayout.Label($"Subtitle: {Plugin.ActiveSubtitle}");

        if (BingBongNetworkSync.IsHosting)
        {
            List<string> unsynced = BingBongNetworkSync.GetUnsyncedPlayerNames();
            if (unsynced.Count > 0)
            {
                GUILayout.Space(4f);
                GUI.color = new Color(1f, 0.6f, 0.4f);
                GUILayout.Label($"Clients not yet synced ({unsynced.Count}): sounds blocked until synced.");
                foreach (string name in unsynced)
                    GUILayout.Label($"  - {name}");
                GUI.color = Color.white;
            }
        }

        GUILayout.Space(6f);
        bool canRefresh = Plugin.IsHoldingBingBong || Plugin.ForceEnableRefresh.Value;
        bool refreshBusy = Plugin.IsRefreshPending;
        GUI.enabled = canRefresh && !refreshBusy;
        if (GUILayout.Button(refreshBusy ? "Syncing..." : "Refresh Sounds"))
            Plugin.Instance.StartRefresh();
        GUI.enabled = true;
        if (!canRefresh)
            GUILayout.Label("(pick up Bing Bong, or set ForceEnableRefresh = true)");
    }

    // Draws the Sounds tab with per-clip enable checkboxes and a play-now button. returns: void
    private void DrawSoundsTab()
    {
        bool isClient = BingBongNetworkSync.IsConnectedAsClient;
        if (isClient)
        {
            GUI.color = new Color(1f, 1f, 0.5f);
            GUILayout.Label("Connected as client -- selection is synced from the host. Changes here are local only.");
            GUI.color = Color.white;
            GUILayout.Space(4f);
        }

        GUILayout.Label("Check the clips that should be in the random pool. Click 'Play' to force-pick one now.");

        GUILayout.BeginHorizontal();
        GUI.enabled = !isClient;
        if (GUILayout.Button("Enable All")) SetAllEnabled(true);
        if (GUILayout.Button("Disable All")) SetAllEnabled(false);
        GUI.enabled = true;
        if (GUILayout.Button("Stop Sound"))
            Plugin.StopAllManagedAudio();
        GUILayout.EndHorizontal();

        GUILayout.Space(4f);
        _soundsScroll = GUILayout.BeginScrollView(_soundsScroll, GUILayout.Height(320f));

        for (int i = 0; i < Plugin.CustomClips.Count; i++)
        {
            AudioClip clip = Plugin.CustomClips[i];
            GUILayout.BeginHorizontal();

            bool enabled;
            if (!Plugin.EnabledClips.TryGetValue(clip.name, out enabled)) enabled = true;
            bool newEnabled = GUILayout.Toggle(enabled, "", GUILayout.Width(20f));
            if (newEnabled != enabled)
            {
                Plugin.EnabledClips[clip.name] = newEnabled;
                Plugin.SaveSelection();
            }

            GUILayout.Label($"[{i + 1}] {clip.name} ({clip.length:F1}s)", GUILayout.ExpandWidth(true));

            string subtitleTag = SubtitleLinkTag(clip.name);
            GUILayout.Label(subtitleTag, GUILayout.Width(46f));

            if (GUILayout.Button("Play", GUILayout.Width(60f)))
                Plugin.PlayThroughPluginSource(clip);
            if (GUILayout.Button("Force Next", GUILayout.Width(90f)))
            {
                Plugin.ForcedNextClipName = clip.name;
                BingBongNetworkSync.BroadcastForceNext(clip.name);
            }

            GUILayout.EndHorizontal();

            string currentSubtitle;
            if (!Plugin.SubtitleOverrides.TryGetValue(clip.name, out currentSubtitle))
                currentSubtitle = string.Empty;

            string draft;
            if (!_subtitleDrafts.TryGetValue(clip.name, out draft))
                draft = currentSubtitle;

            GUILayout.BeginHorizontal();
            GUILayout.Label("Subtitle", GUILayout.Width(56f));
            string newDraft = GUILayout.TextField(draft, GUILayout.ExpandWidth(true));
            if (!newDraft.Equals(draft, StringComparison.Ordinal))
                _subtitleDrafts[clip.name] = newDraft;

            if (GUILayout.Button("Save", GUILayout.Width(54f)))
            {
                Plugin.SaveSubtitleOverrideForClip(clip.name, newDraft);
                _subtitleDrafts[clip.name] = newDraft;
            }

            if (GUILayout.Button("Clear", GUILayout.Width(54f)))
            {
                Plugin.SaveSubtitleOverrideForClip(clip.name, string.Empty);
                _subtitleDrafts[clip.name] = string.Empty;
            }
            GUILayout.EndHorizontal();

            if (Plugin.TimedSubtitleOverrides.ContainsKey(clip.name))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(56f);
                GUI.color = new Color(1f, 0.85f, 0.4f);
                GUILayout.Label("Timed sing-along track attached. Saving above replaces it with a single line.",
                    GUILayout.ExpandWidth(true));
                GUI.color = Color.white;
                if (GUILayout.Button("Remove Timed", GUILayout.Width(120f)))
                    Plugin.RemoveTimedSubtitleForClip(clip.name);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4f);
        }

        GUILayout.EndScrollView();

        GUILayout.Space(4f);
        GUILayout.Label($"Force next override pick: {(string.IsNullOrEmpty(Plugin.ForcedNextClipName) ? "(random)" : Plugin.ForcedNextClipName)}");
        if (GUILayout.Button("Clear Forced Pick"))
            Plugin.ForcedNextClipName = string.Empty;
    }

    // Draws the Playback tab as a full music player with transport controls and a scrollable queue. returns: void
    private void DrawPlaybackTab()
    {
        List<AudioClip> active = Plugin.GetActiveClips();
        bool isPlaying = Plugin.PluginAudioSource != null && Plugin.PluginAudioSource.isPlaying;
        bool isPaused = Plugin.PluginAudioSource != null
            && !Plugin.PluginAudioSource.isPlaying
            && Plugin.PluginAudioSource.clip != null
            && Plugin.PluginAudioSource.time > 0f;
        AudioClip? nowPlaying = (isPlaying || isPaused) ? Plugin.PluginAudioSource!.clip : null;

        int currentActiveIndex = -1;
        if (nowPlaying != null)
        {
            for (int i = 0; i < active.Count; i++)
            {
                if (active[i] == nowPlaying) { currentActiveIndex = i; break; }
            }
        }

        if (active.Count > 0 && _playerQueueIndex >= active.Count)
            _playerQueueIndex = 0;

        GUILayout.Label(nowPlaying != null ? $"Now Playing:  {nowPlaying.name}" : "Now Playing:  (stopped)");

        GUILayout.BeginHorizontal();
        float clipTime = (isPlaying || isPaused) ? Plugin.PluginAudioSource!.time : 0f;
        float clipLen = nowPlaying != null ? Mathf.Max(0.01f, nowPlaying.length) : 1f;
        GUILayout.Label(FormatTime(clipTime), GUILayout.Width(40f));
        GUI.enabled = nowPlaying != null;
        float newTime = GUILayout.HorizontalSlider(clipTime, 0f, clipLen);
        if (GUI.enabled && Mathf.Abs(newTime - clipTime) > 0.05f)
            Plugin.PluginAudioSource!.time = Mathf.Clamp(newTime, 0f, clipLen - 0.01f);
        GUI.enabled = true;
        GUILayout.Label(FormatTime(clipLen), GUILayout.Width(40f));
        GUILayout.EndHorizontal();

        GUILayout.Space(4f);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("<< Prev", GUILayout.Width(80f)))
        {
            if (active.Count > 0)
            {
                int baseIdx = currentActiveIndex >= 0 ? currentActiveIndex : _playerQueueIndex;
                _playerQueueIndex = (baseIdx - 1 + active.Count) % active.Count;
                Plugin.PlayThroughPluginSource(active[_playerQueueIndex]);
            }
        }
        string midLabel = isPlaying ? "|| Pause" : (isPaused ? "> Resume" : "> Play");
        if (GUILayout.Button(midLabel, GUILayout.Width(90f)))
        {
            if (isPlaying)
                Plugin.PausePlayback();
            else if (isPaused)
                Plugin.UnpausePlayback();
            else if (active.Count > 0)
                Plugin.PlayThroughPluginSource(active[_playerQueueIndex]);
        }
        if (GUILayout.Button("[] Stop", GUILayout.Width(70f)))
            Plugin.StopAllManagedAudio();
        if (GUILayout.Button("Next >>", GUILayout.Width(80f)))
        {
            if (active.Count > 0)
            {
                int baseIdx = currentActiveIndex >= 0 ? currentActiveIndex : _playerQueueIndex;
                _playerQueueIndex = (baseIdx + 1) % active.Count;
                Plugin.PlayThroughPluginSource(active[_playerQueueIndex]);
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(4f);

        GUILayout.BeginHorizontal();
        Plugin.MusicMode.Value = GUILayout.Toggle(Plugin.MusicMode.Value, "  Music Mode", GUILayout.Width(120f));
        GUILayout.Space(8f);
        GUILayout.Label($"Vol: {Plugin.VolumeMultiplier.Value:F2}x", GUILayout.Width(72f));
        Plugin.VolumeMultiplier.Value = GUILayout.HorizontalSlider(Plugin.VolumeMultiplier.Value, 0f, 3f);
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);

        GUILayout.Label($"Queue  --  {active.Count} playable clip(s):");
        _playbackScroll = GUILayout.BeginScrollView(_playbackScroll, GUILayout.Height(200f));
        for (int i = 0; i < active.Count; i++)
        {
            AudioClip clip = active[i];
            bool isCurrent = clip == nowPlaying;
            GUILayout.BeginHorizontal();
            if (isCurrent)
            {
                GUI.color = isPlaying ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 1f, 0.5f);
                GUILayout.Label(isPlaying ? ">" : "||", GUILayout.Width(16f));
            }
            else
            {
                GUILayout.Space(16f);
            }
            GUILayout.Label($"{i + 1}. {clip.name}  ({FormatTime(clip.length)})", GUILayout.ExpandWidth(true));
            GUI.color = Color.white;
            if (GUILayout.Button("Play", GUILayout.Width(50f)))
            {
                _playerQueueIndex = i;
                Plugin.PlayThroughPluginSource(clip);
            }
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        GUILayout.Space(4f);

        if (GUILayout.Button(_playbackSettingsExpanded ? "v Settings" : "> Settings", GUILayout.Width(100f)))
            _playbackSettingsExpanded = !_playbackSettingsExpanded;

        if (_playbackSettingsExpanded)
        {
            bool globalDistance = !Plugin.ShortRangeOnly.Value;
            bool newGlobal = GUILayout.Toggle(globalDistance, "  Global distance (hear anywhere)");
            Plugin.ShortRangeOnly.Value = !newGlobal;
            if (Plugin.ShortRangeOnly.Value)
            {
                GUILayout.Label($"  Max distance: {Plugin.ShortRangeMaxDistance.Value:F0} m");
                Plugin.ShortRangeMaxDistance.Value = GUILayout.HorizontalSlider(Plugin.ShortRangeMaxDistance.Value, 5f, 200f);
            }
            Plugin.AutoPlayEnabled.Value = GUILayout.Toggle(Plugin.AutoPlayEnabled.Value,
                "  Auto-play random clip on a timer");
            if (Plugin.AutoPlayEnabled.Value)
            {
                GUILayout.Label($"  Interval: {Plugin.AutoPlayIntervalSeconds.Value:F0} s");
                Plugin.AutoPlayIntervalSeconds.Value = GUILayout.HorizontalSlider(Plugin.AutoPlayIntervalSeconds.Value, 5f, 300f);
            }
            Plugin.TimedSubtitlesEnabled.Value = GUILayout.Toggle(Plugin.TimedSubtitlesEnabled.Value,
                "  Timed sing-along subtitles [experimental]");
        }
    }

    // Formats a duration in seconds as M:SS.
    // seconds (float): duration to format
    // returns: string
    private static string FormatTime(float seconds)
    {
        int s = Mathf.FloorToInt(Mathf.Max(0f, seconds));
        return $"{s / 60}:{s % 60:D2}";
    }

    // Draws the Network tab with sync status and size guard. returns: void
    private void DrawNetworkTab()
    {
        GUILayout.Label($"Sync server: {BingBongNetworkSync.StatusText}");
        GUILayout.Label("Files larger than the limit below will not be served or downloaded, keeping joins fast.");
        GUILayout.Label($"Max sync file size: {Plugin.MaxSyncFileSizeKb.Value} KB");
        Plugin.MaxSyncFileSizeKb.Value = (int)GUILayout.HorizontalSlider(Plugin.MaxSyncFileSizeKb.Value, 64f, 8192f);
        GUILayout.Space(6f);

        if (BingBongNetworkSync.IsHosting)
        {
            Plugin.AllowClientImports.Value = GUILayout.Toggle(Plugin.AllowClientImports.Value,
                "  Allow any client to upload new sounds to this host");

            GUILayout.Space(4f);
            int servedFiles = BingBongNetworkSync.GetServedAudioFileCount();
            List<string> unsynced = BingBongNetworkSync.GetUnsyncedPlayerNames();
            List<(string displayName, int count)> clientCounts = BingBongNetworkSync.GetClientDownloadCounts();
            GUILayout.Label($"Serving {servedFiles} audio file(s) to {clientCounts.Count} known client(s)");

            if (unsynced.Count > 0)
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                GUILayout.Label($"Not yet synced ({unsynced.Count}):");
                foreach (string name in unsynced)
                {
                    int got = BingBongNetworkSync.GetPlayerSyncedFileCount(name);
                    string progress = servedFiles > 0 ? $" ({got}/{servedFiles} files)" : string.Empty;
                    GUILayout.Label($"  - {name}{progress}");
                }
                GUI.color = Color.white;
            }
            else if (clientCounts.Count > 0)
            {
                GUI.color = new Color(0.5f, 1f, 0.5f);
                GUILayout.Label("All lobby clients are synced.");
                GUI.color = Color.white;
            }

            if (clientCounts.Count > 0)
            {
                GUILayout.Space(2f);
                foreach ((string name, int count) in clientCounts)
                {
                    string syncTag = servedFiles > 0 && count >= servedFiles ? " [synced]" : $" [{count}/{servedFiles} files]";
                    GUILayout.Label($"  {name}{syncTag}");
                }
            }

            List<(string fileName, int pending)> pendingImports = BingBongNetworkSync.GetPendingImports();
            if (pendingImports.Count > 0)
            {
                GUILayout.Space(4f);
                GUILayout.Label("Waiting for clients to download:");
                foreach ((string fileName, int pending) in pendingImports)
                    GUILayout.Label($"  {fileName}  --  {pending} client(s) remaining");
            }
        }
        else if (!string.IsNullOrEmpty(BingBongNetworkSync.ActiveHostAddress))
        {
            string importLabel = BingBongNetworkSync.HostAllowsClientImports
                ? "Host allows client imports: YES"
                : "Host allows client imports: NO (host must enable AllowClientImports)";
            GUILayout.Label(importLabel);
            GUILayout.Label($"Local clips loaded: {Plugin.CustomClips.Count}");
            if (!BingBongNetworkSync.HasReachedHost)
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                GUILayout.Label("Waiting for first reply from the host (running over Photon).");
                GUILayout.Label("Verify the host has the mod installed and is in the same Photon room.");
                GUI.color = Color.white;
            }
            else if (BingBongNetworkSync.StatusText.Contains("failed"))
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                GUILayout.Label($"Sync status: {BingBongNetworkSync.StatusText}");
                GUILayout.Label("Use Refresh Sounds button to retry.");
                GUI.color = Color.white;
            }
        }
    }

    // Draws the Importer tab with the URL field. returns: void
    private void DrawImporterTab()
    {
        if (BingBongNetworkSync.IsConnectedAsClient)
        {
            GUI.color = new Color(1f, 1f, 0.5f);
            GUILayout.Label(BingBongNetworkSync.HostAllowsClientImports
                ? "Connected as client -- downloads will be sent to the host (AllowClientImports is ON)."
                : "Connected as client -- host does not allow client imports.");
            GUI.color = Color.white;
            GUILayout.Space(4f);
        }

        GUILayout.Label("Paste a direct audio URL (.ogg or .wav). The clip is normalized, validated, and added to the pool.");
        _importUrl = GUILayout.TextField(_importUrl);
        GUILayout.Label($"Status: {Plugin.ImportStatus}");

        GUILayout.Space(6f);
        Plugin.FetchTimedSubtitlesOnImport.Value = GUILayout.Toggle(Plugin.FetchTimedSubtitlesOnImport.Value,
            "  Also fetch timed sing-along subtitles for this import [experimental] (yt-dlp captions)"); ;

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Download + Refresh"))
            Plugin.Instance.StartImportFromUrl(_importUrl);
        if (GUILayout.Button("Clear"))
            _importUrl = string.Empty;
        GUILayout.EndHorizontal();
    }

    // Sets every loaded clip's enabled flag and persists the change.
    // value (bool): true to enable all, false to disable all
    // returns: void
    private void SetAllEnabled(bool value)
    {
        foreach (AudioClip c in Plugin.CustomClips)
            Plugin.EnabledClips[c.name] = value;
        Plugin.SaveSelection();
    }

    // Opens the sounds folder in the OS file explorer. returns: void
    private void OpenSoundsFolder()
    {
        try
        {
            Application.OpenURL("file:///" + Plugin.SoundsFolder.Replace('\\', '/'));
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"OpenSoundsFolder failed: {ex.Message}");
        }
    }

    // Returns a short tag showing which subtitle overrides are linked to the clip.
    // clipName (string): clip name (no extension) to look up in SubtitleOverrides
    // returns: string - "[S]", "[T]", "[ST]", or "[ ]" depending on which overrides are present
    private static string SubtitleLinkTag(string clipName)
    {
        bool hasSimple = Plugin.SubtitleOverrides.TryGetValue(clipName, out string val) && !string.IsNullOrWhiteSpace(val);
        bool hasTimed = Plugin.TimedSubtitleOverrides.ContainsKey(clipName);
        if (hasSimple && hasTimed) return "[ST]";
        if (hasTimed) return "[T]";
        if (hasSimple) return "[S]";
        return "[ ]";
    }
}
