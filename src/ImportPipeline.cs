using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
namespace BingBongVoiceOverride;

public partial class Plugin
{
    // Queues a URL import and triggers refresh after a successful download.
    // url (string): Direct audio URL to download into the sounds folder.
    // returns: void
    internal void StartImportFromUrl(string url)
    {
        StartCoroutine(ImportFromUrlCoroutine(url));
    }

    // Downloads an audio URL into the sounds folder and refreshes clips.
    // url (string): Audio URL entered from the importer menu.
    // returns: IEnumerator
    private IEnumerator ImportFromUrlCoroutine(string url)
    {
        EnsureExampleFiles();

        if (string.IsNullOrWhiteSpace(url))
        {
            ImportStatus = "error: URL is empty";
            yield break;
        }

        string trimmed = url.Trim();
        if (IsYtDlpUrl(trimmed))
        {
            yield return StartCoroutine(ImportFromYtDlpCoroutine(trimmed));
            yield break;
        }

        ImportStatus = "downloading...";
        using UnityWebRequest request = UnityWebRequest.Get(trimmed);
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            ImportStatus = $"error: {request.error}";
            yield break;
        }

        byte[] data = request.downloadHandler.data;
        if (data == null || data.Length == 0) { ImportStatus = "error: no file data returned"; yield break; }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri uri))
        {
            ImportStatus = "error: invalid URL";
            yield break;
        }

        string normalizedExt = ResolveAudioExtension(request, uri, data);
        if (string.IsNullOrWhiteSpace(normalizedExt))
        {
            ImportStatus = "error: URL did not return recognizable OGG/WAV audio";
            yield break;
        }

        string baseName = ResolveBaseNameFromLink(request, uri);
        string safeBase = NormalizeFileName(baseName);
        if (safeBase.Length > 60) safeBase = safeBase.Substring(0, 60);
        string finalName = $"{safeBase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}{normalizedExt}";
        string finalPath = Path.Combine(SoundsFolder, finalName);

        File.WriteAllBytes(finalPath, data);

        string subtitlePath = Path.ChangeExtension(finalPath, ".json");
        if (!File.Exists(subtitlePath))
            File.WriteAllText(subtitlePath, "{\n  \"subtitle\": \"Bing Bong imported line\"\n}\n");

        yield return StartCoroutine(FinalizeImportedFile(finalName));
    }

    // Downloads audio from a YouTube or Twitch URL using yt-dlp, converts to mono OGG, and loads the clip.
    // url (string): YouTube or Twitch URL to extract audio from.
    // returns: IEnumerator
    private IEnumerator ImportFromYtDlpCoroutine(string url)
    {
        string ytDlp = FindYtDlp();
        if (string.IsNullOrEmpty(ytDlp))
        {
            ImportStatus = "yt-dlp not found -- downloading automatically...";
            yield return StartCoroutine(TryAutoDownloadYtDlpCoroutine());
            ytDlp = FindYtDlp();
        }
        if (string.IsNullOrEmpty(ytDlp))
        {
            ImportStatus = "error: yt-dlp could not be found or downloaded. Get it from https://github.com/yt-dlp/yt-dlp";
            yield break;
        }

        string pluginDirForFfmpeg = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
        string ffmpeg = FindFfmpeg(pluginDirForFfmpeg);
        if (string.IsNullOrEmpty(ffmpeg))
        {
            ImportStatus = "ffmpeg not found -- downloading automatically...";
            yield return StartCoroutine(TryAutoDownloadFfmpegCoroutine(pluginDirForFfmpeg));
            ffmpeg = FindFfmpeg(pluginDirForFfmpeg);
        }
        if (string.IsNullOrEmpty(ffmpeg))
        {
            ImportStatus = "error: ffmpeg could not be found or downloaded. Get it from https://ffmpeg.org/download.html";
            yield break;
        }

        string ffmpegLocationArg = Path.IsPathRooted(ffmpeg)
            ? $" --ffmpeg-location \"{Path.GetDirectoryName(ffmpeg)}\""
            : string.Empty;

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string outTemplate = Path.Combine(SoundsFolder, $"yt_{timestamp}.%(ext)s");

        ProcessStartInfo psi = new()
        {
            FileName = ytDlp,
            Arguments = $"-x --no-playlist -N 4 --audio-format vorbis --audio-quality 8"
                + ffmpegLocationArg
                + $" --print \"%(title)s\""
                + $" --print \"after_move:%(filepath)s\""
                + $" --postprocessor-args \"ffmpeg:-ac 1 -ar 32000\""
                + $" -o \"{outTemplate}\""
                + $" \"{url}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex) { ImportStatus = $"error: could not start yt-dlp: {ex.Message}"; yield break; }

        float elapsed = 0f;
        while (!proc.HasExited)
        {
            elapsed += UnityEngine.Time.unscaledDeltaTime;
            ImportStatus = $"yt-dlp running... ({elapsed:F0}s)";
            yield return null;
        }

        int exitCode = proc.ExitCode;
        string stdout = proc.StandardOutput.ReadToEnd().Trim();
        string stderr = proc.StandardError.ReadToEnd().Trim();
        proc.Dispose();

        if (exitCode != 0)
        {
            string msg = stderr.Length > 120 ? stderr.Substring(stderr.Length - 120) : stderr;
            ImportStatus = $"error: yt-dlp exited {exitCode}: {msg}";
            yield break;
        }

        string detectedTitle = string.Empty;
        string outputPath = string.Empty;
        foreach (string line in stdout.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = line.Trim();
            if (t.StartsWith("after_move:", StringComparison.OrdinalIgnoreCase))
                outputPath = t.Substring("after_move:".Length).Trim();
            else if (string.IsNullOrWhiteSpace(detectedTitle))
                detectedTitle = t;
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            string[] candidates = Directory.GetFiles(SoundsFolder, $"yt_{timestamp}.*", SearchOption.TopDirectoryOnly);
            if (candidates.Length > 0) outputPath = candidates[0];
        }

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
        {
            ImportStatus = "error: yt-dlp finished but no output file found in sounds folder";
            yield break;
        }

        string ext = Path.GetExtension(outputPath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".ogg";
        string safeBase = NormalizeFileName(detectedTitle);
        if (string.IsNullOrWhiteSpace(safeBase)) safeBase = $"yt_{timestamp}";
        if (safeBase.Length > 60) safeBase = safeBase.Substring(0, 60);
        string finalName = $"{safeBase}_{timestamp}{ext}";
        string finalPath = Path.Combine(SoundsFolder, finalName);
        if (!outputPath.Equals(finalPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(outputPath, finalPath);
        }

        string subtitlePath = Path.ChangeExtension(finalPath, ".json");
        if (!File.Exists(subtitlePath))
            File.WriteAllText(subtitlePath, "{\n  \"subtitle\": \"Bing Bong imported line\"\n}\n");

        yield return StartCoroutine(FinalizeImportedFile(Path.GetFileName(finalPath)));
        if (FetchTimedSubtitlesOnImport.Value)
            StartCoroutine(ImportSubtitleFromYtDlpCoroutine(url, ytDlp, timestamp, finalPath));
        else
            ImportStatus = $"saved (no timed subs): {Path.GetFileName(finalPath)}";
    }

    // Runs subtitle extraction in a separate yt-dlp request after audio import completes.
    // url (string): Original media URL.
    // ytDlp (string): Resolved yt-dlp executable path.
    // timestamp (string): Import timestamp prefix used by the audio import.
    // finalAudioPath (string): Final saved audio file path for writing companion subtitle JSON.
    // returns: IEnumerator
    private IEnumerator ImportSubtitleFromYtDlpCoroutine(string url, string ytDlp, string timestamp, string finalAudioPath)
    {
        float now = Time.realtimeSinceStartup;
        if (_nextSubtitleImportAllowedAt > now)
            yield return new WaitForSecondsRealtime(_nextSubtitleImportAllowedAt - now);

        string outputTemplate = Path.Combine(SoundsFolder, $"yt_{timestamp}.%(ext)s");
        bool fetched = false;
        string stderr = string.Empty;

        yield return StartCoroutine(RunSubtitleImportPass(ytDlp, url, outputTemplate, autoSubs: false, onDone: (ok, err) =>
        {
            fetched = ok;
            stderr = err;
        }));

        if (!fetched)
        {
            yield return StartCoroutine(RunSubtitleImportPass(ytDlp, url, outputTemplate, autoSubs: true, onDone: (ok, err) =>
            {
                fetched = ok;
                stderr = err;
            }));
        }

        _nextSubtitleImportAllowedAt = Time.realtimeSinceStartup + SubtitleImportCooldownSeconds;

        if (!fetched)
        {
            if (!string.IsNullOrWhiteSpace(stderr)) Log.LogInfo($"Subtitle import skipped: {stderr}");
            yield break;
        }

        string subtitlePath = Path.ChangeExtension(finalAudioPath, ".json");
        if (!TryWriteTimedSubtitleJsonFromVtt(timestamp, finalAudioPath, subtitlePath)) yield break;

        string clipName = Path.GetFileNameWithoutExtension(finalAudioPath);
        string subtitle = TryReadSubtitleOverride(finalAudioPath);
        if (!string.IsNullOrWhiteSpace(subtitle)) SubtitleOverrides[clipName] = subtitle;
        System.Collections.Generic.List<TimedSubtitleLine> timed = TryReadTimedSubtitleOverrides(finalAudioPath);
        if (timed.Count > 0) TimedSubtitleOverrides[clipName] = timed;
        ImportStatus = $"subtitle ready: {Path.GetFileName(finalAudioPath)}";
    }

    // Executes one yt-dlp subtitle pass and reports whether it produced any VTT files for the timestamp.
    // ytDlp (string): Resolved yt-dlp executable path.
    // url (string): Original media URL.
    // outputTemplate (string): yt-dlp output template path.
    // autoSubs (bool): True to request auto captions, false for regular subtitle tracks.
    // onDone (Action<bool, string>): Callback receiving (success, stderr).
    // returns: IEnumerator
    private IEnumerator RunSubtitleImportPass(string ytDlp, string url, string outputTemplate, bool autoSubs, Action<bool, string> onDone)
    {
        string passFlag = autoSubs ? "--write-auto-subs" : "--write-subs";
        ProcessStartInfo psi = new()
        {
            FileName = ytDlp,
            Arguments = "--skip-download"
                + $" {passFlag}"
                + " --sub-langs \"en\""
                + " --sub-format \"vtt/best\""
                + " --convert-subs vtt"
                + " --sleep-requests 0.75"
                + " --retries 5"
                + " --fragment-retries 5"
                + $" -o \"{outputTemplate}\""
                + $" \"{url}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        Process proc;
        try { proc = Process.Start(psi)!; }
        catch (Exception ex)
        {
            Log.LogWarning($"Subtitle import start failed: {ex.Message}");
            onDone(false, ex.Message);
            yield break;
        }

        while (!proc.HasExited)
            yield return new WaitForSecondsRealtime(0.2f);

        int exitCode = proc.ExitCode;
        string stderr = proc.StandardError.ReadToEnd().Trim();
        proc.Dispose();

        string vttBase = Path.GetFileNameWithoutExtension(outputTemplate);
        bool foundAny = Directory.GetFiles(SoundsFolder, vttBase + "*.vtt", SearchOption.TopDirectoryOnly).Length > 0;
        onDone(exitCode == 0 && foundAny, stderr);
    }

    // Marks a newly imported file for network sync and refreshes once all known clients have downloaded it.
    // fileName (string): Imported audio file name (including extension).
    // returns: IEnumerator
    private IEnumerator FinalizeImportedFile(string fileName)
    {
        string clipName = Path.GetFileNameWithoutExtension(fileName);
        PendingSyncClipNames.Add(clipName);
        BingBongNetworkSync.RegisterImportedFile(fileName);
        BingBongNetworkSync.AllowResync();

        if (!BingBongNetworkSync.IsHosting && !string.IsNullOrEmpty(BingBongNetworkSync.ActiveHostAddress)
            && AllowClientImports.Value)
        {
            string jsonName = Path.ChangeExtension(fileName, ".json");
            yield return StartCoroutine(BingBongNetworkSync.UploadFileToHost(fileName, BingBongNetworkSync.ActiveHostAddress));
            if (File.Exists(Path.Combine(SoundsFolder, jsonName)))
                yield return StartCoroutine(BingBongNetworkSync.UploadFileToHost(jsonName, BingBongNetworkSync.ActiveHostAddress));
        }

        float syncElapsed = 0f;
        float syncTimeout = 60f;
        string audioPath = Path.Combine(SoundsFolder, fileName);
        if (File.Exists(audioPath))
            syncTimeout = Math.Max(60f, 45f + (float)(new FileInfo(audioPath).Length / (256d * 1024d)));
        while (!BingBongNetworkSync.IsImportedFileSynced(fileName) && syncElapsed < syncTimeout)
        {
            ImportStatus = $"syncing to clients... {BingBongNetworkSync.GetPendingClientCount(fileName)} remaining";
            yield return new WaitForSecondsRealtime(0.25f);
            syncElapsed += 0.25f;
        }

        PendingSyncClipNames.Remove(clipName);
        ImportStatus = $"saved: {fileName}";
        StartRefresh();
    }

    // Downloads yt-dlp.exe from the official GitHub release into the plugin folder.
    // returns: IEnumerator
    private IEnumerator TryAutoDownloadYtDlpCoroutine()
    {
        string destPath = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty, "yt-dlp.exe");
        ImportStatus = "Downloading yt-dlp.exe...";
        using UnityWebRequest req = new UnityWebRequest("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", UnityWebRequest.kHttpVerbGET);
        req.downloadHandler = new DownloadHandlerBuffer();
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        {
            Log.LogWarning($"[YtDlp] Auto-download failed: {req.error}");
            yield break;
        }
        try
        {
            File.WriteAllBytes(destPath, req.downloadHandler.data);
            Log.LogInfo($"[YtDlp] Downloaded to {destPath}");
        }
        catch (Exception ex) { Log.LogWarning($"[YtDlp] Could not write yt-dlp.exe: {ex.Message}"); }
    }

    // Downloads ffmpeg.exe and ffprobe.exe from yt-dlp's FFmpeg-Builds into the given directory.
    // targetDir (string): Directory to extract ffmpeg.exe and ffprobe.exe into.
    // returns: IEnumerator
    private IEnumerator TryAutoDownloadFfmpegCoroutine(string targetDir)
    {
        ImportStatus = "Downloading ffmpeg...";
        using UnityWebRequest req = new UnityWebRequest(
            "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-essentials.zip",
            UnityWebRequest.kHttpVerbGET);
        req.downloadHandler = new DownloadHandlerBuffer();
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success)
        {
            Log.LogWarning($"[Ffmpeg] Auto-download failed: {req.error}");
            yield break;
        }
        try
        {
            using MemoryStream ms = new MemoryStream(req.downloadHandler.data);
            using ZipArchive zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = Path.GetFileName(entry.FullName);
                if (!name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(targetDir, name);
                using System.IO.Stream src = entry.Open();
                using FileStream dst = File.Create(dest);
                src.CopyTo(dst);
                Log.LogInfo($"[Ffmpeg] Extracted {name} to {dest}");
            }
        }
        catch (Exception ex) { Log.LogWarning($"[Ffmpeg] Could not extract ffmpeg: {ex.Message}"); }
    }

    // Finds ffmpeg.exe by checking the given preferred directory first, then PATH.
    // preferDir (string): Directory to check before PATH (usually the plugin folder).
    // returns: string
    private static string FindFfmpeg(string preferDir)
    {
        string[] candidates = [
            Path.Combine(preferDir, "ffmpeg.exe"), Path.Combine(preferDir, "ffmpeg"),
            "ffmpeg", "ffmpeg.exe",
        ];
        foreach (string candidate in candidates)
        {
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
                if (p != null) { p.WaitForExit(3000); if (p.ExitCode == 0) return candidate; }
            }
            catch (Exception) { }
        }
        return string.Empty;
    }

    // Finds the yt-dlp executable by checking PATH, the plugin folder, and common install locations.
    // returns: string
    private static string FindYtDlp()
    {
        string pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? string.Empty;
        string[] candidates = [
            Path.Combine(pluginDir, "yt-dlp.exe"), Path.Combine(pluginDir, "yt-dlp"),
            "yt-dlp", "yt-dlp.exe",
            Path.Combine(SoundsFolder, "..", "yt-dlp.exe"), Path.Combine(SoundsFolder, "..", "yt-dlp"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "yt-dlp", "yt-dlp.exe"),
        ];
        foreach (string candidate in candidates)
        {
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                if (p != null) { p.WaitForExit(3000); if (p.ExitCode == 0) return candidate; }
            }
            catch (Exception) { }
        }
        return string.Empty;
    }

    // Resolves the audio extension from payload signature, response headers, and URL as fallback.
    // request (UnityWebRequest): Completed UnityWebRequest with response headers.
    // uri (Uri): Parsed source URL.
    // data (byte[]): Downloaded bytes.
    // returns: string
    private string ResolveAudioExtension(UnityWebRequest request, Uri uri, byte[] data)
    {
        string signatureExt = DetectExtensionFromSignature(data);
        if (!string.IsNullOrWhiteSpace(signatureExt)) return signatureExt;

        string contentType = request.GetResponseHeader("Content-Type") ?? string.Empty;
        if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase)) return ".ogg";
        if (contentType.Contains("wav", StringComparison.OrdinalIgnoreCase)) return ".wav";
        if (contentType.Contains("audio", StringComparison.OrdinalIgnoreCase))
        {
            string urlExt = Path.GetExtension(uri.AbsolutePath);
            if (urlExt.Equals(".wav", StringComparison.OrdinalIgnoreCase)) return ".wav";
            return ".ogg";
        }
        return string.Empty;
    }

    // Detects container type directly from downloaded bytes.
    // data (byte[]): Downloaded file bytes.
    // returns: string
    private static string DetectExtensionFromSignature(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
            return ".ogg";
        if (data.Length >= 12 && data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F'
            && data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E')
            return ".wav";
        return string.Empty;
    }

    // Builds a best-effort base name using response filename metadata, query params, then URL path.
    // request (UnityWebRequest): Completed UnityWebRequest with response headers.
    // uri (Uri): Parsed source URL.
    // returns: string
    private string ResolveBaseNameFromLink(UnityWebRequest request, Uri uri)
    {
        string fromHeader = TryGetFileNameFromContentDisposition(request.GetResponseHeader("Content-Disposition"));
        if (!string.IsNullOrWhiteSpace(fromHeader)) return Path.GetFileNameWithoutExtension(fromHeader);

        string fromQuery = TryGetQueryValue(uri.Query, "title");
        if (string.IsNullOrWhiteSpace(fromQuery)) fromQuery = TryGetQueryValue(uri.Query, "filename");
        if (string.IsNullOrWhiteSpace(fromQuery)) fromQuery = TryGetQueryValue(uri.Query, "name");
        if (!string.IsNullOrWhiteSpace(fromQuery)) return fromQuery;

        string pathName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
        return !string.IsNullOrWhiteSpace(pathName) ? Uri.UnescapeDataString(pathName) : "imported_sound";
    }

    // Returns true when the URL is a YouTube or Twitch link that requires yt-dlp.
    // url (string): URL to inspect.
    // returns: bool
    private static bool IsYtDlpUrl(string url)
    {
        return url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase)
            || url.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
            || url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase)
            || url.Contains("clips.twitch.tv", StringComparison.OrdinalIgnoreCase);
    }

    // Extracts a filename from Content-Disposition when present.
    // header (string): Raw Content-Disposition header value.
    // returns: string
    private static string TryGetFileNameFromContentDisposition(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return string.Empty;
        Match utf8Match = Regex.Match(header, "filename\\*=UTF-8''(?<v>[^;]+)", RegexOptions.IgnoreCase);
        if (utf8Match.Success) return Uri.UnescapeDataString(utf8Match.Groups["v"].Value.Trim('"'));
        Match plainMatch = Regex.Match(header, "filename=(?<v>[^;]+)", RegexOptions.IgnoreCase);
        return plainMatch.Success ? plainMatch.Groups["v"].Value.Trim().Trim('"') : string.Empty;
    }

    // Normalizes a raw title into a stable filename-safe base.
    // name (string): Raw title/name string.
    // returns: string
    private static string NormalizeFileName(string name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "imported_sound" : name.Trim();
        value = Regex.Replace(value, "\\s+", "_");
        value = Regex.Replace(value, "[^A-Za-z0-9_-]", "_");
        value = Regex.Replace(value, "_+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(value)) value = "imported_sound";
        if (value.Length > 80) value = value.Substring(0, 80).Trim('_');
        return value.ToLowerInvariant();
    }

    // Extracts a query parameter value from a URI query string.
    // query (string): URI query string beginning with '?' or empty.
    // key (string): Query key to locate.
    // returns: string
    private static string TryGetQueryValue(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key)) return string.Empty;
        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (string pair in trimmed.Split('&'))
        {
            if (string.IsNullOrWhiteSpace(pair)) continue;
            string[] parts = pair.Split(['='], 2);
            if (!Uri.UnescapeDataString(parts[0]).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return parts.Length < 2 ? string.Empty : Uri.UnescapeDataString(parts[1].Replace('+', ' ')).Trim();
        }
        return string.Empty;
    }
}
