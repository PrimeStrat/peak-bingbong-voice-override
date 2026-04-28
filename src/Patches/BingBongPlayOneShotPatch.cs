using BingBongVoiceOverride.Handlers;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), [typeof(AudioClip)])]
internal static class BingBongPlayOneShotPatch
{
    [HarmonyPrefix]
    private static bool Prefix(AudioSource __instance, AudioClip clip)
    {
        if (Plugin.CustomClips.Count == 0)
        {
            return true;
        }

        if (Plugin.GetActiveClips().Count == 0)
        {
            return true;
        }

        if (BingBongAudioHandler.IsPluginOwnedSource(__instance))
        {
            return true;
        }

        if (!BingBongAudioHandler.IsBingBongSource(__instance))
        {
            return true;
        }

        AudioClip? replacement = BingBongAudioHandler.PickReplacementClip(clip);
        if (replacement == null)
        {
            return true;
        }
        Plugin.PlayDetachedFromBingBong(replacement, __instance);
        Plugin.Log.LogInfo($"[Override] PlayOneShot (detached) -> {replacement.name}");
        return false;
    }
}
