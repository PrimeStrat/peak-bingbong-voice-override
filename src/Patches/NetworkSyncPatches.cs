using System;
using System.Net;
using System.Reflection;
using HarmonyLib;

namespace BingBongVoiceOverride.Patches;

/// <summary>Hooks PEAK's session-join events via reflection so the sound sync server starts on the host and clients pull files automatically when joining.</summary>
internal static class NetworkSyncPatches
{
    private static bool _applied = false;

    private static readonly string[] SessionTypeNames =
    {
        "SteamLobbyHandler",
        "LobbyManager",
        "NetworkSessionManager",
    };

    private static readonly string[] JoinMethodNames =
    {
        "OnJoinedLobby",
        "OnJoinedRoom",
        "OnSessionJoined",
    };

    /// <summary>Attempts to patch PEAK's session-join method using reflection. Falls back silently when types are not found.</summary>
    /// <param name="harmony">Harmony instance to register patches with.</param>
    /// <returns>void</returns>
    internal static void TryApply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;

        foreach (string typeName in SessionTypeNames)
        {
            Type sessionType = AccessTools.TypeByName(typeName);
            if (sessionType == null) continue;

            foreach (string methodName in JoinMethodNames)
            {
                MethodInfo method = AccessTools.Method(sessionType, methodName);
                if (method == null) continue;

                try
                {
                    harmony.Patch(method,
                        postfix: new HarmonyMethod(
                            typeof(NetworkSyncPatches),
                            nameof(OnSessionJoinedPostfix)));

                    Plugin.Log.LogInfo($"[NetworkSync] Patched {typeName}.{methodName} for auto-sync.");
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[NetworkSync] Failed to patch {typeName}.{methodName}: {ex.Message}");
                }
            }
        }

        Plugin.Log.LogWarning("[NetworkSync] No session-join method found automatically. Use manual sync in the Network tab.");
    }

    private static void OnSessionJoinedPostfix(object __instance)
    {
        try
        {
            string hostIp = TryExtractHostIp(__instance) ?? string.Empty;

            bool isHost = TryIsHost(__instance);
            if (isHost)
            {
                Plugin.Log.LogInfo("[NetworkSync] Local player is host; ensuring server is started.");
                BingBongNetworkSync.StartServer();
                return;
            }

            if (string.IsNullOrWhiteSpace(hostIp))
            {
                Plugin.Log.LogWarning("[NetworkSync] Joined session but could not determine host IP. Use manual sync in the Network tab.");
                return;
            }

            Plugin.Log.LogInfo($"[NetworkSync] Joined session as client; syncing from host {hostIp}.");
            BingBongNetworkSync.OnPlayerJoined(hostIp);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NetworkSync] OnSessionJoinedPostfix error: {ex.Message}");
        }
    }

    private static string? TryExtractHostIp(object instance)
    {
        if (instance == null) return null;
        Type t = instance.GetType();

        FieldInfo? direct = AccessTools.Field(t, "hostAddress") ?? AccessTools.Field(t, "serverAddress");
        if (direct != null)
        {
            string val = direct.GetValue(direct.IsStatic ? null : instance) as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(val) && IsLikelyIp(val)) return val;
        }

        FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        foreach (FieldInfo f in fields)
        {
            if (f.FieldType != typeof(string)) continue;
            string lower = f.Name.ToLowerInvariant();
            if (!lower.Contains("host") && !lower.Contains("server") && !lower.Contains("address")) continue;
            string val = f.GetValue(f.IsStatic ? null : instance) as string ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(val) && IsLikelyIp(val)) return val;
        }

        return null;
    }

    private static bool TryIsHost(object instance)
    {
        if (instance == null) return false;
        Type t = instance.GetType();

        FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        foreach (FieldInfo f in fields)
        {
            if (f.FieldType != typeof(bool)) continue;
            string lower = f.Name.ToLowerInvariant();
            if (!lower.Contains("host") && !lower.Contains("server") && !lower.Contains("master")) continue;
            return (bool)f.GetValue(f.IsStatic ? null : instance);
        }
        return false;
    }

    private static bool IsLikelyIp(string value)
    {
        return value.IndexOf('.') > 0
            && IPAddress.TryParse(value, out _)
            && !value.Equals("127.0.0.1");
    }
}
