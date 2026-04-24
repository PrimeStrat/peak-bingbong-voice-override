using System;
using System.Collections.Generic;
using UnityEngine;
namespace BingBongVoiceOverride;

// Single in-game IMGUI window with tabbed sections for status, sound selection, playback, network, and the URL importer.
internal class UnifiedMenu : MonoBehaviour
{
    private const float WIN_W = 820f;
    private const float WIN_H = 660f;

    private Rect _windowRect = new Rect(0f, 0f, WIN_W, WIN_H);
    private bool _windowRectInitialized = false;
    private Vector2 _soundsScroll = Vector2.zero;
    private string _importUrl = string.Empty;
    private int _activeTab = 0;
    private readonly string[] _tabLabels = ["  Status  ", "  Sounds  ", "  Playback  ", "  Network  ", "  Importer  ", "  Settings  "];
    private readonly Dictionary<string, string> _subtitleDrafts = new(StringComparer.OrdinalIgnoreCase);

    private Vector2 _playbackScroll = Vector2.zero;
    private int _playerQueueIndex = 0;
    private Vector2 _settingsScroll = Vector2.zero;
    private GUIStyle? _overlayStyle;
    private Font? _overlayStyleFont;
    private int _overlayStyleFontSize;
    private Font? _gameFont;

    // Skin cache
    private GUISkin? _skin;
    private Texture2D? _texDark;
    private Texture2D? _texMid;
    private Texture2D? _texAccent;
    private Texture2D? _texButton;
    private Texture2D? _texButtonHover;
    private Texture2D? _texButtonActive;
    private Texture2D? _texHeader;

    // On-join lobby HUD toast
    internal static float JoinToastUntil = 0f;
    private GUIStyle? _toastStyle;

    private static readonly Color TimedFillColor = new Color(0.96f, 0.97f, 0.55f, 1f);
    private static readonly Color TimedStrokeColor = new Color(0.08f, 0.08f, 0.04f, 1f);
    private const float TimedStrokeWidth = 3f;
    private const int TimedFontSizeDefault = 28;

    // Polls the menu toggle key each frame and ticks the join toast. returns: void
    private void Update()
    {
        bool isClient = BingBongNetworkSync.IsConnectedAsClient;
        if (isClient && !BingBongNetworkSync.ClientAllowMenu)
        {
            if (Plugin.MenuVisible)
                Plugin.SetMenuVisible(false);
            return;
        }
        if (Input.GetKeyDown(Plugin.MenuToggleKey.Value))
            Plugin.SetMenuVisible(!Plugin.MenuVisible);
    }

    // Builds a 1x1 solid-color texture. returns: Texture2D
    private static Texture2D MakeTex(Color c)
    {
        Texture2D t = new Texture2D(1, 1);
        t.SetPixel(0, 0, c);
        t.Apply();
        return t;
    }

    // Lazily builds the dark themed GUISkin used for the window. returns: GUISkin
    private GUISkin GetSkin()
    {
        if (_skin != null) return _skin;

        _texDark = MakeTex(new Color(0.10f, 0.10f, 0.13f, 0.97f));
        _texMid = MakeTex(new Color(0.17f, 0.17f, 0.22f, 1f));
        _texAccent = MakeTex(new Color(0.98f, 0.72f, 0.10f, 1f));
        _texButton = MakeTex(new Color(0.22f, 0.22f, 0.30f, 1f));
        _texButtonHover = MakeTex(new Color(0.30f, 0.30f, 0.42f, 1f));
        _texButtonActive = MakeTex(new Color(0.98f, 0.72f, 0.10f, 1f));
        _texHeader = MakeTex(new Color(0.14f, 0.14f, 0.19f, 1f));

        _skin = Instantiate(GUI.skin);

        _skin.window.normal.background = _texDark;
        _skin.window.normal.textColor = new Color(0.98f, 0.72f, 0.10f, 1f);
        _skin.window.focused.background = _texDark;
        _skin.window.focused.textColor = new Color(0.98f, 0.72f, 0.10f, 1f);
        _skin.window.onNormal.background = _texDark;
        _skin.window.onNormal.textColor = new Color(0.98f, 0.72f, 0.10f, 1f);
        _skin.window.fontSize = 14;
        _skin.window.fontStyle = FontStyle.Bold;
        _skin.window.padding = new RectOffset(10, 10, 28, 10);

        _skin.label.normal.textColor = new Color(0.90f, 0.90f, 0.95f, 1f);
        _skin.label.fontSize = 13;

        _skin.button.normal.background = _texButton;
        _skin.button.normal.textColor = new Color(0.92f, 0.92f, 1f, 1f);
        _skin.button.hover.background = _texButtonHover;
        _skin.button.hover.textColor = Color.white;
        _skin.button.active.background = _texButtonActive;
        _skin.button.active.textColor = new Color(0.08f, 0.08f, 0.08f, 1f);
        _skin.button.fontSize = 14;
        _skin.button.padding = new RectOffset(10, 10, 8, 8);
        _skin.button.fixedHeight = 30f;

        _skin.toggle.normal.textColor = new Color(0.85f, 0.85f, 0.90f, 1f);
        _skin.toggle.fontSize = 13;
        _skin.toggle.fixedHeight = 26f;

        _skin.textField.normal.background = _texMid;
        _skin.textField.normal.textColor = Color.white;
        _skin.textField.focused.background = _texMid;
        _skin.textField.focused.textColor = Color.white;
        _skin.textField.fontSize = 13;
        _skin.textField.padding = new RectOffset(6, 6, 6, 6);
        _skin.textField.fixedHeight = 28f;

        _skin.horizontalSlider.normal.background = _texMid;
        _skin.horizontalSlider.fixedHeight = 14f;
        _skin.horizontalSlider.padding = new RectOffset(0, 0, 0, 0);
        _skin.horizontalSliderThumb.normal.background = _texAccent;
        _skin.horizontalSliderThumb.hover.background = _texButtonHover;
        _skin.horizontalSliderThumb.fixedWidth = 16f;
        _skin.horizontalSliderThumb.fixedHeight = 20f;

        // Toolbar (tab bar)
        _skin.GetStyle("toolbar").normal.background = _texHeader;
        _skin.GetStyle("toolbar").fontSize = 13;
        _skin.GetStyle("toolbarButton").normal.background = _texHeader;
        _skin.GetStyle("toolbarButton").normal.textColor = new Color(0.70f, 0.70f, 0.80f, 1f);
        _skin.GetStyle("toolbarButton").hover.background = _texMid;
        _skin.GetStyle("toolbarButton").hover.textColor = Color.white;
        _skin.GetStyle("toolbarButton").active.background = _texAccent;
        _skin.GetStyle("toolbarButton").active.textColor = new Color(0.06f, 0.06f, 0.06f, 1f);
        _skin.GetStyle("toolbarButton").onNormal.background = _texAccent;
        _skin.GetStyle("toolbarButton").onNormal.textColor = new Color(0.06f, 0.06f, 0.06f, 1f);
        _skin.GetStyle("toolbarButton").onHover.background = _texAccent;
        _skin.GetStyle("toolbarButton").onHover.textColor = new Color(0.06f, 0.06f, 0.06f, 1f);
        _skin.GetStyle("toolbarButton").onActive.background = _texButtonHover;
        _skin.GetStyle("toolbarButton").onActive.textColor = Color.white;
        _skin.GetStyle("toolbarButton").fontSize = 13;
        _skin.GetStyle("toolbarButton").fontStyle = FontStyle.Bold;
        _skin.GetStyle("toolbarButton").padding = new RectOffset(10, 10, 6, 6);

        _skin.scrollView.normal.background = _texDark;
        _skin.verticalScrollbar.normal.background = _texMid;
        _skin.verticalScrollbarThumb.normal.background = _texButton;

        return _skin;
    }

    // Draws the menu window when visible. returns: void
    private void OnGUI()
    {
        DrawSubtitleOverlay();
        DrawJoinToast();

        if (!Plugin.MenuVisible) return;
        if (!_windowRectInitialized)
        {
            _windowRect = new Rect(
                (Screen.width - WIN_W) * 0.5f,
                (Screen.height - WIN_H) * 0.35f,
                WIN_W, WIN_H);
            _windowRectInitialized = true;
        }

        GUISkin prev = GUI.skin;
        GUI.skin = GetSkin();
        _windowRect = GUILayout.Window(9875, _windowRect, DrawWindow,
            $"  BBVO  v{MyPluginInfo.PLUGIN_VERSION}   [{Plugin.MenuToggleKey.Value}] to close",
            GUILayout.Width(WIN_W), GUILayout.Height(WIN_H));
        GUI.skin = prev;
    }

    // Draws the on-join lobby HUD toast that fades out after a few seconds. returns: void
    private void DrawJoinToast()
    {
        if (Time.unscaledTime > JoinToastUntil) return;

        if (_toastStyle == null)
        {
            _toastStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                richText = false,
            };
            _toastStyle.normal.textColor = new Color(0.98f, 0.90f, 0.45f, 1f);
            _toastStyle.normal.background = MakeTex(new Color(0.08f, 0.08f, 0.12f, 0.88f));
            _toastStyle.padding = new RectOffset(18, 18, 12, 12);
        }

        float fade = Mathf.Clamp01((JoinToastUntil - Time.unscaledTime) / 1.5f);
        Color prev = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, fade);

        float w = 520f;
        float h = 70f;
        float x = (Screen.width - w) * 0.5f;
        float y = Screen.height * 0.12f;
        GUI.Box(new Rect(x, y, w, h),
            $"BingBong Voice Override is active!  Press [{Plugin.MenuToggleKey.Value}] to open.",
            _toastStyle);

        GUI.color = prev;
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
        GUILayout.Space(4f);
        _activeTab = GUILayout.Toolbar(_activeTab, _tabLabels);
        GUILayout.Space(8f);

        switch (_activeTab)
        {
            case 0: DrawStatusTab(); break;
            case 1: DrawSoundsTab(); break;
            case 2: DrawPlaybackTab(); break;
            case 3: DrawNetworkTab(); break;
            case 4: DrawImporterTab(); break;
            case 5: DrawSettingsTab(); break;
        }

        GUILayout.FlexibleSpace();
        GUILayout.Space(4f);

        // Accent separator
        Rect sep = GUILayoutUtility.GetRect(0, 2f, GUILayout.ExpandWidth(true));
        GUI.DrawTexture(sep, _texAccent ?? Texture2D.whiteTexture);
        GUILayout.Space(6f);

        GUILayout.BeginHorizontal();
        GUI.backgroundColor = new Color(0.8f, 0.25f, 0.25f, 1f);
        if (GUILayout.Button("  Close  ", GUILayout.Width(90f)))
            Plugin.SetMenuVisible(false);
        GUI.backgroundColor = Color.white;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Open Sounds Folder"))
            OpenSoundsFolder();
        GUILayout.EndHorizontal();
    }

    // Draws a tinted section header label. returns: void
    private static void SectionHeader(string text)
    {
        GUILayout.Space(4f);
        Color prev = GUI.color;
        GUI.color = new Color(0.98f, 0.72f, 0.10f, 1f);
        GUILayout.Label(text);
        GUI.color = prev;
        GUILayout.Space(2f);
    }

    // Draws the Status tab. returns: void
    private void DrawStatusTab()
    {
        SectionHeader("Info");
        GUI.color = new Color(0.7f, 1f, 1f);
        GUILayout.Label($"Press [{Plugin.MenuToggleKey.Value}] while the escape menu is open to toggle this window.");
        GUI.color = Color.white;
        GUILayout.Space(6f);

        if (BingBongNetworkSync.IsConnectedAsClient)
        {
            SectionHeader("Network");
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
            GUILayout.Space(6f);
        }

        SectionHeader("State");
        Color readyColor = Plugin.ClipsReady ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.8f, 0.3f);
        GUI.color = readyColor;
        GUILayout.Label($"Status:   {(Plugin.ClipsReady ? "ready" : "loading...")}");
        GUI.color = Color.white;
        GUILayout.Label($"Loaded:   {Plugin.CustomClips.Count} clip(s)");
        GUILayout.Label($"Active:   {Plugin.GetActiveClips().Count} enabled");
        GUILayout.Label($"Last:     {Plugin.DebugLastPlayed}");
        if (!string.IsNullOrWhiteSpace(Plugin.ActiveSubtitle))
        {
            GUI.color = new Color(1f, 0.97f, 0.6f);
            GUILayout.Label($"Subtitle: {Plugin.ActiveSubtitle}");
            GUI.color = Color.white;
        }

        if (BingBongNetworkSync.IsHosting)
        {
            List<string> unsynced = BingBongNetworkSync.GetUnsyncedPlayerNames();
            if (unsynced.Count > 0)
            {
                GUILayout.Space(6f);
                SectionHeader("Clients not yet synced");
                GUI.color = new Color(1f, 0.6f, 0.4f);
                GUILayout.Label($"Sounds are blocked until all clients confirm. ({unsynced.Count} remaining)");
                foreach (string name in unsynced)
                    GUILayout.Label($"  - {name}");
                GUI.color = Color.white;
            }
        }

        GUILayout.Space(8f);
        bool canRefresh = Plugin.IsHoldingBingBong || Plugin.ForceEnableRefresh.Value;
        bool refreshBusy = Plugin.IsRefreshPending;
        GUI.enabled = canRefresh && !refreshBusy;
        GUI.backgroundColor = refreshBusy ? new Color(0.3f, 0.3f, 0.4f) : new Color(0.25f, 0.55f, 0.25f);
        if (GUILayout.Button(refreshBusy ? "  Syncing...  " : "  Refresh Sounds  ", GUILayout.Width(180f)))
            Plugin.Instance.StartRefresh();
        GUI.backgroundColor = Color.white;
        GUI.enabled = true;
        if (!canRefresh)
        {
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label("(pick up Bing Bong, or enable ForceEnableRefresh in config)");
            GUI.color = Color.white;
        }
    }

    // Draws the Sounds tab with per-clip enable checkboxes and a play-now button. returns: void
    private void DrawSoundsTab()
    {
        bool isClient = BingBongNetworkSync.IsConnectedAsClient;
        bool canPlay = !isClient || BingBongNetworkSync.ClientAllowPlayback;
        bool canEditSelection = !isClient || BingBongNetworkSync.ClientAllowSelectionEdit;
        bool canEditSubtitle = !isClient || BingBongNetworkSync.ClientAllowSubtitleEdit;
        if (isClient)
        {
            GUI.color = new Color(1f, 1f, 0.5f);
            string playPerm = BingBongNetworkSync.ClientAllowPlayback ? "allowed" : "locked";
            string selPerm = BingBongNetworkSync.ClientAllowSelectionEdit ? "can edit" : "read-only";
            string subPerm = BingBongNetworkSync.ClientAllowSubtitleEdit ? "can edit" : "read-only";
            GUILayout.Label($"Connected as client -- playback: {playPerm}, selection: {selPerm}, subtitles: {subPerm}.");
            GUI.color = Color.white;
            GUILayout.Space(4f);
        }

        GUILayout.Label("Check the clips that should be in the random pool. Click 'Play' to force-pick one now.");

        GUILayout.BeginHorizontal();
        GUI.enabled = canEditSelection;
        if (GUILayout.Button("Enable All")) SetAllEnabled(true);
        if (GUILayout.Button("Disable All")) SetAllEnabled(false);
        GUI.enabled = canPlay;
        if (GUILayout.Button("Stop Sound"))
            Plugin.StopAllManagedAudio();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        GUILayout.Space(4f);
        _soundsScroll = GUILayout.BeginScrollView(_soundsScroll, GUILayout.Height(320f));

        for (int i = 0; i < Plugin.CustomClips.Count; i++)
        {
            AudioClip clip = Plugin.CustomClips[i];
            GUILayout.BeginHorizontal();

            bool enabled;
            if (!Plugin.EnabledClips.TryGetValue(clip.name, out enabled)) enabled = true;
            GUI.enabled = canEditSelection;
            bool newEnabled = GUILayout.Toggle(enabled, "", GUILayout.Width(20f));
            GUI.enabled = true;
            if (newEnabled != enabled && canEditSelection)
            {
                Plugin.EnabledClips[clip.name] = newEnabled;
                Plugin.SaveSelection();
                BingBongNetworkSync.BroadcastClipEnabled(clip.name, newEnabled);
            }

            GUILayout.Label($"[{i + 1}] {clip.name} ({clip.length:F1}s)", GUILayout.ExpandWidth(true));

            string subtitleTag = SubtitleLinkTag(clip.name);
            GUILayout.Label(subtitleTag, GUILayout.Width(46f));

            GUI.enabled = canPlay;
            if (GUILayout.Button("Play", GUILayout.Width(60f)))
                Plugin.PlayThroughPluginSource(clip);
            if (GUILayout.Button("Force Next", GUILayout.Width(90f)))
            {
                Plugin.ForcedNextClipName = clip.name;
                BingBongNetworkSync.BroadcastForceNext(clip.name);
            }
            GUI.enabled = true;

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

            GUI.enabled = canEditSubtitle;
            if (GUILayout.Button("Save", GUILayout.Width(54f)))
            {
                Plugin.SaveSubtitleOverrideForClip(clip.name, newDraft);
                _subtitleDrafts[clip.name] = newDraft;
                BingBongNetworkSync.BroadcastSubtitleUpdate(clip.name, newDraft);
            }

            if (GUILayout.Button("Clear", GUILayout.Width(54f)))
            {
                Plugin.SaveSubtitleOverrideForClip(clip.name, string.Empty);
                _subtitleDrafts[clip.name] = string.Empty;
                BingBongNetworkSync.BroadcastSubtitleUpdate(clip.name, string.Empty);
            }
            GUI.enabled = true;
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
        GUI.enabled = canPlay;
        if (GUILayout.Button("Clear Forced Pick"))
            Plugin.ForcedNextClipName = string.Empty;
        GUI.enabled = true;
    }

    // Draws the Playback tab as a full music player with transport controls and a scrollable queue. returns: void
    private void DrawPlaybackTab()
    {
        bool isClient = BingBongNetworkSync.IsConnectedAsClient;
        bool canPlay = !isClient || BingBongNetworkSync.ClientAllowPlayback;
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
        GUI.enabled = canPlay && nowPlaying != null;
        float newTime = GUILayout.HorizontalSlider(clipTime, 0f, clipLen);
        if (GUI.enabled && Mathf.Abs(newTime - clipTime) > 0.05f)
            Plugin.PluginAudioSource!.time = Mathf.Clamp(newTime, 0f, clipLen - 0.01f);
        GUI.enabled = true;
        GUILayout.Label(FormatTime(clipLen), GUILayout.Width(40f));
        GUILayout.EndHorizontal();

        GUILayout.Space(4f);

        GUI.enabled = canPlay;
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
        GUI.enabled = true;

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
            GUI.enabled = canPlay;
            if (GUILayout.Button("Play", GUILayout.Width(50f)))
            {
                _playerQueueIndex = i;
                Plugin.PlayThroughPluginSource(clip);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

    }

    // Formats a duration in seconds as M:SS.
    // seconds (float): duration to format
    // returns: string
    private static string FormatTime(float seconds)
    {
        int s = Mathf.FloorToInt(Mathf.Max(0f, seconds));
        return $"{s / 60}:{s % 60:D2}";
    }

    // Draws the Network tab with live sync status and per-client progress. returns: void
    private void DrawNetworkTab()
    {
        GUILayout.Label($"Sync server: {BingBongNetworkSync.StatusText}");
        GUILayout.Space(6f);

        if (BingBongNetworkSync.IsHosting)
        {
            GUILayout.Space(4f);
            int servedFiles = BingBongNetworkSync.GetServedAudioFileCount();
            List<string> unsynced = BingBongNetworkSync.GetUnsyncedPlayerNames();
            List<(string displayName, int count)> clientCounts = BingBongNetworkSync.GetClientDownloadCounts();
            GUILayout.Label($"Serving {servedFiles} file(s) to {clientCounts.Count} known client(s)");

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

    // Draws the Settings tab with global playback, network, and menu options. returns: void
    private void DrawSettingsTab()
    {
        bool isClient = BingBongNetworkSync.IsConnectedAsClient;
        bool isHost = BingBongNetworkSync.IsHosting;
        bool canSettings = !isClient || BingBongNetworkSync.ClientAllowSettingsChange;

        _settingsScroll = GUILayout.BeginScrollView(_settingsScroll);

        SectionHeader("Playback");
        GUI.enabled = canSettings;
        Plugin.MusicMode.Value = GUILayout.Toggle(Plugin.MusicMode.Value, "  Music Mode");
        GUILayout.BeginHorizontal();
        GUILayout.Label($"  Volume: {Plugin.VolumeMultiplier.Value:F2}x", GUILayout.Width(130f));
        Plugin.VolumeMultiplier.Value = GUILayout.HorizontalSlider(Plugin.VolumeMultiplier.Value, 0f, 3f);
        GUILayout.EndHorizontal();
        bool globalDistance = !Plugin.ShortRangeOnly.Value;
        Plugin.ShortRangeOnly.Value = !GUILayout.Toggle(globalDistance, "  Global distance (hear anywhere)");
        if (Plugin.ShortRangeOnly.Value)
        {
            GUILayout.Label($"  Max distance: {Plugin.ShortRangeMaxDistance.Value:F0} m");
            Plugin.ShortRangeMaxDistance.Value = GUILayout.HorizontalSlider(Plugin.ShortRangeMaxDistance.Value, 5f, 200f);
        }
        Plugin.AutoPlayEnabled.Value = GUILayout.Toggle(Plugin.AutoPlayEnabled.Value, "  Auto-play random clip on a timer");
        if (Plugin.AutoPlayEnabled.Value)
        {
            GUILayout.Label($"  Interval: {Plugin.AutoPlayIntervalSeconds.Value:F0} s");
            Plugin.AutoPlayIntervalSeconds.Value = GUILayout.HorizontalSlider(Plugin.AutoPlayIntervalSeconds.Value, 5f, 300f);
        }
        Plugin.TimedSubtitlesEnabled.Value = GUILayout.Toggle(Plugin.TimedSubtitlesEnabled.Value, "  Timed sing-along subtitles [experimental]");
        GUI.enabled = true;

        GUILayout.Space(8f);
        SectionHeader("Network");
        string maxSyncLabel = Plugin.MaxSyncFileSizeKb.Value <= 0
            ? "  Max sync file size: unlimited"
            : $"  Max sync file size: {Plugin.MaxSyncFileSizeKb.Value} KB";
        GUILayout.Label(maxSyncLabel);
        float maxSyncSlider = Plugin.MaxSyncFileSizeKb.Value <= 0 ? 0f : Plugin.MaxSyncFileSizeKb.Value;
        maxSyncSlider = GUILayout.HorizontalSlider(maxSyncSlider, 0f, 8192f);
        Plugin.MaxSyncFileSizeKb.Value = maxSyncSlider < 64f ? 0 : (int)maxSyncSlider;
        GUILayout.Space(4f);
        if (isHost)
        {
            GUILayout.Label("Client permissions:");
            Plugin.AllowClientImports.Value = GUILayout.Toggle(Plugin.AllowClientImports.Value,
                "  Allow any client to upload new sounds to this host");
            Plugin.AllowClientPlayback.Value = GUILayout.Toggle(Plugin.AllowClientPlayback.Value,
                "  Allow clients to control playback (play/pause/stop/force-next)");
            Plugin.AllowClientSubtitleEdit.Value = GUILayout.Toggle(Plugin.AllowClientSubtitleEdit.Value,
                "  Allow clients to save subtitle edits");
            Plugin.AllowClientSelectionEdit.Value = GUILayout.Toggle(Plugin.AllowClientSelectionEdit.Value,
                "  Allow clients to toggle clip enabled states");
            Plugin.AllowClientSettingsChange.Value = GUILayout.Toggle(Plugin.AllowClientSettingsChange.Value,
                "  Allow clients to change settings (volume, mode, autoplay)");
            Plugin.AllowClientMenu.Value = GUILayout.Toggle(Plugin.AllowClientMenu.Value,
                "  Allow clients to open the mod menu");
        }
        else if (isClient)
        {
            GUILayout.Label("Host permissions (read-only):");
            string yn(bool v) => v ? "allowed" : "host-locked";
            GUILayout.Label($"  Imports:           {yn(BingBongNetworkSync.HostAllowsClientImports)}");
            GUILayout.Label($"  Playback:          {yn(BingBongNetworkSync.ClientAllowPlayback)}");
            GUILayout.Label($"  Subtitle editing:  {yn(BingBongNetworkSync.ClientAllowSubtitleEdit)}");
            GUILayout.Label($"  Clip selection:    {yn(BingBongNetworkSync.ClientAllowSelectionEdit)}");
            GUILayout.Label($"  Settings changes:  {yn(BingBongNetworkSync.ClientAllowSettingsChange)}");
            GUILayout.Label($"  Menu access:       {yn(BingBongNetworkSync.ClientAllowMenu)}");
        }

        GUILayout.Space(8f);
        SectionHeader("Menu");
        Plugin.ForceEnableRefresh.Value = GUILayout.Toggle(Plugin.ForceEnableRefresh.Value,
            "  Enable Refresh without holding Bing Bong");
        GUI.color = new Color(0.65f, 0.65f, 0.65f);
        GUILayout.Label($"  Menu toggle key: [{Plugin.MenuToggleKey.Value}]  (change in BepInEx config file)");
        GUI.color = Color.white;

        GUILayout.EndScrollView();
    }

    // Draws the Importer tab with the URL field. returns: void
    private void DrawImporterTab()
    {
        bool isClient = BingBongNetworkSync.IsConnectedAsClient;
        bool canImport = !isClient || BingBongNetworkSync.HostAllowsClientImports;

        if (isClient)
        {
            GUI.color = canImport ? new Color(0.5f, 1f, 0.5f) : new Color(1f, 0.6f, 0.4f);
            GUILayout.Label(canImport
                ? "Connected as client -- host allows imports. Your download will be sent to the host."
                : "Connected as client -- host does not allow client imports.");
            GUI.color = Color.white;
            GUILayout.Space(4f);
        }

        GUILayout.Label("Paste a direct audio URL (.ogg, .wav, or YouTube/Twitch). The clip is normalized and added to the pool.");
        GUI.enabled = canImport;
        _importUrl = GUILayout.TextField(_importUrl);
        GUI.enabled = true;
        GUILayout.Label($"Status: {Plugin.ImportStatus}");

        GUILayout.Space(6f);
        GUI.enabled = canImport;
        Plugin.FetchTimedSubtitlesOnImport.Value = GUILayout.Toggle(Plugin.FetchTimedSubtitlesOnImport.Value,
            "  Also fetch timed sing-along subtitles for this import [experimental] (yt-dlp captions)");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Download + Refresh"))
            Plugin.Instance.StartImportFromUrl(_importUrl);
        if (GUILayout.Button("Clear"))
            _importUrl = string.Empty;
        GUILayout.EndHorizontal();
        GUI.enabled = true;
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
