using System;
using HarmonyLib;
using UnityEngine;
namespace BingBongVoiceOverride.Patches;

// Replaces PlayOneShot on Bing Bong audio sources with a custom clip routed through the plugin source.
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), new Type[] { typeof(AudioClip) })]
internal static class BingBongOneShotPatch
{
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

// Replaces Play on Bing Bong audio sources with a custom clip routed through the plugin source.
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), new Type[] { })]
internal static class BingBongPlayPatch
{
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

// Shared helpers used by the Bing Bong audio patches.
internal static class BingBongHelper
{
    private static readonly string[] BingBongNames =
    {
        "BingBong",
        "Bing Bong",
        "bing_bong",
        "bingbong",
    };

    // Returns true when the source or any ancestor game object name matches a known Bing Bong identifier.
    // source (AudioSource): the AudioSource to check
    // returns: bool
    internal static bool IsBingBongSource(AudioSource source)
    {
        return FindBingBongRoot(source) != null;
    }

    // Walks up from a source's transform and returns the highest ancestor matching a Bing Bong name.
    // source (AudioSource): the AudioSource to inspect
    // returns: Transform? - the Bing Bong root transform, or null
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

    // Returns true when the AudioSource belongs to the plugin's own GameObject.
    // source (AudioSource): the AudioSource to check
    // returns: bool
    internal static bool IsPluginOwnedSource(AudioSource source)
    {
        if (Plugin.Instance == null) return false;
        return source != null && source.gameObject == Plugin.Instance.gameObject;
    }

    // Returns the next clip for an override pick: forced clip if set, otherwise a uniformly random enabled clip.
    // returns: AudioClip
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

    // Uses the incoming clip when it is one of our managed custom clips; otherwise picks a random enabled custom clip.
    // incoming (AudioClip): original clip from the Bing Bong source
    // returns: AudioClip
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

