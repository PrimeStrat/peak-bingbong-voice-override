using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace BingBongVoiceOverride.Patches;

// Hooks PEAK's Photon-based player-join and player-leave events to start the sound sync server
// on the host and automatically download files on clients when joining a room.
internal static class NetworkSyncPatches
{
    private static bool _applied = false;

    // True when the local player clicked Play to create the lobby. Set from MainMenuMainPage.PlayClicked
    // because PhotonNetwork.IsMasterClient is unreliable inside PlayerConnectionLog callbacks.
    internal static bool LocalPlayerCreatedLobby = false;

    // True while the local player is inside a Photon room. Set in OnJoinedRoom; cleared in OnLeftRoom.
    internal static bool IsInRoom = false;

    internal static void TryApply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;

        Type? logType = AccessTools.TypeByName("PlayerConnectionLog");
        if (logType == null)
        {
            Plugin.Log.LogWarning("[NetworkSync] PlayerConnectionLog type not found. Player auto-detect disabled.");
        }
        else
        {
            TryPatch(harmony, logType, "OnPlayerEnteredRoom", nameof(OnPlayerEnteredRoomPostfix));
            TryPatch(harmony, logType, "OnPlayerLeftRoom", nameof(OnPlayerLeftRoomPostfix));
            TryPatch(harmony, logType, "OnJoinedRoom", nameof(OnJoinedRoomPostfix));
            TryPatch(harmony, logType, "OnLeftRoom", nameof(OnLeftRoomPostfix));
        }

        // Track lobby-host intent up front because IsMasterClient lies inside the join callbacks.
        Type? menuType = AccessTools.TypeByName("MainMenuMainPage");
        if (menuType != null)
            TryPatch(harmony, menuType, "PlayClicked", nameof(PlayClickedPostfix));
    }

    private static void TryPatch(Harmony harmony, Type type, string methodName, string postfixName)
    {
        try
        {
            MethodInfo? method = AccessTools.Method(type, methodName);
            if (method == null)
            {
                Plugin.Log.LogWarning($"[NetworkSync] {type.Name}.{methodName} not found.");
                return;
            }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(NetworkSyncPatches), postfixName));
            Plugin.Log.LogInfo($"[NetworkSync] Patched {type.Name}.{methodName}.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NetworkSync] Failed to patch {type.Name}.{methodName}: {ex.Message}");
        }
    }

    private static void OnPlayerEnteredRoomPostfix(object __0)
    {
        try
        {
            string name = PhotonBridge.GetNickName(__0);
            bool host = LocalPlayerCreatedLobby;
            Plugin.Log.LogInfo($"[NetworkSync] Player entered room: '{name}'. LocalCreatedLobby={host}");
            if (host)
            {
                if (!BingBongNetworkSync.IsHosting)
                    BingBongNetworkSync.StartServer();
                BingBongNetworkSync.OnPhotonPlayerJoined(name);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NetworkSync] OnPlayerEnteredRoom error: {ex.Message}");
        }
    }

    private static void OnPlayerLeftRoomPostfix(object __0)
    {
        try
        {
            string name = PhotonBridge.GetNickName(__0);
            Plugin.Log.LogInfo($"[NetworkSync] Player left room: '{name}'.");
            BingBongNetworkSync.OnPhotonPlayerLeft(name);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NetworkSync] OnPlayerLeftRoom error: {ex.Message}");
        }
    }

    // IsMasterClient is unreliable inside join callbacks; use LocalPlayerCreatedLobby as the host signal.
    private static void OnJoinedRoomPostfix()
    {
        IsInRoom = true;
        try
        {
            if (LocalPlayerCreatedLobby)
            {
                if (BingBongNetworkSync.IsHosting) return;
                Plugin.Log.LogInfo($"[NetworkSync] Local player joined room as host.");
                BingBongNetworkSync.StartServer();
                foreach (string name in PhotonBridge.GetOtherPlayerNames())
                    BingBongNetworkSync.OnPhotonPlayerJoined(name);
            }
            else
            {
                if (BingBongNetworkSync.IsConnectedAsClient) return;
                Plugin.Log.LogInfo($"[NetworkSync] Local player joined room as client.");
                BingBongNetworkSync.OnPlayerJoined(string.Empty);
                UnifiedMenu.JoinToastUntil = UnityEngine.Time.unscaledTime + 8f;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NetworkSync] OnJoinedRoom error: {ex.Message}");
        }
    }

    private static void OnLeftRoomPostfix()
    {
        LocalPlayerCreatedLobby = false;
        IsInRoom = false;
        Plugin.Log.LogInfo("[NetworkSync] Left room; cleared LocalPlayerCreatedLobby.");
        BingBongNetworkSync.StopServer();
    }

    private static void PlayClickedPostfix()
    {
        LocalPlayerCreatedLobby = true;
        Plugin.Log.LogInfo("[NetworkSync] PlayClicked detected; this player will host the sync server.");
    }

    // Returns the local player's Photon NickName, or empty string if unavailable.
    // returns: string
    internal static string GetLocalPhotonNickName() => PhotonBridge.GetLocalNickName();

    // Reflection-based Photon PUN access without a hard DLL dependency.
    private static class PhotonBridge
    {
        private static Type? _pnType;
        private static Type? PN => _pnType ??= AccessTools.TypeByName("Photon.Pun.PhotonNetwork");

        private static object? GetProp(string name)
        {
            Type? t = PN;
            if (t == null) return null;
            return AccessTools.Property(t, name)?.GetValue(null);
        }

        internal static bool IsMasterClient => (bool)(GetProp("IsMasterClient") ?? false);

        internal static string GetLocalNickName()
        {
            object? local = GetProp("LocalPlayer");
            if (local == null) return string.Empty;
            return AccessTools.Property(local.GetType(), "NickName")?.GetValue(local) as string ?? string.Empty;
        }

        internal static string GetNickName(object? player)
        {
            if (player == null) return string.Empty;
            return AccessTools.Property(player.GetType(), "NickName")?.GetValue(player) as string ?? string.Empty;
        }

        // Returns NickName for every non-local player currently in the room.
        // returns: List<string>
        internal static List<string> GetOtherPlayerNames()
        {
            List<string> names = new();
            object? room = GetProp("CurrentRoom");
            if (room == null) return names;
            object? players = AccessTools.Property(room.GetType(), "Players")?.GetValue(room);
            if (players is not System.Collections.IDictionary dict) return names;

            // Use ActorNumber comparison to identify the local player -- more reliable than IsLocal reflection.
            object? localPlayer = GetProp("LocalPlayer");
            int localActor = localPlayer != null
                ? (int)(AccessTools.Property(localPlayer.GetType(), "ActorNumber")?.GetValue(localPlayer) ?? -1)
                : -1;

            foreach (object? val in dict.Values)
            {
                if (val == null) continue;
                int actorNum = (int)(AccessTools.Property(val.GetType(), "ActorNumber")?.GetValue(val) ?? -1);
                if (localActor >= 0 && actorNum == localActor) continue;
                string? name = AccessTools.Property(val.GetType(), "NickName")?.GetValue(val) as string;
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name!);
            }
            return names;
        }

        // Sets a string on the current Photon room's custom properties.
        // key (string): property key
        // value (string): property value
        // returns: void
        internal static void SetRoomProperty(string key, string value)
        {
            object? room = GetProp("CurrentRoom");
            if (room == null) return;
            Type? htType = AccessTools.TypeByName("ExitGames.Client.Photon.Hashtable");
            if (htType == null) return;
            object? ht = Activator.CreateInstance(htType);
            if (ht == null) return;
            AccessTools.Method(htType, "Add", new[] { typeof(object), typeof(object) })?.Invoke(ht, new object[] { key, value });

            // Search by name only to avoid type-identity mismatches when Photon assemblies are loaded
            // with different contexts. Fill remaining parameters with their defaults.
            foreach (MethodInfo m in room.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "SetCustomProperties") continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length < 1) continue;
                object?[] args = new object?[p.Length];
                args[0] = ht;
                for (int i = 1; i < p.Length; i++)
                    args[i] = p[i].HasDefaultValue ? p[i].DefaultValue
                        : (p[i].ParameterType.IsValueType ? Activator.CreateInstance(p[i].ParameterType) : null);
                m.Invoke(room, args);
                return;
            }

            Plugin.Log.LogWarning("[NetworkSync] SetCustomProperties overload not found on Room type.");
        }

        // Reads a string from the current Photon room's custom properties.
        // key (string): property key
        // returns: string?
        internal static string? GetRoomProperty(string key)
        {
            object? room = GetProp("CurrentRoom");
            if (room == null) return null;
            object? props = AccessTools.Property(room.GetType(), "CustomProperties")?.GetValue(room);
            if (props is not System.Collections.IDictionary dict) return null;
            return dict[key] as string;
        }
    }
}

// Reflection-based access to Photon's RaiseEvent transport. Lets the mod send and receive
// arbitrary binary blobs through the existing Photon connection so file transfers work over
// the internet without any LAN connectivity or firewall holes.
internal static class PhotonNet
{
    private static Type? _pnType;
    private static Type? PN => _pnType ??= AccessTools.TypeByName("Photon.Pun.PhotonNetwork")
        ?? AccessTools.TypeByName("PhotonNetwork");

    private static Type? _evDataType;
    private static Type? EvData => _evDataType ??= AccessTools.TypeByName("ExitGames.Client.Photon.EventData")
        ?? AccessTools.TypeByName("EventData");

    private static Type? _raiseOptsType;
    private static Type? RaiseOpts => _raiseOptsType ??= AccessTools.TypeByName("Photon.Realtime.RaiseEventOptions")
        ?? AccessTools.TypeByName("ExitGames.Client.Photon.RaiseEventOptions")
        ?? AccessTools.TypeByName("RaiseEventOptions");

    private static Type? _sendOptsType;
    private static Type? SendOpts => _sendOptsType ??= AccessTools.TypeByName("ExitGames.Client.Photon.SendOptions")
        ?? AccessTools.TypeByName("SendOptions");

    private static Type? _receiverGroupType;
    private static Type? ReceiverGroup => _receiverGroupType ??= AccessTools.TypeByName("Photon.Realtime.ReceiverGroup")
        ?? AccessTools.TypeByName("ExitGames.Client.Photon.Lite.ReceiverGroup")
        ?? AccessTools.TypeByName("ReceiverGroup");

    private static MethodInfo? _raiseEventMethod;
    private static MemberInfo? _evCodeMember;
    private static MemberInfo? _evCustomDataMember;
    private static MemberInfo? _evSenderMember;

    private static MemberInfo? FindMember(Type t, string name)
    {
        BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo? f = t.GetField(name, flags);
        if (f != null) return f;
        return t.GetProperty(name, flags);
    }

    private static object? ReadMember(MemberInfo? m, object target)
    {
        if (m is FieldInfo f) return f.GetValue(target);
        if (m is PropertyInfo p) return p.GetValue(target);
        return null;
    }

    private static bool _subscribed = false;
    private static Delegate? _eventDelegate;
    private static object? _subscribedClient;
    private static EventInfo? _subscribedEvent;
    private static Action<byte, object?, int>? _userHandler;

    // True when Photon's runtime types are reachable via reflection.
    internal static bool IsAvailable
    {
        get
        {
            bool ok = PN != null && EvData != null && RaiseOpts != null && SendOpts != null;
            if (!_loggedAvailability)
            {
                _loggedAvailability = true;
                Plugin.Log.LogInfo($"[PhotonNet] Reflection bindings: PN={PN?.FullName ?? "null"} EvData={EvData?.FullName ?? "null"} RaiseOpts={RaiseOpts?.FullName ?? "null"} SendOpts={SendOpts?.FullName ?? "null"} Receivers={ReceiverGroup?.FullName ?? "null"}");
            }
            return ok;
        }
    }
    private static bool _loggedAvailability = false;

    // Returns the local player's Photon actor number, or -1 if not in a room.
    internal static int LocalActorNumber
    {
        get
        {
            try
            {
                object? local = AccessTools.Property(PN!, "LocalPlayer")?.GetValue(null);
                if (local == null) return -1;
                return (int)(AccessTools.Property(local.GetType(), "ActorNumber")?.GetValue(local) ?? -1);
            }
            catch { return -1; }
        }
    }

    // Returns the master client actor number, or -1 if no room.
    internal static int MasterActorNumber
    {
        get
        {
            try
            {
                object? master = AccessTools.Property(PN!, "MasterClient")?.GetValue(null);
                if (master == null) return -1;
                return (int)(AccessTools.Property(master.GetType(), "ActorNumber")?.GetValue(master) ?? -1);
            }
            catch { return -1; }
        }
    }

    // Returns the NickName of the player with the given actor number, or empty string if not found.
    internal static string GetNickNameForActor(int actorNumber)
    {
        try
        {
            object? room = AccessTools.Property(PN!, "CurrentRoom")?.GetValue(null);
            if (room == null) return string.Empty;
            object? players = AccessTools.Property(room.GetType(), "Players")?.GetValue(room);
            if (players is not System.Collections.IDictionary dict) return string.Empty;
            foreach (object? val in dict.Values)
            {
                if (val == null) continue;
                int an = (int)(AccessTools.Property(val.GetType(), "ActorNumber")?.GetValue(val) ?? -1);
                if (an == actorNumber)
                    return AccessTools.Property(val.GetType(), "NickName")?.GetValue(val) as string ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    // Subscribes a single application-level event handler. Replaces any previous subscription.
    // handler (Action<byte, object?, int>): receives (eventCode, customData, senderActorNumber)
    internal static void Subscribe(Action<byte, object?, int> handler)
    {
        _userHandler = handler;
        if (_subscribed) return;
        try
        {
            // NetworkingClient is a static FIELD in PUN2, not a property. Use FindMember with Static flag.
            BindingFlags staticFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo? ncField = PN!.GetField("NetworkingClient", staticFlags);
            PropertyInfo? ncProp = PN!.GetProperty("NetworkingClient", staticFlags);
            object? client = ncField?.GetValue(null) ?? ncProp?.GetValue(null);
            if (client == null)
            {
                Plugin.Log.LogWarning($"[PhotonNet] NetworkingClient unavailable; cannot subscribe. (field={ncField != null}, prop={ncProp != null})");
                return;
            }
            EventInfo? ev = client.GetType().GetEvent("EventReceived");
            if (ev == null || ev.EventHandlerType == null)
            {
                Plugin.Log.LogWarning("[PhotonNet] EventReceived event not found on NetworkingClient.");
                return;
            }
            _evCodeMember ??= FindMember(EvData!, "Code");
            _evCustomDataMember ??= FindMember(EvData!, "CustomData");
            _evSenderMember ??= FindMember(EvData!, "Sender");
            if (_evCodeMember == null || _evCustomDataMember == null || _evSenderMember == null)
            {
                Plugin.Log.LogWarning($"[PhotonNet] EventData layout unexpected: Code={_evCodeMember != null} Custom={_evCustomDataMember != null} Sender={_evSenderMember != null}.");
            }

            // Build a dynamic trampoline: void(EventData) -> calls our static dispatch with object boxing.
            ParameterInfo[] invokeParams = ev.EventHandlerType.GetMethod("Invoke")!.GetParameters();
            DynamicMethod dm = new("BBVOPhotonEvTrampoline", typeof(void),
                [invokeParams[0].ParameterType], typeof(PhotonNet).Module, true);
            ILGenerator il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(PhotonNet).GetMethod(nameof(DispatchEvent), BindingFlags.NonPublic | BindingFlags.Static)!);
            il.Emit(OpCodes.Ret);
            _eventDelegate = dm.CreateDelegate(ev.EventHandlerType);
            ev.AddEventHandler(client, _eventDelegate);
            _subscribedClient = client;
            _subscribedEvent = ev;
            _subscribed = true;
            Plugin.Log.LogInfo("[PhotonNet] Subscribed to Photon EventReceived.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PhotonNet] Subscribe failed: {ex.Message}");
        }
    }

    internal static void Unsubscribe()
    {
        if (!_subscribed) return;
        try
        {
            if (_subscribedEvent != null && _subscribedClient != null && _eventDelegate != null)
                _subscribedEvent.RemoveEventHandler(_subscribedClient, _eventDelegate);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PhotonNet] Unsubscribe failed: {ex.Message}");
        }
        _subscribed = false;
        _subscribedClient = null;
        _subscribedEvent = null;
        _eventDelegate = null;
        _userHandler = null;
    }

    // Sends an event to every other player in the room (reliable).
    internal static void SendToOthers(byte code, object? data) => RaiseEvent(code, data, null, true, false);

    // Sends an event to the master client only (reliable).
    internal static void SendToMaster(byte code, object? data) => RaiseEvent(code, data, null, true, true);

    // Sends an event to a single actor by actor number (reliable).
    internal static void SendToActor(byte code, object? data, int actorNumber) =>
        RaiseEvent(code, data, [actorNumber], true, false);

    // Internal trampoline target invoked by the dynamic delegate; unboxes the EventData.
    private static void DispatchEvent(object eventData)
    {
        if (_userHandler == null || eventData == null) return;
        try
        {
            byte code = Convert.ToByte(ReadMember(_evCodeMember, eventData) ?? (byte)0);
            object? content = ReadMember(_evCustomDataMember, eventData);
            int sender = Convert.ToInt32(ReadMember(_evSenderMember, eventData) ?? -1);
            // Only log our own event range to avoid spamming on every game packet.
            if (code >= 173 && code <= 180)
                Plugin.Log.LogInfo($"[PhotonNet] <- event {code} from actor {sender}");
            _userHandler(code, content, sender);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PhotonNet] Dispatch error: {ex.Message}");
        }
    }

    private static void RaiseEvent(byte code, object? data, int[]? targetActors, bool reliable, bool toMasterOnly)
    {
        try
        {
            if (PN == null || RaiseOpts == null || SendOpts == null)
            {
                Plugin.Log.LogWarning($"[PhotonNet] RaiseEvent({code}) skipped: PN={PN != null} Opts={RaiseOpts != null} Send={SendOpts != null}.");
                return;
            }
            if (_raiseEventMethod == null)
            {
                foreach (MethodInfo m in PN.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "RaiseEvent") continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 4 && p[0].ParameterType == typeof(byte))
                    {
                        _raiseEventMethod = m;
                        break;
                    }
                }
            }
            if (_raiseEventMethod == null)
            {
                Plugin.Log.LogWarning("[PhotonNet] PhotonNetwork.RaiseEvent overload not found.");
                return;
            }

            object? opts = Activator.CreateInstance(RaiseOpts);
            if (opts == null) return;
            if (targetActors != null)
            {
                FieldInfo? f = RaiseOpts.GetField("TargetActors");
                if (f != null) f.SetValue(opts, targetActors);
            }
            else if (toMasterOnly && ReceiverGroup != null)
            {
                FieldInfo? f = RaiseOpts.GetField("Receivers");
                if (f != null) f.SetValue(opts, Enum.ToObject(ReceiverGroup, (byte)2));
            }
            else if (ReceiverGroup != null)
            {
                FieldInfo? f = RaiseOpts.GetField("Receivers");
                if (f != null) f.SetValue(opts, Enum.ToObject(ReceiverGroup, (byte)0));
            }

            FieldInfo? sendField = SendOpts.GetField(reliable ? "SendReliable" : "SendUnreliable",
                BindingFlags.Public | BindingFlags.Static);
            object send = sendField != null ? sendField.GetValue(null)! : Activator.CreateInstance(SendOpts)!;

            object? rv = _raiseEventMethod.Invoke(null, [code, data, opts, send]);
            if (code >= 173 && code <= 180)
            {
                string target = targetActors != null ? $"actor {targetActors[0]}"
                    : toMasterOnly ? "master" : "others";
                Plugin.Log.LogInfo($"[PhotonNet] -> event {code} to {target} (returned {rv})");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PhotonNet] RaiseEvent({code}) failed: {ex.Message}");
        }
    }
}
