using System;
using BingBongVoiceOverride.Handlers;
using HarmonyLib;
using UnityEngine;

namespace BingBongVoiceOverride.Patches;

[HarmonyPatch(typeof(GameObject))]
internal static class AskBingBongAddComponentPatch {
    [HarmonyPatch(nameof(GameObject.AddComponent), new Type[] { typeof(Type) })]
    [HarmonyPostfix]
    private static void AddComponentPostfix(GameObject __instance, ref Component __result) {
        if (!Plugin.UseNativeBingBongAPI.Value) {
            return;
        }

        AskBingBongResponseHandler.HandleCreatedObject((UnityEngine.Object)__result ?? __instance);
    }
}
