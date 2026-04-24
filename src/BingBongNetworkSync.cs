using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
namespace BingBongVoiceOverride;

// Photon-based sound sync. The master client (host) serves audio files; non-master clients
// pull missing files. All transport rides on Photon RaiseEvent so it works through Photon's
// relay just like normal game traffic - no LAN, no HTTP server, no firewall holes required.
internal static class BingBongNetworkSync
{
    private const byte EV_REQ_LIST = 173;
    private const byte EV_FILE_LIST = 174;
    private const byte EV_REQ_FILE = 175;
    private const byte EV_FILE_CHUNK = 176;
    private const byte EV_CONFIRM_HAVE = 177;
    private const byte EV_SIGNAL = 178;
    private const byte EV_REQ_SEL = 179;
    private const byte EV_SELECTION = 180;

    private const byte SIG_REFRESH = 1;
    private const byte SIG_STOP = 2;
    private const byte SIG_PLAY = 3;
    private const byte SIG_PAUSE = 4;
    private const byte SIG_RESUME = 5;
    private const byte SIG_FORCE_NEXT = 6;
    private const byte SIG_SUBTITLE = 7;
    private const byte SIG_CLIP_ENABLED = 8;
    private const byte SIG_PERMISSIONS = 9;

    private const int CHUNK_SIZE = 64 * 1024;

    // Human-readable transport state shown in menus and overlay.
    internal static string StatusText = "idle";

    // True when the local player is the master client and acting as the sound host.
    internal static bool IsHosting => _running;

    // True when the local player is in a room as a non-master client and has been linked to the host.
    internal static bool IsConnectedAsClient => !_running && _hostJoined;

    // True when the host allows clients to upload new sounds, mirrored from host status broadcasts.
    internal static bool HostAllowsClientImports = false;

    // Reserved for menu code that previously displayed the host IP. Always empty under Photon transport.
    internal static string ActiveHostAddress => _hostJoined ? "photon" : string.Empty;

    // True after the client has received any event from the host this session.
    internal static bool HasReachedHost => _hasReachedHost;

    // True when the most recent client sync attempt failed and is awaiting retry.
    internal static bool ClientSyncFailed => _syncFailed;

    // True while a client sync is downloading or the host is waiting for a client to confirm files.
    internal static bool IsSyncBusy =>
        _clientSyncRunning ||
        (IsHosting && _lobbyPlayerNames.Count > 0 && GetUnsyncedPlayerNames().Count > 0);

    private static bool _running = false;
    private static bool _hostJoined = false;
    private static bool _clientSyncRunning = false;
    private static bool _syncFailed = false;
    private static bool _hasReachedHost = false;
    internal static bool ClientAllowPlayback = false;
    internal static bool ClientAllowSubtitleEdit = false;
    internal static bool ClientAllowSelectionEdit = false;
    internal static bool ClientAllowSettingsChange = false;

    private static readonly object _syncLock = new();

    // Lobby (host-side) tracking by Photon NickName.
    private static readonly HashSet<string> _lobbyPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _playerSyncedFileNames =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-actor download tracking (host-side).
    private static readonly HashSet<int> _knownActors = new();
    private static readonly Dictionary<int, string> _actorNames = new();
    private static readonly Dictionary<int, HashSet<string>> _actorDownloadedFiles = new();
    private static readonly Dictionary<string, HashSet<string>> _pendingImportedFileClients =
        new(StringComparer.OrdinalIgnoreCase);

    // Incoming chunk buffers keyed by "senderActor:fileName".
    private static readonly Dictionary<string, byte[][]> _chunkBuffers = new();
    private static readonly Dictionary<string, int> _chunkExpected = new();

    // Client-side file list received from the host.
    private static List<(string name, long size)>? _hostFileList = null;
    // Files the client is still waiting to download (set populated when the file list arrives).
    private static readonly HashSet<string> _clientPendingFiles = new(StringComparer.OrdinalIgnoreCase);
    // Selection JSON bytes most recently received from the host (consumed by the sync coroutine).
    private static byte[]? _pendingSelectionBytes = null;

    // Subscribes to Photon events and marks this client as the sound host.
    // returns: void
    internal static void StartServer()
    {
        if (_running) return;
        if (!Patches.PhotonNet.IsAvailable)
        {
            StatusText = "photon unavailable";
            Plugin.Log.LogWarning("[Sync] Photon runtime not found; sync disabled.");
            return;
        }
        _running = true;
        Patches.PhotonNet.Subscribe(OnPhotonEvent);
        StatusText = "hosting (photon)";
        Plugin.Log.LogInfo("[Sync] Photon sync host active.");
    }

    // Tears down host state and unsubscribes from Photon events.
    // returns: void
    internal static void StopServer()
    {
        _running = false;
        _hostJoined = false;
        _hasReachedHost = false;
        StatusText = "idle";
        Patches.PhotonNet.Unsubscribe();
        lock (_syncLock)
        {
            _lobbyPlayerNames.Clear();
            _playerSyncedFileNames.Clear();
            _knownActors.Clear();
            _actorNames.Clear();
            _actorDownloadedFiles.Clear();
            _pendingImportedFileClients.Clear();
            _chunkBuffers.Clear();
            _chunkExpected.Clear();
            _clientPendingFiles.Clear();
            _hostFileList = null;
            _pendingSelectionBytes = null;
        }
    }

    // Tells every connected client to clear timed subtitle state.
    // returns: void
    internal static void BroadcastStop()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_STOP });
    }

    // Broadcasts a play command for a specific clip to all other players.
    // clipName (string): clip name without extension
    // returns: void
    internal static void BroadcastPlay(string clipName)
    {
        if (!_running) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] payload = new byte[1 + nameBytes.Length];
        payload[0] = SIG_PLAY;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts a pause command to all other players. returns: void
    internal static void BroadcastPause()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_PAUSE });
    }

    // Broadcasts a resume command to all other players. returns: void
    internal static void BroadcastResume()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_RESUME });
    }

    // Broadcasts a force-next clip command to all other players.
    // clipName (string): clip name without extension to set as the forced next pick
    // returns: void
    internal static void BroadcastForceNext(string clipName)
    {
        if (!_running) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] payload = new byte[1 + nameBytes.Length];
        payload[0] = SIG_FORCE_NEXT;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts a subtitle override change to all other players. Usable by host or a permitted client.
    // clipName (string): clip name without extension
    // subtitle (string): new subtitle text, empty to clear
    // returns: void
    internal static void BroadcastSubtitleUpdate(string clipName, string subtitle)
    {
        if (!Patches.PhotonNet.IsAvailable) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] subtitleBytes = Encoding.UTF8.GetBytes(subtitle ?? string.Empty);
        byte[] payload = new byte[1 + nameBytes.Length + 1 + subtitleBytes.Length];
        payload[0] = SIG_SUBTITLE;
        Buffer.BlockCopy(nameBytes, 0, payload, 1, nameBytes.Length);
        payload[1 + nameBytes.Length] = 0;
        Buffer.BlockCopy(subtitleBytes, 0, payload, 1 + nameBytes.Length + 1, subtitleBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts a clip enabled/disabled state change to all other players. Usable by host or a permitted client.
    // clipName (string): clip name without extension
    // enabled (bool): new enabled state
    // returns: void
    internal static void BroadcastClipEnabled(string clipName, bool enabled)
    {
        if (!Patches.PhotonNet.IsAvailable) return;
        byte[] nameBytes = Encoding.UTF8.GetBytes(clipName ?? string.Empty);
        byte[] payload = new byte[2 + nameBytes.Length];
        payload[0] = SIG_CLIP_ENABLED;
        payload[1] = enabled ? (byte)1 : (byte)0;
        Buffer.BlockCopy(nameBytes, 0, payload, 2, nameBytes.Length);
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, payload);
    }

    // Broadcasts the current host permission flags to all connected players. Host only.
    // returns: void
    internal static void BroadcastPermissions()
    {
        if (!_running) return;
        byte perms = 0;
        if (Plugin.AllowClientPlayback.Value) perms |= 1;
        if (Plugin.AllowClientSubtitleEdit.Value) perms |= 2;
        if (Plugin.AllowClientSelectionEdit.Value) perms |= 4;
        if (Plugin.AllowClientSettingsChange.Value) perms |= 8;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_PERMISSIONS, perms });
    }

    // Tells every connected client to re-pull files and refresh.
    // returns: void
    internal static void BroadcastRefresh()
    {
        if (!_running) return;
        Patches.PhotonNet.SendToOthers(EV_SIGNAL, new byte[] { SIG_REFRESH });
    }

    // Sends a locally imported file to the host as a chunked Photon upload.
    // fileName (string): file name including extension to upload from the local sounds folder
    // _hostAddressIgnored (string): legacy parameter; transport now uses Photon master client routing
    // returns: IEnumerator
    internal static IEnumerator UploadFileToHost(string fileName, string _hostAddressIgnored)
    {
        string filePath = Path.Combine(Plugin.SoundsFolder, fileName);
        if (!File.Exists(filePath)) yield break;
        byte[] data = File.ReadAllBytes(filePath);
        SendFileChunked(fileName, data, Patches.PhotonNet.MasterActorNumber);
        yield break;
    }

    // Marks the local player as connected to the host and triggers the initial sync. Called
    // from the Harmony OnJoinedRoom postfix when the local player is not the master client.
    // _hostAddressIgnored (string): legacy parameter; ignored under Photon transport
    // returns: void
    internal static void OnPlayerJoined(string _hostAddressIgnored)
    {
        if (!Patches.PhotonNet.IsAvailable)
        {
            StatusText = "photon unavailable";
            return;
        }
        _hostJoined = true;
        _syncFailed = false;
        Patches.PhotonNet.Subscribe(OnPhotonEvent);
        TriggerClientSync();
    }

    // Legacy entry point retained for menu compatibility. Photon transport does not need polling.
    // returns: void
    internal static void StartClientPollLoop() { }

    // Adds a player to the lobby tracking list. Called from NetworkSyncPatches on Photon player-join.
    // playerName (string): Photon NickName of the player who joined
    // returns: void
    internal static void OnPhotonPlayerJoined(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        lock (_syncLock)
            _lobbyPlayerNames.Add(playerName);
        Plugin.Log.LogInfo($"[Sync] Lobby player joined: '{playerName}'");
        BroadcastPermissions();
    }

    // Removes a player from the lobby tracking list. Called from NetworkSyncPatches on Photon player-leave.
    // playerName (string): Photon NickName of the player who left
    // returns: void
    internal static void OnPhotonPlayerLeft(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName)) return;
        lock (_syncLock)
        {
            _lobbyPlayerNames.Remove(playerName);
            _playerSyncedFileNames.Remove(playerName);
        }
        Plugin.Log.LogInfo($"[Sync] Lobby player left: '{playerName}'");
    }

    // Resets failure state and either re-broadcasts to clients (host) or kicks off a fresh sync (client).
    // returns: void
    internal static void AllowResync()
    {
        _syncFailed = false;
        if (IsHosting)
        {
            lock (_syncLock)
                _playerSyncedFileNames.Clear();
            BroadcastRefresh();
        }
        if (IsConnectedAsClient)
            TriggerClientSync();
    }

    // Starts a one-shot client sync coroutine if one is not already running.
    // returns: void
    internal static void TriggerClientSync()
    {
        if (_syncFailed || _clientSyncRunning || !_hostJoined) return;
        _clientSyncRunning = true;
        Plugin.Instance.StartCoroutine(RunClientSync());
    }

    // Returns the names of lobby players who have not yet downloaded all served audio files.
    // returns: List<string>
    internal static List<string> GetUnsyncedPlayerNames()
    {
        int totalFiles = GetServedAudioFileCount();
        lock (_syncLock)
        {
            List<string> result = new();
            foreach (string name in _lobbyPlayerNames)
            {
                HashSet<string>? synced;
                if (!_playerSyncedFileNames.TryGetValue(name, out synced) || synced.Count < totalFiles)
                    result.Add(name);
            }
            return result;
        }
    }

    // Returns true when every known lobby player has downloaded the given clip. Only relevant on the host.
    // clipName (string): clip name without extension
    // returns: bool
    internal static bool IsClipSyncedToAllClients(string clipName)
    {
        if (!_running) return true;
        lock (_syncLock)
        {
            if (_lobbyPlayerNames.Count == 0) return true;
            string? fileName = ResolveAudioFileName(clipName);
            if (fileName == null) return true;
            foreach (string playerName in _lobbyPlayerNames)
            {
                HashSet<string>? synced;
                if (!_playerSyncedFileNames.TryGetValue(playerName, out synced) || !synced.Contains(fileName))
                    return false;
            }
            return true;
        }
    }

    // Registers a newly imported file and waits for all known clients to fetch it.
    // fileName (string): imported file name including extension
    // returns: void
    internal static void RegisterImportedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;
        lock (_syncLock)
        {
            HashSet<string> waiting = new(_lobbyPlayerNames, StringComparer.OrdinalIgnoreCase);
            _pendingImportedFileClients[fileName] = waiting;
        }
    }

    // Returns true when all known clients have acknowledged download of the imported file.
    // fileName (string): imported file name including extension
    // returns: bool
    internal static bool IsImportedFileSynced(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting)) return true;
            if (waiting.Count == 0)
            {
                _pendingImportedFileClients.Remove(fileName);
                return true;
            }
            return false;
        }
    }

    // Returns count of clients still waiting to download the imported file.
    // fileName (string): imported file name including extension
    // returns: int - count of pending client downloads
    internal static int GetPendingClientCount(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting)) return 0;
            return waiting.Count;
        }
    }

    // Returns the count of audio files currently available to serve from this host.
    // returns: int - count of .ogg and .wav files in the sounds folder
    internal static int GetServedAudioFileCount()
    {
        try
        {
            return Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly).Length
                 + Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly).Length
                 + Directory.GetFiles(Plugin.SoundsFolder, "*.json", SearchOption.TopDirectoryOnly).Length;
        }
        catch (Exception) { return 0; }
    }

    // Returns how many files have been confirmed synced for the given player (by Photon display name).
    // playerName (string): the player's Photon NickName as stored in _lobbyPlayerNames
    // returns: int - number of files recorded as synced for that player
    internal static int GetPlayerSyncedFileCount(string playerName)
    {
        lock (_syncLock)
        {
            return _playerSyncedFileNames.TryGetValue(playerName, out HashSet<string> s) ? s.Count : 0;
        }
    }

    // Returns per-client download counts as a snapshot.
    // returns: List<(string displayName, int count)> - downloaded file count per known client
    internal static List<(string displayName, int count)> GetClientDownloadCounts()
    {
        lock (_syncLock)
        {
            List<(string, int)> result = new(_knownActors.Count);
            foreach (int actor in _knownActors)
            {
                string name = _actorNames.TryGetValue(actor, out string n) && !string.IsNullOrWhiteSpace(n)
                    ? n : $"actor#{actor}";
                int c = _actorDownloadedFiles.TryGetValue(actor, out HashSet<string> f) ? f.Count : 0;
                result.Add((name, c));
            }
            return result;
        }
    }

    // Returns imported files still pending download by at least one client.
    // returns: List<(string fileName, int pending)> - pending import entries
    internal static List<(string fileName, int pending)> GetPendingImports()
    {
        lock (_syncLock)
        {
            List<(string, int)> result = new();
            foreach (KeyValuePair<string, HashSet<string>> kv in _pendingImportedFileClients)
                if (kv.Value.Count > 0) result.Add((kv.Key, kv.Value.Count));
            return result;
        }
    }

    // Returns null when no audio file matches the clip name in the local sounds folder.
    private static string? ResolveAudioFileName(string clipName)
    {
        foreach (string ext in new[] { ".ogg", ".wav" })
        {
            if (File.Exists(Path.Combine(Plugin.SoundsFolder, clipName + ext)))
                return clipName + ext;
        }
        return null;
    }

    // Coroutine that drives a single client sync round-trip.
    private static IEnumerator RunClientSync()
    {
        StatusText = "syncing (photon)";
        _hostFileList = null;
        Patches.PhotonNet.SendToMaster(EV_REQ_LIST, null);

        float waited = 0f;
        while (_hostFileList == null && waited < 15f)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        if (_hostFileList == null)
        {
            _syncFailed = true;
            _clientSyncRunning = false;
            StatusText = "sync failed (no host response)";
            Plugin.Log.LogWarning("[Sync] No file list received from host within 15s.");
            yield break;
        }

        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        List<string> needed = new();
        foreach ((string name, long size) in _hostFileList)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool isAudio = ext == ".ogg" || ext == ".wav";
            if (isAudio && size > 0L && size > maxBytes)
            {
                Plugin.Log.LogInfo($"  Skipping '{name}' ({size / 1024L} KB > {Plugin.MaxSyncFileSizeKb.Value} KB).");
                continue;
            }
            bool isJson = ext == ".json";
            if (!isJson && File.Exists(Path.Combine(Plugin.SoundsFolder, name))) continue;
            needed.Add(name);
        }

        lock (_syncLock)
        {
            _clientPendingFiles.Clear();
            foreach (string n in needed) _clientPendingFiles.Add(n);
        }

        foreach (string name in needed)
            Patches.PhotonNet.SendToMaster(EV_REQ_FILE, name);

        // Wait for every requested chunk stream to complete, with an overall timeout.
        float dlElapsed = 0f;
        float perFileTimeoutBudget = Math.Max(30f, needed.Count * 30f);
        while (true)
        {
            int remaining;
            lock (_syncLock) remaining = _clientPendingFiles.Count;
            if (remaining == 0) break;
            if (dlElapsed >= perFileTimeoutBudget)
            {
                Plugin.Log.LogWarning($"[Sync] Download timeout - {remaining} file(s) never completed.");
                break;
            }
            StatusText = $"downloading ({needed.Count - remaining}/{needed.Count})";
            dlElapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        // Confirm every file we have on disk that the host advertises.
        List<string> presentFiles = new();
        foreach ((string name, long _) in _hostFileList)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            if ((ext == ".ogg" || ext == ".wav" || ext == ".json") && File.Exists(Path.Combine(Plugin.SoundsFolder, name)))
                presentFiles.Add(name);
        }
        if (presentFiles.Count > 0)
            Patches.PhotonNet.SendToMaster(EV_CONFIRM_HAVE, presentFiles.ToArray());

        // Pull selection.json.
        _pendingSelectionBytes = null;
        Patches.PhotonNet.SendToMaster(EV_REQ_SEL, null);
        float selWait = 0f;
        while (_pendingSelectionBytes == null && selWait < 5f)
        {
            selWait += Time.unscaledDeltaTime;
            yield return null;
        }
        if (_pendingSelectionBytes != null)
            ApplySelectionJson(_pendingSelectionBytes);

        StatusText = "in sync";
        _clientSyncRunning = false;
        if (!Plugin.IsRefreshPending)
            Plugin.Instance.StartRefresh();
    }

    // Photon event dispatcher. Runs on the Unity main thread.
    private static void OnPhotonEvent(byte code, object? data, int senderActor)
    {
        try
        {
            switch (code)
            {
                case EV_REQ_LIST: HandleReqList(senderActor); break;
                case EV_FILE_LIST: HandleFileList(data); break;
                case EV_REQ_FILE: HandleReqFile(data, senderActor); break;
                case EV_FILE_CHUNK: HandleFileChunk(data, senderActor); break;
                case EV_CONFIRM_HAVE: HandleConfirmHave(data, senderActor); break;
                case EV_SIGNAL: HandleSignal(data); break;
                case EV_REQ_SEL: HandleReqSelection(senderActor); break;
                case EV_SELECTION: HandleSelection(data); break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Event {code} handler error: {ex.Message}");
        }
    }

    private static void HandleReqList(int senderActor)
    {
        if (!_running) return;
        TrackActor(senderActor);
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        List<string> entries = new();
        try
        {
            foreach (string f in Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(f).Length;
                if (size > maxBytes) continue;
                entries.Add($"{Path.GetFileName(f)}|{size}");
            }
            foreach (string f in Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(f).Length;
                if (size > maxBytes) continue;
                entries.Add($"{Path.GetFileName(f)}|{size}");
            }
            foreach (string f in Directory.GetFiles(Plugin.SoundsFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                long size = new FileInfo(f).Length;
                entries.Add($"{Path.GetFileName(f)}|{size}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Building file list failed: {ex.Message}");
        }
        Patches.PhotonNet.SendToActor(EV_FILE_LIST, entries.ToArray(), senderActor);
    }

    private static void HandleFileList(object? data)
    {
        if (data is not string[] entries) return;
        _hasReachedHost = true;
        List<(string, long)> list = new(entries.Length);
        foreach (string entry in entries)
        {
            string e = entry?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(e)) continue;
            int sep = e.IndexOf('|');
            string name = sep > 0 ? e.Substring(0, sep).Trim() : e;
            long size = -1L;
            if (sep > 0) long.TryParse(e.Substring(sep + 1).Trim(), out size);
            if (!string.IsNullOrEmpty(name)) list.Add((name, size));
        }
        _hostFileList = list;
    }

    private static void HandleReqFile(object? data, int senderActor)
    {
        if (!_running || data is not string fileName) return;
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
        string filePath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName));
        string root = Path.GetFullPath(Plugin.SoundsFolder);
        if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath)) return;
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        if (new FileInfo(filePath).Length > maxBytes) return;
        byte[] bytes = File.ReadAllBytes(filePath);
        SendFileChunked(fileName, bytes, senderActor);
    }

    private static void HandleFileChunk(object? data, int senderActor)
    {
        if (data is not object[] arr || arr.Length < 4) return;
        string? fileName = arr[0] as string;
        if (string.IsNullOrEmpty(fileName)) return;
        int idx = Convert.ToInt32(arr[1]);
        int total = Convert.ToInt32(arr[2]);
        byte[]? payload = arr[3] as byte[];
        if (payload == null || idx < 0 || total <= 0 || idx >= total) return;

        string key = $"{senderActor}:{fileName}";
        byte[][] buffer;
        bool complete;
        lock (_syncLock)
        {
            if (!_chunkBuffers.TryGetValue(key, out buffer))
            {
                buffer = new byte[total][];
                _chunkBuffers[key] = buffer;
                _chunkExpected[key] = total;
            }
            buffer[idx] = payload;
            complete = AllChunksPresent(buffer);
        }
        if (!complete) return;

        // Reassemble.
        int totalLen = 0;
        for (int i = 0; i < buffer.Length; i++) totalLen += buffer[i].Length;
        byte[] full = new byte[totalLen];
        int off = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            Buffer.BlockCopy(buffer[i], 0, full, off, buffer[i].Length);
            off += buffer[i].Length;
        }
        lock (_syncLock)
        {
            _chunkBuffers.Remove(key);
            _chunkExpected.Remove(key);
        }

        try
        {
            string destPath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName!));
            string root = Path.GetFullPath(Plugin.SoundsFolder);
            if (!destPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;

            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (_running)
            {
                // Treat any chunk arriving at the host as a client upload (only if allowed).
                if (Plugin.AllowClientImports == null || !Plugin.AllowClientImports.Value) return;
                if (ext != ".ogg" && ext != ".wav" && ext != ".json") return;
                File.WriteAllBytes(destPath, full);
                Plugin.Log.LogInfo($"[Sync] Received uploaded '{fileName}' from actor {senderActor}.");
                Plugin.Instance?.StartRefresh();
            }
            else
            {
                File.WriteAllBytes(destPath, full);
                Plugin.Log.LogInfo($"[Sync] Synced: {fileName}");
                lock (_syncLock) _clientPendingFiles.Remove(fileName);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Write failed for '{fileName}': {ex.Message}");
        }
    }

    private static bool AllChunksPresent(byte[][] buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            if (buffer[i] == null) return false;
        return true;
    }

    private static void HandleConfirmHave(object? data, int senderActor)
    {
        if (!_running || data is not string[] names) return;
        TrackActor(senderActor);
        foreach (string name in names)
        {
            if (!string.IsNullOrEmpty(name))
                RecordActorDownload(name, senderActor);
        }
    }

    private static void HandleSignal(object? data)
    {
        if (data is not byte[] bytes || bytes.Length == 0) return;
        switch (bytes[0])
        {
            case SIG_REFRESH:
                _syncFailed = false;
                _clientSyncRunning = false;
                TriggerClientSync();
                break;
            case SIG_STOP:
                Plugin.ClearTimedSubtitles();
                Plugin.ApplyRemoteStop();
                break;
            case SIG_PLAY:
                string playName = bytes.Length > 1 ? Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1) : string.Empty;
                Plugin.ApplyRemotePlay(playName);
                break;
            case SIG_PAUSE:
                Plugin.ApplyRemotePause();
                break;
            case SIG_RESUME:
                Plugin.ApplyRemoteResume();
                break;
            case SIG_FORCE_NEXT:
                string forceName = bytes.Length > 1 ? Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1) : string.Empty;
                Plugin.ApplyRemoteForceNext(forceName);
                break;
            case SIG_SUBTITLE:
                if (bytes.Length >= 2)
                {
                    int sep = Array.IndexOf(bytes, (byte)0, 1);
                    string clipName = sep > 1 ? Encoding.UTF8.GetString(bytes, 1, sep - 1) : string.Empty;
                    string subtitle = sep >= 1 && sep < bytes.Length - 1
                        ? Encoding.UTF8.GetString(bytes, sep + 1, bytes.Length - sep - 1)
                        : string.Empty;
                    if (!string.IsNullOrEmpty(clipName))
                        Plugin.SaveSubtitleOverrideForClip(clipName, subtitle);
                }
                break;
            case SIG_CLIP_ENABLED:
                if (bytes.Length >= 3)
                {
                    bool enabled = bytes[1] != 0;
                    string enabledClipName = Encoding.UTF8.GetString(bytes, 2, bytes.Length - 2);
                    if (!string.IsNullOrEmpty(enabledClipName))
                    {
                        Plugin.EnabledClips[enabledClipName] = enabled;
                        Plugin.SaveSelection();
                    }
                }
                break;
            case SIG_PERMISSIONS:
                if (bytes.Length >= 2)
                {
                    byte perms = bytes[1];
                    ClientAllowPlayback = (perms & 1) != 0;
                    ClientAllowSubtitleEdit = (perms & 2) != 0;
                    ClientAllowSelectionEdit = (perms & 4) != 0;
                    ClientAllowSettingsChange = (perms & 8) != 0;
                    Plugin.Log.LogInfo($"[Sync] Host permissions: playback={ClientAllowPlayback} subtitleEdit={ClientAllowSubtitleEdit} selectionEdit={ClientAllowSelectionEdit} settingsChange={ClientAllowSettingsChange}");
                }
                break;
        }
    }

    private static void HandleReqSelection(int senderActor)
    {
        if (!_running) return;
        TrackActor(senderActor);
        try
        {
            string selPath = Path.Combine(Plugin.SoundsFolder, "selection.json");
            if (!File.Exists(selPath)) return;
            byte[] bytes = File.ReadAllBytes(selPath);
            Patches.PhotonNet.SendToActor(EV_SELECTION, bytes, senderActor);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] Selection serve failed: {ex.Message}");
        }
    }

    private static void HandleSelection(object? data)
    {
        if (data is byte[] bytes) _pendingSelectionBytes = bytes;
    }

    // Splits a file into Photon-sized chunks and dispatches each to the target actor.
    private static void SendFileChunked(string fileName, byte[] bytes, int targetActor)
    {
        if (targetActor < 0) return;
        int total = Math.Max(1, (int)Math.Ceiling(bytes.Length / (double)CHUNK_SIZE));
        for (int i = 0; i < total; i++)
        {
            int off = i * CHUNK_SIZE;
            int len = Math.Min(CHUNK_SIZE, bytes.Length - off);
            byte[] slice = new byte[len];
            Buffer.BlockCopy(bytes, off, slice, 0, len);
            object[] payload = new object[] { fileName, i, total, slice };
            Patches.PhotonNet.SendToActor(EV_FILE_CHUNK, payload, targetActor);
        }
    }

    // Tracks an actor and updates the synced file map for their NickName.
    private static void RecordActorDownload(string fileName, int actor)
    {
        string displayName;
        int fileCount;
        lock (_syncLock)
        {
            HashSet<string> files;
            if (!_actorDownloadedFiles.TryGetValue(actor, out files))
            {
                files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _actorDownloadedFiles[actor] = files;
            }
            files.Add(fileName);
            fileCount = files.Count;
            displayName = _actorNames.TryGetValue(actor, out string n) && !string.IsNullOrWhiteSpace(n)
                ? n : $"actor#{actor}";
            if (!string.IsNullOrEmpty(displayName))
            {
                if (!_playerSyncedFileNames.TryGetValue(displayName, out HashSet<string> namedFiles))
                {
                    namedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _playerSyncedFileNames[displayName] = namedFiles;
                }
                namedFiles.Add(fileName);
            }
            if (_pendingImportedFileClients.TryGetValue(fileName, out HashSet<string> waiting))
            {
                waiting.Remove(displayName);
            }
        }
        Plugin.Log.LogInfo($"[Sync] {displayName} confirmed '{fileName}' (now has {fileCount} file(s) from this host)");
    }

    // Resolves a sender actor's NickName via Photon and stores it. Also auto-registers them in the
    // lobby tracking list so the host still works when PEAK's PlayerConnectionLog hook never fires.
    private static void TrackActor(int actor)
    {
        if (actor < 0) return;
        string name = Patches.PhotonNet.GetNickNameForActor(actor);
        bool added = false;
        lock (_syncLock)
        {
            _knownActors.Add(actor);
            if (!string.IsNullOrWhiteSpace(name))
            {
                _actorNames[actor] = name;
                if (!_lobbyPlayerNames.Contains(name))
                {
                    _lobbyPlayerNames.Add(name);
                    added = true;
                }
            }
        }
        if (added)
            Plugin.Log.LogInfo($"[Sync] Auto-registered actor {actor} as '{name}' (via incoming Photon event).");
    }

    private static void ApplySelectionJson(byte[] bytes)
    {
        try
        {
            string raw = Encoding.UTF8.GetString(bytes);
            System.Text.RegularExpressions.MatchCollection matches =
                System.Text.RegularExpressions.Regex.Matches(
                    raw, "\"(?<k>(?:\\\\.|[^\"])+)\"\\s*:\\s*(?<v>true|false)");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string key = System.Text.RegularExpressions.Regex.Unescape(m.Groups["k"].Value);
                bool value = m.Groups["v"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                Plugin.EnabledClips[key] = value;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Sync] selection.json parse failed: {ex.Message}");
        }
    }
}
