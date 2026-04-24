using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;
namespace BingBongVoiceOverride;

/// 
/// HTTP-based sound sync server (host) and downloader (client).
///
/// Host side: after clips load, a lightweight HttpListener serves the sound file
/// list and raw bytes on port 28472 so joining clients can pull them.
///
/// Client side: call OnPlayerJoined(hostAddress) from a Harmony patch on the
/// game's player-join event (e.g. the method that fires when the local client
/// finishes connecting to a session). Supply the host's LAN IP and this class
/// downloads any missing .ogg files then triggers a sound refresh automatically.
/// 
internal static class BingBongNetworkSync
{
    /// Human-readable server/sync state shown in the debug overlay.
    internal static string StatusText = "idle";

    /// True when the local player is running the HTTP sound-sync server (is the session host).
    internal static bool IsHosting => _running;

    /// The host IP address this client is syncing from. Empty when not connected as a client.
    internal static string ActiveHostAddress => _activeHostAddress;

    /// True when the host has set AllowClientImports, updated via status polling.
    internal static bool HostAllowsClientImports = false;

    private static HttpListener? _listener;
    private static Thread? _serverThread;
    private static bool _running = false;
    private static readonly object _syncLock = new();
    private static readonly HashSet<string> _knownClients = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _pendingImportedFileClients =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<string>> _clientDownloadedFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _clientDisplayNames =
        new(StringComparer.OrdinalIgnoreCase);
    private static string _activeHostAddress = string.Empty;
    private static bool _clientAutoSyncRunning = false;
    private static int _stopGeneration = 0;
    private static int _lastSeenStopGen = -1;
    private static int _refreshGeneration = 0;
    private static int _lastSeenRefreshGen = -1;

    /// Starts the HTTP sound-sync server so clients that join can pull .ogg files from this host.
    /// <returns>void</returns>
    internal static void StartServer()
    {
        if (_running) return;

        _listener = new HttpListener();
        bool bound = TryBind("http://+:28472/bingbong/") || TryBind("http://localhost:28472/bingbong/");

        if (!bound)
        {
            StatusText = "server unavailable";
            Plugin.Log.LogWarning("Could not start sound sync server on port 28472.");
            return;
        }

        _running = true;
        _serverThread = new Thread(ServeLoop) { IsBackground = true, Name = "BingBongSoundServer" };
        _serverThread.Start();
        Plugin.Log.LogInfo($"Sound sync server started. ({StatusText})");
    }

    /// Stops the HTTP sound-sync server.
    /// <returns>void</returns>
    internal static void StopServer()
    {
        _running = false;
        _clientAutoSyncRunning = false;
        try { _listener?.Stop(); } catch (Exception) { }
        StatusText = "idle";
    }

    /// Increments the stop generation counter so connected clients clear their timed subtitle state on the next poll.
    /// <returns>void</returns>
    internal static void BroadcastStop()
    {
        Interlocked.Increment(ref _stopGeneration);
    }

    /// Increments the refresh generation counter so connected clients trigger a sound refresh on the next poll.
    /// <returns>void</returns>
    internal static void BroadcastRefresh()
    {
        Interlocked.Increment(ref _refreshGeneration);
    }

    /// Uploads a file from the local sounds folder to the host server. Only runs when AllowClientImports is true on the host.
    /// <param name="fileName">File name (including extension) to upload from the local sounds folder.</param>
    /// <param name="hostAddress">LAN IP address of the session host.</param>
    /// <returns>IEnumerator</returns>
    internal static IEnumerator UploadFileToHost(string fileName, string hostAddress)
    {
        string filePath = Path.Combine(Plugin.SoundsFolder, fileName);
        if (!File.Exists(filePath)) yield break;

        byte[] data = File.ReadAllBytes(filePath);
        string url = $"http://{hostAddress}:28472/bingbong/upload?name={Uri.EscapeDataString(fileName)}";
        string localName = Plugin.GetLocalPlayerName();
        bool success = false;
        string? error = null;
        yield return NetPost(url, data, "application/octet-stream", localName, (ok, err) => { success = ok; error = err; });

        if (success)
            Plugin.Log.LogInfo($"[Upload] Sent '{fileName}' to host.");
        else
            Plugin.Log.LogWarning($"[Upload] Failed to send '{fileName}': {error}");
    }

    /// 
    /// Call this from a Harmony patch on the game's player-join event.
    /// Downloads any sound/subtitle files the client is missing from the host, then
    /// triggers a sound refresh so the new clips are available immediately.
    /// 
    /// <param name="hostAddress">LAN IP address of the session host.</param>
    /// <returns>void</returns>
    internal static void OnPlayerJoined(string hostAddress)
    {
        if (hostAddress == GetLocalIpAddress() || hostAddress == "127.0.0.1") return;
        _activeHostAddress = hostAddress;
        Plugin.Instance.StartCoroutine(DownloadSoundsFromHost(hostAddress));
        if (!_clientAutoSyncRunning)
        {
            _clientAutoSyncRunning = true;
            Plugin.Instance.StartCoroutine(ClientAutoSyncLoop());
        }
    }

    /// Registers a newly imported file and starts waiting for all known clients to fetch it.
    /// <param name="fileName">Imported file name, including extension.</param>
    /// <returns>void</returns>
    internal static void RegisterImportedFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        lock (_syncLock)
        {
            HashSet<string> waiting = new(_knownClients, StringComparer.OrdinalIgnoreCase);
            _pendingImportedFileClients[fileName] = waiting;
        }
    }

    /// Returns true when all known clients have acknowledged download of the imported file.
    /// <param name="fileName">Imported file name, including extension.</param>
    /// <returns>bool</returns>
    internal static bool IsImportedFileSynced(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting))
                return true;
            if (waiting.Count == 0)
            {
                _pendingImportedFileClients.Remove(fileName);
                return true;
            }
            return false;
        }
    }

    /// Returns count of clients still waiting to download the imported file.
    /// <param name="fileName">Imported file name, including extension.</param>
    /// <returns>Count of pending client downloads.</returns>
    internal static int GetPendingClientCount(string fileName)
    {
        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (!_pendingImportedFileClients.TryGetValue(fileName, out waiting))
                return 0;
            return waiting.Count;
        }
    }

    /// Returns the count of audio files currently available to serve from this host.
    /// <returns>Count of .ogg and .wav files in the sounds folder.</returns>
    internal static int GetServedAudioFileCount()
    {
        try
        {
            return Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly).Length
                 + Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly).Length;
        }
        catch (Exception) { return 0; }
    }

    /// Returns per-client download counts as a snapshot.
    /// <returns>List of (displayName, downloadedFileCount) for each known client.</returns>
    internal static List<(string displayName, int count)> GetClientDownloadCounts()
    {
        lock (_syncLock)
        {
            List<(string, int)> result = new(_knownClients.Count);
            foreach (string ip in _knownClients)
            {
                string name = _clientDisplayNames.TryGetValue(ip, out string n) && !string.IsNullOrWhiteSpace(n) ? n : ip;
                int c = _clientDownloadedFiles.TryGetValue(ip, out HashSet<string> f) ? f.Count : 0;
                result.Add((name, c));
            }
            return result;
        }
    }

    /// Returns imported files still pending download by at least one client.
    /// <returns>List of (fileName, pendingClientCount) for each pending import.</returns>
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

    private static bool TryBind(string prefix)
    {
        try
        {
            _listener!.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            StatusText = prefix.Contains("+") ? "hosting *:28472" : "hosting localhost:28472";
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Bind failed for {prefix}: {ex.Message}");
            return false;
        }
    }

    private static IEnumerator DownloadSoundsFromHost(string hostAddress)
    {
        StatusText = $"syncing from {hostAddress}";
        Plugin.Log.LogInfo($"Fetching sound list from host {hostAddress}...");

        string localName = Plugin.GetLocalPlayerName();
        byte[]? listBytes = null;
        string? listError = null;
        yield return NetGet($"http://{hostAddress}:28472/bingbong/list", localName,
            (b, e) => { listBytes = b; listError = e; });

        if (listBytes == null)
        {
            StatusText = "sync failed";
            Plugin.Log.LogWarning($"Could not reach host sound server: {listError}");
            yield break;
        }

        string[] fileNames = Encoding.UTF8.GetString(listBytes)
            .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        int downloaded = 0;
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        foreach (string fileName in fileNames)
        {
            string entry = fileName.Trim();
            if (string.IsNullOrEmpty(entry)) continue;

            string name;
            long sizeBytes = -1L;
            int sep = entry.IndexOf('|');
            if (sep > 0)
            {
                name = entry.Substring(0, sep).Trim();
                long.TryParse(entry.Substring(sep + 1).Trim(), out sizeBytes);
            }
            else
            {
                name = entry;
            }
            if (string.IsNullOrEmpty(name)) continue;

            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool isAudio = ext == ".ogg" || ext == ".wav";
            if (isAudio && sizeBytes > 0L && sizeBytes > maxBytes)
            {
                Plugin.Log.LogInfo($"  Skipping '{name}' ({sizeBytes / 1024L} KB > {Plugin.MaxSyncFileSizeKb.Value} KB).");
                continue;
            }

            string destPath = Path.Combine(Plugin.SoundsFolder, name);
            if (File.Exists(destPath)) continue;

            byte[]? fileBytes = null;
            string? fileError = null;
            yield return NetGet(
                $"http://{hostAddress}:28472/bingbong/file/{Uri.EscapeDataString(name)}",
                localName, (b, e) => { fileBytes = b; fileError = e; });

            if (fileBytes == null)
            {
                Plugin.Log.LogWarning($"Failed to download '{name}': {fileError}");
                continue;
            }

            File.WriteAllBytes(destPath, fileBytes);
            Plugin.Log.LogInfo($"  Synced: {name}");
            downloaded++;
        }

        StatusText = downloaded > 0 ? $"synced {downloaded} file(s)" : "in sync";

        if (downloaded > 0)
            Plugin.Instance.StartRefresh();

        yield return Plugin.Instance.StartCoroutine(SyncSelectionFromHost(hostAddress));
    }

    private static IEnumerator SyncSelectionFromHost(string hostAddress)
    {
        byte[]? bytes = null;
        yield return NetGet(
            $"http://{hostAddress}:28472/bingbong/file/{Uri.EscapeDataString("selection.json")}",
            Plugin.GetLocalPlayerName(), (b, _) => { bytes = b; });

        if (bytes == null) yield break;

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

    private static IEnumerator NetGet(string url, string playerName, Action<byte[]?, string?> onDone)
    {
        byte[]? result = null;
        string? error = null;
        bool done = false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                using System.Net.WebClient wc = new System.Net.WebClient();
                if (!string.IsNullOrEmpty(playerName))
                    wc.Headers["X-Player-Name"] = playerName;
                result = wc.DownloadData(url);
            }
            catch (Exception ex) { error = ex.Message; }
            finally { done = true; }
        });
        while (!done) yield return null;
        onDone(result, error);
    }

    private static IEnumerator NetPost(string url, byte[] data, string contentType, string playerName, Action<bool, string?> onDone)
    {
        bool success = false;
        string? error = null;
        bool done = false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                using System.Net.WebClient wc = new System.Net.WebClient();
                wc.Headers["Content-Type"] = contentType;
                if (!string.IsNullOrEmpty(playerName))
                    wc.Headers["X-Player-Name"] = playerName;
                wc.UploadData(url, "POST", data);
                success = true;
            }
            catch (Exception ex) { error = ex.Message; }
            finally { done = true; }
        });
        while (!done) yield return null;
        onDone(success, error);
    }

    private static IEnumerator ClientAutoSyncLoop()
    {
        while (_clientAutoSyncRunning)
        {
            if (!string.IsNullOrWhiteSpace(_activeHostAddress) && Plugin.Instance != null)
            {
                yield return Plugin.Instance.StartCoroutine(DownloadSoundsFromHost(_activeHostAddress));
                yield return Plugin.Instance.StartCoroutine(FetchHostStatus(_activeHostAddress));
            }
            yield return new UnityEngine.WaitForSecondsRealtime(2f);
        }
    }

    private static IEnumerator FetchHostStatus(string hostAddress)
    {
        byte[]? bytes = null;
        yield return NetGet($"http://{hostAddress}:28472/bingbong/status",
            Plugin.GetLocalPlayerName(), (b, _) => { bytes = b; });

        if (bytes == null) yield break;

        string body = Encoding.UTF8.GetString(bytes);

        int stopGen = ParseJsonInt(body, "stopGen", -1);
        if (stopGen >= 0)
        {
            if (_lastSeenStopGen >= 0 && stopGen != _lastSeenStopGen)
                Plugin.ClearTimedSubtitles();
            _lastSeenStopGen = stopGen;
        }

        int refreshGen = ParseJsonInt(body, "refreshGen", -1);
        if (refreshGen >= 0)
        {
            if (_lastSeenRefreshGen >= 0 && refreshGen != _lastSeenRefreshGen)
                Plugin.Instance?.StartRefresh();
            _lastSeenRefreshGen = refreshGen;
        }

        int allowImports = ParseJsonInt(body, "allowClientImports", 0);
        HostAllowsClientImports = allowImports == 1;
    }

    private static void ServeLoop()
    {
        while (_running && _listener != null && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = _listener.GetContext();
            }
            catch (Exception)
            {
                break;
            }
            ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url.AbsolutePath;
            string requesterIp = context.Request.RemoteEndPoint?.Address.ToString() ?? string.Empty;
            string playerName = context.Request.Headers["X-Player-Name"] ?? string.Empty;
            TrackClient(requesterIp, playerName);
            if (path.EndsWith("/list", StringComparison.OrdinalIgnoreCase))
                ServeFileList(context);
            else if (path.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
                ServeStatus(context);
            else if (path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
                HandleUpload(context);
            else if (path.Contains("/file/"))
                ServeFile(context, Path.GetFileName(Uri.UnescapeDataString(path)), requesterIp);
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Sound server request error: {ex.Message}");
        }
    }

    private static void ServeStatus(HttpListenerContext context)
    {
        int allowImports = Plugin.AllowClientImports != null && Plugin.AllowClientImports.Value ? 1 : 0;
        string json = $"{{\"stopGen\":{_stopGeneration},\"refreshGen\":{_refreshGeneration},\"allowClientImports\":{allowImports}}}";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void HandleUpload(HttpListenerContext context)
    {
        if (Plugin.AllowClientImports == null || !Plugin.AllowClientImports.Value)
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        string rawName = context.Request.QueryString["name"] ?? string.Empty;
        string fileName = Path.GetFileName(rawName);
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext != ".ogg" && ext != ".wav" && ext != ".json")
        {
            context.Response.StatusCode = 415;
            context.Response.Close();
            return;
        }

        string destPath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName));
        string soundsRoot = Path.GetFullPath(Plugin.SoundsFolder);
        if (!destPath.StartsWith(soundsRoot, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        try
        {
            using System.IO.Stream body = context.Request.InputStream;
            byte[] data;
            using (System.IO.MemoryStream ms = new())
            {
                body.CopyTo(ms);
                data = ms.ToArray();
            }
            File.WriteAllBytes(destPath, data);
            Plugin.Log.LogInfo($"[Upload] Received '{fileName}' from client.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Upload] Failed to save '{fileName}': {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.Close();
        Plugin.Instance?.StartRefresh();
    }

    private static void ServeFileList(HttpListenerContext context)
    {
        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        string[] oggFiles = Directory.GetFiles(Plugin.SoundsFolder, "*.ogg", SearchOption.TopDirectoryOnly);
        string[] wavFiles = Directory.GetFiles(Plugin.SoundsFolder, "*.wav", SearchOption.TopDirectoryOnly);
        System.Collections.Generic.List<string> lines = new();
        foreach (string f in oggFiles)
        {
            long size = new FileInfo(f).Length;
            if (size > maxBytes) continue;
            lines.Add($"{Path.GetFileName(f)}|{size}");
        }
        foreach (string f in wavFiles)
        {
            long size = new FileInfo(f).Length;
            if (size > maxBytes) continue;
            lines.Add($"{Path.GetFileName(f)}|{size}");
        }
        string body = string.Join("\n", lines.ToArray());
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "text/plain";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void ServeFile(HttpListenerContext context, string fileName, string requesterIp)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        string filePath = Path.GetFullPath(Path.Combine(Plugin.SoundsFolder, fileName));
        string soundsRoot = Path.GetFullPath(Plugin.SoundsFolder);

        if (!filePath.StartsWith(soundsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
        {
            context.Response.StatusCode = 404;
            context.Response.Close();
            return;
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".json" && !fileName.Equals("selection.json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        long maxBytes = Math.Max(64L, Plugin.MaxSyncFileSizeKb.Value) * 1024L;
        long fileSize = new FileInfo(filePath).Length;
        if ((ext == ".ogg" || ext == ".wav") && fileSize > maxBytes)
        {
            context.Response.StatusCode = 413;
            context.Response.Close();
            return;
        }

        byte[] bytes = File.ReadAllBytes(filePath);
        AcknowledgeDownloadedByClient(fileName, requesterIp);
        if (ext == ".ogg" || ext == ".wav")
            RecordClientDownload(fileName, requesterIp);
        if (ext == ".json")
            context.Response.ContentType = "application/json";
        else if (ext == ".wav")
            context.Response.ContentType = "audio/wav";
        else
            context.Response.ContentType = "audio/ogg";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.Close();
    }

    private static void RecordClientDownload(string fileName, string requesterIp)
    {
        if (string.IsNullOrWhiteSpace(requesterIp)) return;
        string displayName;
        int fileCount;
        lock (_syncLock)
        {
            HashSet<string> clientFiles;
            if (!_clientDownloadedFiles.TryGetValue(requesterIp, out clientFiles))
            {
                clientFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _clientDownloadedFiles[requesterIp] = clientFiles;
            }
            clientFiles.Add(fileName);
            fileCount = clientFiles.Count;
            displayName = _clientDisplayNames.TryGetValue(requesterIp, out string n) && !string.IsNullOrWhiteSpace(n) ? n : requesterIp;
        }
        Plugin.Log.LogInfo($"[Sync] {displayName} downloaded '{fileName}' (now has {fileCount} file(s) from this host)");
    }

    private static void TrackClient(string requesterIp, string playerName)
    {
        if (string.IsNullOrWhiteSpace(requesterIp) || requesterIp == "127.0.0.1" || requesterIp == "::1")
            return;
        if (requesterIp == GetLocalIpAddress())
            return;

        lock (_syncLock)
        {
            _knownClients.Add(requesterIp);
            if (!string.IsNullOrWhiteSpace(playerName))
                _clientDisplayNames[requesterIp] = playerName;
        }
    }

    private static void AcknowledgeDownloadedByClient(string fileName, string requesterIp)
    {
        if (string.IsNullOrWhiteSpace(requesterIp))
            return;

        lock (_syncLock)
        {
            HashSet<string> waiting;
            if (_pendingImportedFileClients.TryGetValue(fileName, out waiting))
                waiting.Remove(requesterIp);
        }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (IPAddress address in host.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return address.ToString();
            }
        }
        catch (Exception) { }
        return "127.0.0.1";
    }

    private static int ParseJsonInt(string json, string key, int fallback)
    {
        string search = "\"" + key + "\":";
        int idx = json.IndexOf(search, StringComparison.Ordinal);
        if (idx < 0) return fallback;
        int start = idx + search.Length;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        if (end == start) return fallback;
        return int.TryParse(json.Substring(start, end - start), out int val) ? val : fallback;
    }
}
