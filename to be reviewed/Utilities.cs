using System;
using UnityEngine;
namespace BingBongVoiceOverride;

public partial class Plugin {
    private static Transform? FindBingBongTransform() {
        GameObject[] roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
                 {
            Transform? found = root.transform.Find("BingBong");
            if (found != null) return found;
        }
        return null;
    }

    internal static string GetLocalPlayerName() {
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

    internal static AudioSource? FindActiveBingBongSourcePlayingOurClip(string clipName) {
        if (string.IsNullOrWhiteSpace(clipName)) return null;
                 {
            if (src == null) continue;
            if (src.isPlaying && src.clip != null
                && src.clip.name.Equals(clipName, StringComparison.OrdinalIgnoreCase))
                return src;
        }
        return null;
    }

    private static void TickSyncPause() {
        bool syncBusy = BingBongNetworkSync.IsSyncBusy;
                 {
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

    internal static void SetMenuVisible(bool visible) {
        if (MenuVisible == visible) return;
        MenuVisible = visible;

                 {
                         {
                _savedCursorLock = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;
                _menuStateCaptured = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
                 {
                         {
                Cursor.lockState = _savedCursorLock;
                Cursor.visible = _savedCursorVisible;
                _menuStateCaptured = false;
            }
        }
    }

    internal void OnSessionStart() {
        if (SessionLoadOnly.Value && !ClipsReady)
            StartCoroutine(LoadCustomClips());
    }

    internal void StartRefresh() {
        if (_refreshPending) return;
        _refreshPending = true;
        BingBongNetworkSync.AllowResync();
        _cachedActiveClipsExpiry = -1f;
        StartCoroutine(RefreshSoundsCoroutine());
    }

    private IEnumerator RefreshSoundsCoroutine() {
        ClipsReady = false;
        CustomClips.Clear();
        SubtitleOverrides.Clear();
        TimedSubtitleOverrides.Clear();
        EnabledClips.Clear();
        _cachedActiveClipsExpiry = -1f;

        float syncWait = 0f;
        float syncTimeout = 30f;
                 {
            yield return new WaitForSecondsRealtime(0.25f);
            syncWait += 0.25f;
        }

        yield return StartCoroutine(LoadCustomClips());

        _refreshPending = false;
    }
}
