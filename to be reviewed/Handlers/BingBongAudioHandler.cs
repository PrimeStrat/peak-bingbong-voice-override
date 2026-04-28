using System;
using System.Collections.Generic;
using UnityEngine;

namespace BingBongVoiceOverride.Handlers;

internal static class BingBongAudioHandler {
    private static readonly string[] BingBongNames = [
        "BingBong",
        "Bing Bong",
        "bing_bong",
        "bingbong",
    ];

    internal static bool IsBingBongSource(AudioSource source) {
        return FindBingBongRoot(source) != null;
    }

    internal static Transform? FindBingBongRoot(AudioSource source) {
        if (source == null) {
            return null;
        }

        Transform current = source.transform;
        Transform? best = null;
        while (current != null) {
            string name = current.gameObject.name;
            for (int i = 0; i < BingBongNames.Length; i++) {
                if (name.IndexOf(BingBongNames[i], StringComparison.OrdinalIgnoreCase) >= 0) {
                    best = current;
                    break;
                }
            }

            current = current.parent;
        }

        return best;
    }

    internal static bool IsPluginOwnedSource(AudioSource source) {
        if (Plugin.Instance == null) {
            return false;
        }

        return source != null && source.gameObject == Plugin.Instance.gameObject;
    }

    internal static AudioClip PickReplacementClip(AudioClip incoming) {
        if (incoming != null) {
            for (int i = 0; i < Plugin.CustomClips.Count; i++) {
                if (Plugin.CustomClips[i] == incoming) {
                    return incoming;
                }
            }
        }

        return PickRandomActiveClip();
    }

    private static AudioClip PickRandomActiveClip() {
        if (!string.IsNullOrEmpty(Plugin.ForcedNextClipName)) {
            string forced = Plugin.ForcedNextClipName;
            Plugin.ForcedNextClipName = string.Empty;
            for (int i = 0; i < Plugin.CustomClips.Count; i++) {
                if (Plugin.CustomClips[i].name.Equals(forced, StringComparison.OrdinalIgnoreCase)) {
                    return Plugin.CustomClips[i];
                }
            }
        }

        List<AudioClip> active = Plugin.GetActiveClips();
        return active[UnityEngine.Random.Range(0, active.Count)];
    }
}
