using System;
using BingBongVoiceOverride.Handlers;
using HarmonyLib;

namespace BingBongVoiceOverride.Patches;

[HarmonyPatch(typeof(UnityEngine.Object))]
internal static class AskBingBongInstantiatePatch {
    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), new Type[] { typeof(UnityEngine.Object) })]
    [HarmonyPostfix]
    private static void InstantiatePostfix(UnityEngine.Object __result) {
        if (!Plugin.UseNativeBingBongAPI.Value) {
            return;
        }

        AskBingBongResponseHandler.HandleCreatedObject(__result);
    }
}
