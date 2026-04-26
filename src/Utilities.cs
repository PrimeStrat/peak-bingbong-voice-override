using System;
using System.Collections;
using System.Text.RegularExpressions;
using BingBongVoiceOverride.Patches;
using UnityEngine;
namespace BingBongVoiceOverride;

public partial class Plugin
{
    // Finds the transform of the nearest Bing Bong instance in the scene.
    // returns: Transform?
    private static Transform? FindBingBongTransform()
    {
        GameObject[] roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
        foreach (GameObject root in roots)
        {
            Transform? found = root.transform.Find("BingBong");
            if (found != null) return found;
        }
        return null;
    }

    // Returns the local player's nickname from the Photon player object via reflection.
    // returns: string
    internal static string GetLocalPlayerName()
    {
        try
        {
            object? localPlayer = typeof(BingBongNetworkSync).Assembly
                .GetType("Photon.Pun.PhotonNetwork")
                ?.GetProperty("LocalPlayer", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?.GetValue(null);
            if (localPlayer == null) return string.Empty;
            object? nick = localPlayer.GetType()
                .GetProperty("NickName", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?.GetValue(localPlayer);
            return nick?.ToString() ?? string.Empty;
        }
        catch (Exception) { return string.Empty; }
    }

    // Detects when the Bing Bong drop state changes; subtitle display is handled by TickForceActiveSubtitle.
    // returns: void
    private static void TickMirrorOnDrop()
    {
    }

    // Scans ManagedSources for the first AudioSource currently playing a clip matching the given clip name.
    // clipName (string): Name of the AudioClip to look for.
    // returns: AudioSource?
    internal static AudioSource? FindActiveBingBongSourcePlayingOurClip(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName)) return null;
        foreach (AudioSource src in ManagedSources)
        {
            if (src == null) continue;
            if (src.isPlaying && src.clip != null
                && src.clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))
                return src;
        }
        return null;
    }

    // Watches IsSyncBusy each frame; pauses the plugin audio source when sync starts and
    // resumes it when sync ends so audio does not play over an incomplete clip library.
    // returns: void
    private static void TickSyncPause()
    {
        bool syncBusy = BingBongNetworkSync.IsSyncBusy;
        if (syncBusy && !_wasSyncBusy)
        {
            if (PluginAudioSource != null && PluginAudioSource.isPlaying)
            {
                PluginAudioSource.Pause();
                _pausedForSync = true;
            }
        }
        else if (!syncBusy && _wasSyncBusy && _pausedForSync)
        {
            if (PluginAudioSource != null)
                PluginAudioSource.UnPause();
            _pausedForSync = false;
        }
        _wasSyncBusy = syncBusy;
    }

    // Shows or hides the mod menu and adjusts cursor state accordingly.
    // visible (bool): True to show, false to hide.
    // returns: void
    internal static void SetMenuVisible(bool visible)
    {
        if (MenuVisible == visible) return;
        MenuVisible = visible;
        SyncEscapePauseForMenu(visible);
    }

    // Locks/unlocks and shows/hides the cursor based on the desired menu visibility.
    // open (bool): True when the menu is opening.
    // returns: void
    private static void SyncEscapePauseForMenu(bool open)
    {
        if (open)
        {
            if (!_menuStateCaptured)
            {
                _savedCursorLock = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;
                _menuStateCaptured = true;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (!_menuPauseInjected)
            {
                PulseEscapeKey();
                _menuPauseInjected = true;
            }
        }
        else
        {
            if (_menuStateCaptured)
            {
                Cursor.lockState = _savedCursorLock;
                Cursor.visible = _savedCursorVisible;
                _menuStateCaptured = false;
            }
            if (_menuPauseInjected)
            {
                PulseEscapeKey();
                _menuPauseInjected = false;
            }
        }
    }

    // Sends a virtual Escape key press via the Win32 API to trigger or dismiss the in-game pause menu.
    // returns: void
    private static void PulseEscapeKey()
    {
        keybd_event(VK_ESCAPE, 0, 0, System.UIntPtr.Zero);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, System.UIntPtr.Zero);
    }

    // Callback for session-start hooks; loads custom clips when SessionLoadOnly is active.
    // returns: void
    internal void OnSessionStart()
    {
        if (SessionLoadOnly.Value && !ClipsReady)
            StartCoroutine(LoadCustomClips());
    }

    // Marks a refresh pending and queues RefreshSoundsCoroutine if one is not already running.
    // returns: void
    internal void StartRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        BingBongNetworkSync.AllowResync();
        _cachedActiveClipsExpiry = -1f;
        StartCoroutine(RefreshSoundsCoroutine());
    }

    // Clears the loaded clip list, waits for any sync to finish, then reloads all custom clips.
    // returns: IEnumerator
    private IEnumerator RefreshSoundsCoroutine()
    {
        ClipsReady = false;
        CustomClips.Clear();
        SubtitleOverrides.Clear();
        TimedSubtitleOverrides.Clear();
        EnabledClips.Clear();
        _cachedActiveClipsExpiry = -1f;

        float syncWait = 0f;
        float syncTimeout = 30f;
        while (BingBongNetworkSync.IsSyncBusy && syncWait < syncTimeout)
        {
            yield return new WaitForSecondsRealtime(0.25f);
            syncWait += 0.25f;
        }

        yield return StartCoroutine(LoadCustomClips());

        _refreshPending = false;
    }

    // Compiled Regex for sanitizing filenames, shared between ClipManager and other utilities.
    internal static readonly Regex MyRegex = new Regex("[^A-Za-z0-9_\\-\\. ]", RegexOptions.Compiled);
}
