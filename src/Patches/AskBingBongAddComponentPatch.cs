using System;
using BingBongVoiceOverride.Handlers;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

[HarmonyPatch(typeof(GameObject))]
internal static class AskBingBongAddComponentPatch
{
    [HarmonyPatch(nameof(GameObject.AddComponent), [typeof(Type)])]
    [HarmonyPostfix]
    private static void AddComponentPostfix(GameObject __instance, ref Component __result)
    {
        if (Plugin.UseNativeBingBongAPI.Value != true)
        {
            return;
        }

        UnityEngine.Object created = __result != null ? __result : __instance;
        AskBingBongResponseHandler.HandleCreatedObject(created);
    }
}
