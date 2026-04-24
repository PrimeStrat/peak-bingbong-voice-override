using System;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

/// <summary>Harmony prefix for AudioSource.PlayOneShot; replaces one-shot audio on Bing Bong objects with a random custom clip routed through the plugin source so it survives drops.</summary>
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new Type[] { typeof(AudioClip) })]
internal static class BingBongOneShotPatch
{
    /// <summary>Suppresses the original PlayOneShot and starts a custom clip on the plugin source instead, so playback continues after Bing Bong is dropped.</summary>
    /// <param name="__instance">The AudioSource being patched.</param>
    /// <param name="clip">The clip passed to PlayOneShot.</param>
    /// <returns>False to skip the original method when intercepted, otherwise true.</returns>
    [HarmonyPrefix]
    private static bool Prefix(AudioSource __instance, AudioClip clip)
    {
        if (Plugin.CustomClips.Count == 0) return true;
        if (Plugin.GetActiveClips().Count == 0) return true;
        if (BingBongHelper.IsPluginOwnedSource(__instance)) return true;
        if (!BingBongHelper.IsBingBongSource(__instance)) return true;

        AudioClip replacement = BingBongHelper.PickReplacementClip(clip);
        Plugin.PlayDetachedFromBingBong(replacement, __instance);
        Plugin.Log.LogInfo($"[Override] PlayOneShot (detached) -> {replacement.name}");
        return false;
    }
}

/// <summary>Harmony prefix for AudioSource.Play; redirects clip-property-based playback on Bing Bong objects to a random custom clip routed through the plugin source so it survives drops.</summary>
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new Type[] { })]
internal static class BingBongPlayPatch
{
    /// <summary>Suppresses the original Play and starts a custom clip on the plugin source instead, so playback continues after Bing Bong is dropped.</summary>
    /// <param name="__instance">The AudioSource being patched.</param>
    /// <returns>False to skip the original method when intercepted, otherwise true.</returns>
    [HarmonyPrefix]
    private static bool Prefix(AudioSource __instance)
    {
        if (Plugin.CustomClips.Count == 0) return true;
        if (Plugin.GetActiveClips().Count == 0) return true;
        if (BingBongHelper.IsPluginOwnedSource(__instance)) return true;
        if (!BingBongHelper.IsBingBongSource(__instance)) return true;

        AudioClip replacement = BingBongHelper.PickReplacementClip(__instance.clip);
        Plugin.PlayDetachedFromBingBong(replacement, __instance);
        Plugin.Log.LogInfo($"[Override] Play (detached) -> {replacement.name}");
        return false;
    }
}

/// <summary>Shared helpers used by the Bing Bong audio patches.</summary>
internal static class BingBongHelper
{
    private static readonly string[] BingBongNames =
    {
        "BingBong",
        "Bing Bong",
        "bing_bong",
        "bingbong",
    };

    /// <summary>Returns true when the source or any ancestor game object name matches a known Bing Bong identifier.</summary>
    /// <param name="source">The AudioSource to check.</param>
    /// <returns>bool</returns>
    internal static bool IsBingBongSource(AudioSource source)
    {
        return FindBingBongRoot(source) != null;
    }

    /// <summary>Walks up from a source's transform and returns the highest ancestor whose name matches a known Bing Bong identifier.</summary>
    /// <param name="source">The AudioSource to inspect.</param>
    /// <returns>The Bing Bong root transform, or null when not a Bing Bong source.</returns>
    internal static UnityEngine.Transform? FindBingBongRoot(AudioSource source)
    {
        if (source == null) return null;
        UnityEngine.Transform t = source.transform;
        UnityEngine.Transform? best = null;
        while (t != null)
        {
            string name = t.gameObject.name;
            for (int i = 0; i < BingBongNames.Length; i++)
            {
                if (name.IndexOf(BingBongNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = t;
                    break;
                }
            }
            t = t.parent;
        }
        return best;
    }

    /// <summary>Returns true when the AudioSource belongs to the plugin's own GameObject (used for music/auto-play).</summary>
    /// <param name="source">The AudioSource to check.</param>
    /// <returns>bool</returns>
    internal static bool IsPluginOwnedSource(AudioSource source)
    {
        if (Plugin.Instance == null) return false;
        return source != null && source.gameObject == Plugin.Instance.gameObject;
    }

    /// <summary>Returns the next clip for an override pick: forced clip if set, otherwise a uniformly random enabled clip.</summary>
    /// <returns>AudioClip</returns>
    internal static AudioClip RandomClip()
    {
        if (!string.IsNullOrEmpty(Plugin.ForcedNextClipName))
        {
            string forced = Plugin.ForcedNextClipName;
            Plugin.ForcedNextClipName = string.Empty;
            for (int i = 0; i < Plugin.CustomClips.Count; i++)
            {
                if (Plugin.CustomClips[i].name.Equals(forced, StringComparison.OrdinalIgnoreCase))
                    return Plugin.CustomClips[i];
            }
        }
        System.Collections.Generic.List<AudioClip> active = Plugin.GetActiveClips();
        return active[UnityEngine.Random.Range(0, active.Count)];
    }

    /// <summary>Uses the incoming clip when it is one of our managed custom clips; otherwise picks a random enabled custom clip.</summary>
    /// <param name="incoming">Original clip from the Bing Bong source.</param>
    /// <returns>Custom clip to play.</returns>
    internal static AudioClip PickReplacementClip(AudioClip incoming)
    {
        if (incoming != null)
        {
            for (int i = 0; i < Plugin.CustomClips.Count; i++)
            {
                if (Plugin.CustomClips[i] == incoming)
                    return incoming;
            }
        }
        return RandomClip();
    }
}

