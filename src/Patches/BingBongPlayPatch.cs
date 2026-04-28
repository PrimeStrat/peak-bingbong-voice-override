using BingBongVoiceOverride.Handlers;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Play), [])]
internal static class BingBongPlayPatch
{
    [HarmonyPrefix]
    private static bool Prefix(AudioSource __instance)
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

        AudioClip? replacement = BingBongAudioHandler.PickReplacementClip(__instance.clip);
        if (replacement == null)
        {
            return true;
        }
        Plugin.PlayDetachedFromBingBong(replacement, __instance);
        Plugin.Log.LogInfo($"[Override] Play (detached) -> {replacement.name}");
        return false;
    }
}
