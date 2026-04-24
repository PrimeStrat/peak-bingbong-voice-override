using System;
using System.Collections.Generic;
using UnityEngine;

namespace BingBongVoiceOverride;

/// <summary>Single in-game IMGUI window with tabbed sections for status, sound selection, playback, network, and the URL importer.</summary>
internal class UnifiedMenu : MonoBehaviour
{
    private Rect _windowRect = new Rect(0f, 0f, 620f, 520f);
    private bool _windowRectInitialized = false;
    private Vector2 _soundsScroll = Vector2.zero;
    private string _importUrl = string.Empty;
    private int _activeTab = 0;
    private readonly string[] _tabLabels = { "Status", "Sounds", "Playback", "Network", "Importer" };
    private readonly Dictionary<string, string> _subtitleDrafts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private GUIStyle? _overlayStyle;
    private Font? _overlayStyleFont;
    private int _overlayStyleFontSize;
    private Font? _gameFont;

    private static readonly Color TimedFillColor = new Color(0.96f, 0.97f, 0.55f, 1f);
    private static readonly Color TimedStrokeColor = new Color(0.08f, 0.08f, 0.04f, 1f);
    private const float TimedStrokeWidth = 3f;
    private const int TimedFontSizeDefault = 28;

    /// <summary>Polls the menu toggle key each frame.</summary>
    /// <returns>void</returns>
    private void Update()
    {
        if (Input.GetKeyDown(Plugin.MenuToggleKey.Value))
            Plugin.SetMenuVisible(!Plugin.MenuVisible);
    }

    /// <summary>Draws the menu window when visible.</summary>
    /// <returns>void</returns>
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

    /// <summary>Draws timed subtitle fallback text with a hardcoded PEAK-like look from the reference screenshot.</summary>
    /// <returns>void</returns>
    private void DrawSubtitleOverlay()
    {
        bool overlayOnly = Plugin.ShouldForceOverlaySubtitles();
        bool forceOverlay = Plugin.IsTimedSubtitleActive
            && Plugin.UseNativeBingBongAPI.Value
            && !Plugin.UseNativeSubtitleWithCustomAudio.Value;
        if (!Plugin.ShowSubtitleOverlay.Value && !forceOverlay && !overlayOnly) return;
        if (Plugin.MenuVisible) return;
        bool timed = Plugin.IsTimedSubtitleActive;
        if (!timed && !overlayOnly) return;
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

    /// <summary>Resolves a font for timed subtitles by scanning fonts already loaded by the game. Falls back to null (uses default GUI skin font).</summary>
    /// <returns>A loaded game Font, or null to use the default.</returns>
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

    /// <summary>Renders the tabbed window contents.</summary>
    /// <param name="windowId">Unity window identifier.</param>
    /// <returns>void</returns>
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

    /// <summary>Draws the Status tab.</summary>
    /// <returns>void</returns>
    private void DrawStatusTab()
    {
        GUILayout.Label($"Status:   {(Plugin.ClipsReady ? "ready" : "loading...")}");
        GUILayout.Label($"Loaded:   {Plugin.CustomClips.Count} clip(s)");
        GUILayout.Label($"Active:   {Plugin.GetActiveClips().Count} enabled");
        GUILayout.Label($"Last:     {Plugin.DebugLastPlayed}");
        GUILayout.Label($"Subtitle: {Plugin.ActiveSubtitle}");

        GUILayout.Space(6f);
        bool canRefresh = Plugin.IsHoldingBingBong || Plugin.ForceEnableRefresh.Value;
        GUI.enabled = canRefresh;
        if (GUILayout.Button($"Refresh Sounds  [{Plugin.RefreshKey.Value}]"))
            Plugin.Instance.StartRefresh();
        GUI.enabled = true;
        if (!canRefresh)
            GUILayout.Label("(pick up Bing Bong, or set ForceEnableRefresh = true)");
    }

    /// <summary>Draws the Sounds tab with per-clip enable checkboxes and a play-now button.</summary>
    /// <returns>void</returns>
    private void DrawSoundsTab()
    {
        GUILayout.Label("Check the clips that should be in the random pool. Click 'Play' to force-pick one now.");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Enable All")) SetAllEnabled(true);
        if (GUILayout.Button("Disable All")) SetAllEnabled(false);
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
                Plugin.ForcedNextClipName = clip.name;

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

    /// <summary>Draws the Playback tab with volume, distance, autoplay, and music mode controls.</summary>
    /// <returns>void</returns>
    private void DrawPlaybackTab()
    {
        GUILayout.Label($"Volume Multiplier: {Plugin.VolumeMultiplier.Value:F2}x");
        Plugin.VolumeMultiplier.Value = GUILayout.HorizontalSlider(Plugin.VolumeMultiplier.Value, 0f, 3f);

        GUILayout.Space(8f);
        bool globalDistance = !Plugin.ShortRangeOnly.Value;
        bool newGlobalDistance = GUILayout.Toggle(globalDistance,
            "  Global distance (hear anywhere, ignores object distance)");
        Plugin.ShortRangeOnly.Value = !newGlobalDistance;
        if (Plugin.ShortRangeOnly.Value)
        {
            GUILayout.Label($"  Max distance: {Plugin.ShortRangeMaxDistance.Value:F0} m");
            Plugin.ShortRangeMaxDistance.Value = GUILayout.HorizontalSlider(Plugin.ShortRangeMaxDistance.Value, 5f, 200f);
        }

        GUILayout.Space(8f);
        Plugin.AutoPlayEnabled.Value = GUILayout.Toggle(Plugin.AutoPlayEnabled.Value,
            "  Auto-play random clip on a timer (no interaction required)");
        if (Plugin.AutoPlayEnabled.Value)
        {
            GUILayout.Label($"  Interval: {Plugin.AutoPlayIntervalSeconds.Value:F0} s");
            Plugin.AutoPlayIntervalSeconds.Value = GUILayout.HorizontalSlider(Plugin.AutoPlayIntervalSeconds.Value, 5f, 300f);
        }

        GUILayout.Space(8f);
        Plugin.MusicMode.Value = GUILayout.Toggle(Plugin.MusicMode.Value,
            "  Music player mode (continuous back-to-back playback, overrides auto-play)");

        GUILayout.Space(8f);
        Plugin.TimedSubtitlesEnabled.Value = GUILayout.Toggle(Plugin.TimedSubtitlesEnabled.Value,
            "  Timed sing-along subtitles (when off, only the single subtitle line is shown)");

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Start Playback"))
        {
            List<AudioClip> active = Plugin.GetActiveClips();
            if (active.Count > 0)
                Plugin.PlayThroughPluginSource(active[UnityEngine.Random.Range(0, active.Count)]);
        }
        if (GUILayout.Button("Stop Playback"))
        {
            Plugin.StopAllManagedAudio();
        }
        GUILayout.EndHorizontal();
    }

    /// <summary>Draws the Network tab with sync status and size guard.</summary>
    /// <returns>void</returns>
    private void DrawNetworkTab()
    {
        GUILayout.Label($"Sync server: {BingBongNetworkSync.StatusText}");
        GUILayout.Label("Files larger than the limit below will not be served or downloaded, keeping joins fast.");
        GUILayout.Label($"Max sync file size: {Plugin.MaxSyncFileSizeKb.Value} KB");
        Plugin.MaxSyncFileSizeKb.Value = (int)GUILayout.HorizontalSlider(Plugin.MaxSyncFileSizeKb.Value, 64f, 8192f);
        GUILayout.Space(6f);
    }

    /// <summary>Draws the Importer tab with the URL field.</summary>
    /// <returns>void</returns>
    private void DrawImporterTab()
    {
        GUILayout.Label("Paste a direct audio URL (.ogg or .wav). The clip is normalized, validated, and added to the pool.");
        _importUrl = GUILayout.TextField(_importUrl);
        GUILayout.Label($"Status: {Plugin.ImportStatus}");

        GUILayout.Space(6f);
        Plugin.FetchTimedSubtitlesOnImport.Value = GUILayout.Toggle(Plugin.FetchTimedSubtitlesOnImport.Value,
            "  Also fetch timed sing-along subtitles for this import (yt-dlp captions)");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Download + Refresh"))
            Plugin.Instance.StartImportFromUrl(_importUrl);
        if (GUILayout.Button("Clear"))
            _importUrl = string.Empty;
        GUILayout.EndHorizontal();
    }

    /// <summary>Sets every loaded clip's enabled flag and persists the change.</summary>
    /// <param name="value">True to enable all, false to disable all.</param>
    /// <returns>void</returns>
    private void SetAllEnabled(bool value)
    {
        foreach (AudioClip c in Plugin.CustomClips)
            Plugin.EnabledClips[c.name] = value;
        Plugin.SaveSelection();
    }

    /// <summary>Opens the sounds folder in the OS file explorer.</summary>
    /// <returns>void</returns>
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

    /// <summary>Returns a short tag showing whether a subtitle JSON override is linked to the clip.</summary>
    /// <param name="clipName">Clip name (no extension) to look up in SubtitleOverrides.</param>
    /// <returns>"[S]", "[T]", "[ST]", or "[ ]" depending on which overrides are present.</returns>
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
