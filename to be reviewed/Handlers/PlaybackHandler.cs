using System;
using System.Collections;
using System.Collections.Generic;
using BingBongVoiceOverride.Handlers;
using UnityEngine;
namespace BingBongVoiceOverride;

public partial class Plugin {
    internal static void PlayThroughPluginSource(AudioClip clip) {
        if (clip == null || PluginAudioSource == null) return;
        RegisterManagedSource(PluginAudioSource);
        if (ShortRangeOnly.Value) {
            UnityEngine.Transform? t = FindBingBongTransform();
            if (t != null) PluginAudioSource.transform.position = t.position;
            ApplySpatialSettings(PluginAudioSource);
        }
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

    internal static void PlayDetachedFromBingBong(AudioClip clip, AudioSource originSource) {
        if (clip == null || PluginAudioSource == null) return;
        RegisterManagedSource(PluginAudioSource);

        UnityEngine.Transform? followTarget = BingBongAudioHandler.FindBingBongRoot(originSource)
            ?? FindBingBongTransform();
        FollowTransform = followTarget;
        if (followTarget != null)
            PluginAudioSource.transform.position = followTarget.position;
        PluginAudioSource.spatialBlend = 0f;
        PluginAudioSource.volume = Mathf.Clamp(VolumeMultiplier.Value, 0f, 4f);
        PluginAudioSource.clip = clip;
        PluginAudioSource.Play();
        OnClipPlayed(clip);
    }

    internal static void StopAllManagedAudio() {
        for (int i = ManagedSources.Count - 1; i >= 0; i--) {
            AudioSource src = ManagedSources[i];
            if (src == null) { ManagedSources.RemoveAt(i); continue; }
            try { src.Stop(); } catch (Exception) { }
        }
        try { PluginAudioSource?.Stop(); } catch (Exception) { }
        ClearTimedSubtitles();
        if (!SuppressPlaybackBroadcast)
            BingBongNetworkSync.BroadcastStop();
    }

    private static void StopAllPlayback() {
                 {
            if (PluginAudioSource != null) {
                PluginAudioSource.Stop();
                PluginAudioSource.clip = null;
            }
        }
        catch (Exception) { }
        for (int i = 0; i < ManagedSources.Count; i++) {
                         {
                AudioSource s = ManagedSources[i];
                if (s != null) { s.Stop(); s.clip = null; }
            }
            catch (Exception) { }
        }
        ManagedSources.Clear();
        FollowTransform = null;
        ActiveSubtitle = string.Empty;
        ActiveSubtitleUntil = 0f;
        ActiveNativeSubtitle = string.Empty;
        ActiveTimedSubtitleClip = null;
    }

    internal static void PausePlayback() {
        if (PluginAudioSource != null && PluginAudioSource.isPlaying) {
            PluginAudioSource.Pause();
            if (!SuppressPlaybackBroadcast)
                BingBongNetworkSync.BroadcastPause();
        }
    }

    internal static void UnpausePlayback() {
        if (PluginAudioSource != null && !PluginAudioSource.isPlaying && PluginAudioSource.clip != null) {
            PluginAudioSource.UnPause();
            if (!SuppressPlaybackBroadcast)
                BingBongNetworkSync.BroadcastResume();
        }
    }

    internal static void ClearTimedSubtitles() {
        ActiveSubtitle = string.Empty;
        ActiveSubtitleUntil = 0f;
        ActiveTimedSubtitleClipName = string.Empty;
        ActiveTimedSubtitleClip = null;
        ActiveTimedSubtitleStart = 0f;
        ActiveTimedSubtitleLastTick = 0f;
        ActiveNativeSubtitle = string.Empty;
    }

    internal static void ApplyRemotePlay(string clipName) {
        if (string.IsNullOrEmpty(clipName) || !ClipsReady) return;
        AudioClip? clip = CustomClips.Find(c => c.name.Equals(clipName, StringComparison.OrdinalIgnoreCase));
        if (clip == null) return;
        SuppressPlaybackBroadcast = true;
        try { PlayThroughPluginSource(clip); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    internal static void ApplyRemotePause() {
        SuppressPlaybackBroadcast = true;
        try { PausePlayback(); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    internal static void ApplyRemoteResume() {
        SuppressPlaybackBroadcast = true;
        try { UnpausePlayback(); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    internal static void ApplyRemoteStop() {
        SuppressPlaybackBroadcast = true;
        try { StopAllManagedAudio(); }
        finally { SuppressPlaybackBroadcast = false; }
    }

    internal static void ApplyRemoteForceNext(string clipName) {
        ForcedNextClipName = clipName ?? string.Empty;
    }

    private static void UpdateFollowTransform() {
        if (PluginAudioSource == null) return;
        if (!PluginAudioSource.isPlaying) {
            FollowTransform = null;
            return;
        }
        FollowTransform ??= FindBingBongTransform();

        float baseVolume = Mathf.Clamp(VolumeMultiplier.Value, 0f, 4f);
        if (!ShortRangeOnly.Value || FollowTransform == null) {
            PluginAudioSource.volume = baseVolume;
            return;
        }

        PluginAudioSource.transform.position = FollowTransform.position;
        UnityEngine.Transform? listener = FindListenerTransform();
        if (listener == null) { PluginAudioSource.volume = baseVolume; return; }
        float maxDist = Mathf.Max(1f, ShortRangeMaxDistance.Value);
        float attenuation = Mathf.Clamp01(1f - (Vector3.Distance(listener.position, FollowTransform.position) / maxDist));
        PluginAudioSource.volume = baseVolume * attenuation;
    }

    private static UnityEngine.Transform? FindListenerTransform() {
        AudioListener listener = UnityEngine.Object.FindObjectOfType<AudioListener>();
        if (listener != null) return listener.transform;
        return Camera.main?.transform;
    }

    private IEnumerator AutoPlayLoop() {
        while (true) {
            yield return new WaitForSeconds(Mathf.Max(1f, AutoPlayIntervalSeconds.Value));
            if (!EnableMod.Value || MusicMode.Value || !AutoPlayEnabled.Value || !ClipsReady) continue;
            List<AudioClip> active = GetActiveClips();
            if (active.Count == 0) continue;
            AudioClip clip = active[UnityEngine.Random.Range(0, active.Count)];
            PlayThroughPluginSource(clip);
            Log.LogInfo($"[AutoPlay] {clip.name}");
        }
    }

    private IEnumerator MusicModeLoop() {
        int index = 0;
        while (true) {
            if (!EnableMod.Value || !MusicMode.Value || !ClipsReady) {
                yield return new WaitForSeconds(0.5f);
                continue;
            }
            List<AudioClip> active = GetActiveClips();
            if (active.Count == 0) { yield return new WaitForSeconds(1f); continue; }
            if (index >= active.Count) index = 0;
            AudioClip clip = active[index++];
            PlayThroughPluginSource(clip);
            Log.LogInfo($"[Music] {clip.name}");
            float wait = Mathf.Max(0.25f, clip.length + 0.25f);
            float elapsed = 0f;
            while (elapsed < wait) {
                if (!MusicMode.Value) break;
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }
    }
}
