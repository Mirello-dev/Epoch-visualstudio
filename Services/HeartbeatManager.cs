using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace EpochVisualStudio.Services
{
    [DataContract]
    internal class Heartbeat
    {
        [DataMember(Name = "timestamp", Order = 1)]
        public string Timestamp { get; set; }

        [DataMember(Name = "ide", Order = 2)]
        public string Ide { get; set; }

        [DataMember(Name = "os", Order = 3)]
        public string Os { get; set; }

        [DataMember(Name = "project", Order = 4)]
        public string Project { get; set; }

        [DataMember(Name = "language", Order = 5)]
        public string Language { get; set; }

        [DataMember(Name = "file", Order = 6)]
        public string File { get; set; }

        [DataMember(Name = "duration_seconds", Order = 7)]
        public int DurationSeconds { get; set; }
    }

    /// <summary>
    /// The heart of the extension: watches editor activity, accumulates the time
    /// the user is actively coding while Visual Studio is focused, and POSTs
    /// periodic heartbeats to the configured epoch instance. Heartbeats that
    /// cannot be sent are queued to disk and flushed when connectivity returns.
    ///
    /// This is a direct port of the VS Code <c>HeartbeatManager</c>:
    ///   - VS Code editor/document/window events  -> EnvDTE WindowEvents /
    ///     TextEditorEvents / DocumentEvents
    ///   - <c>window.state.focused</c>             -> Win32 foreground-window check
    ///   - the git extension API                   -> parsing <c>.git/config</c>
    /// The on-disk config and offline-queue formats are identical to VS Code's,
    /// so both editors share one <c>~/.config/epoch</c> directory.
    /// </summary>
    internal sealed class HeartbeatManager : IDisposable
    {
        private const int HeartbeatIntervalMs = 120000;
        private const int InactivityThresholdMs = 15 * 60 * 1000;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly DTE2 _dte;
        private readonly StatusBarManager _statusBar;
        private readonly string _offlineQueuePath;

        // EnvDTE event sources must be held in fields, otherwise they are garbage
        // collected and the handlers silently stop firing.
        private Events _events;
        private WindowEvents _windowEvents;
        private TextEditorEvents _textEditorEvents;
        private DocumentEvents _documentEvents;

        private IntPtr _mainHwnd;
        private string _ideName = "Visual Studio";
        private string _osName = "Windows";

        private Timer _accumulatorTimer;
        private Timer _heartbeatTimer;
        private Timer _statsTimer;

        private readonly object _lock = new object();
        private readonly object _offlineLock = new object();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        // Cached active-document info (written on the UI thread by event handlers,
        // read by background timers).
        private string _activeFile;
        private string _activeFilePath;
        private string _activeLanguage;

        private long _lastHeartbeatMs;
        private string _lastFile = string.Empty;
        private long _lastActivityMs;
        private long _lastTimeAccumulatedMs;

        private bool _isWindowFocused = true;
        private bool _isOnline = true;
        private bool _hasValidApiKey = true;

        private int _heartbeatCount;
        private int _successCount;
        private int _failureCount;

        private long _todayLocalTotalSeconds;
        private long _activeSecondsSinceLastHeartbeat;
        private string _currentDay = DateTime.Now.ToString("yyyy-MM-dd");

        private readonly List<Heartbeat> _offlineHeartbeats = new List<Heartbeat>();

        public HeartbeatManager(DTE2 dte, StatusBarManager statusBar)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _dte = dte;
            _statusBar = statusBar;
            _offlineQueuePath = Path.Combine(Config.ConfigDir, "offline_heartbeats.json");

            Config.EnsureConfigDir();
            MigrateOfflineHeartbeats();
            LoadOfflineHeartbeats();
            Initialize();
        }

        private static long NowMs()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        private void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                _ideName = "Visual Studio " + _dte.Version;
                _mainHwnd = (IntPtr)_dte.MainWindow.HWnd;
                _isWindowFocused = GetForegroundWindow() == _mainHwnd;
            }
            catch (Exception ex)
            {
                Logger.Error("Could not read DTE main window: " + ex.Message);
            }

            _lastActivityMs = NowMs();
            _lastTimeAccumulatedMs = NowMs();

            RegisterEventListeners();

            _accumulatorTimer = new Timer(_ => Safe(AccumulatorTick), null, 5000, 5000);
            _heartbeatTimer = new Timer(_ => Safe(HeartbeatTick), null, HeartbeatIntervalMs, HeartbeatIntervalMs);
            _statsTimer = new Timer(_ => Safe(LogStats), null, 15 * 60 * 1000, 15 * 60 * 1000);

            _statusBar.SetOnlineStatus(_isOnline);
            _statusBar.SetApiKeyStatus(_hasValidApiKey);
            if (_isWindowFocused)
            {
                _statusBar.StartTracking();
            }

            _ = SyncOfflineHeartbeatsAsync();
            Logger.Log("Heartbeat manager initialized (" + _ideName + ")");
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Error("Timer callback failed: " + ex.Message);
            }
        }

        private void RegisterEventListeners()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Logger.Log("Registering event listeners for editor changes");

            _events = _dte.Events;
            _windowEvents = _events.get_WindowEvents(null);
            _textEditorEvents = _events.get_TextEditorEvents(null);
            _documentEvents = _events.get_DocumentEvents(null);

            _windowEvents.WindowActivated += OnWindowActivated;
            _textEditorEvents.LineChanged += OnLineChanged;
            _documentEvents.DocumentSaved += OnDocumentSaved;

            CaptureActiveDocument();
        }

        private void CaptureActiveDocument()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = _dte.ActiveDocument;
                if (doc == null)
                {
                    return;
                }

                lock (_lock)
                {
                    _activeFilePath = doc.FullName;
                    _activeFile = Path.GetFileName(doc.FullName);
                    _activeLanguage = SafeLanguage(doc);
                }
            }
            catch
            {
                // No active document yet — ignore.
            }
        }

        private static string SafeLanguage(Document doc)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                return doc.Language ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private void RecordUserInteraction()
        {
            lock (_lock)
            {
                _lastActivityMs = NowMs();
            }
            if (_isWindowFocused)
            {
                _statusBar.StartTracking();
            }
        }

        private void OnWindowActivated(Window gotFocus, Window lostFocus)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = _dte.ActiveDocument;
                if (doc == null)
                {
                    return;
                }

                Logger.Log("Editor changed: " + doc.FullName + " (" + SafeLanguage(doc) + ")");
                lock (_lock)
                {
                    _activeFilePath = doc.FullName;
                    _activeFile = Path.GetFileName(doc.FullName);
                    _activeLanguage = SafeLanguage(doc);
                }
                RecordUserInteraction();
                _ = SendHeartbeatAsync(force: true);
            }
            catch
            {
                // Activated window was not a document (e.g. a tool window).
            }
        }

        private void OnLineChanged(TextPoint startPoint, TextPoint endPoint, int hint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var doc = _dte.ActiveDocument;
                if (doc == null)
                {
                    return;
                }

                lock (_lock)
                {
                    _activeFilePath = doc.FullName;
                    _activeFile = Path.GetFileName(doc.FullName);
                    _activeLanguage = SafeLanguage(doc);
                }
                RecordUserInteraction();

                long now = NowMs();
                bool fileChanged;
                bool timeThresholdPassed;
                lock (_lock)
                {
                    fileChanged = _lastFile != doc.FullName;
                    timeThresholdPassed = now - _lastHeartbeatMs >= HeartbeatIntervalMs;
                }
                if (fileChanged || timeThresholdPassed)
                {
                    _ = SendHeartbeatAsync(force: false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("OnLineChanged failed: " + ex.Message);
            }
        }

        private void OnDocumentSaved(Document document)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (document == null)
                {
                    return;
                }
                lock (_lock)
                {
                    _activeFilePath = document.FullName;
                    _activeFile = Path.GetFileName(document.FullName);
                    _activeLanguage = SafeLanguage(document);
                }
                RecordUserInteraction();
                _ = SendHeartbeatAsync(force: true);
            }
            catch (Exception ex)
            {
                Logger.Error("OnDocumentSaved failed: " + ex.Message);
            }
        }

        private void AccumulatorTick()
        {
            long now = NowMs();
            RolloverDayIfNeeded();

            bool focused = GetForegroundWindow() == _mainHwnd;
            bool wasFocused = _isWindowFocused;
            _isWindowFocused = focused;

            if (!focused && wasFocused)
            {
                _statusBar.StopTracking();
            }
            else if (focused && !wasFocused)
            {
                lock (_lock)
                {
                    _lastActivityMs = now;
                }
                _statusBar.StartTracking();
            }

            if (focused)
            {
                long sinceInteraction;
                lock (_lock)
                {
                    sinceInteraction = now - _lastActivityMs;
                }

                if (sinceInteraction < InactivityThresholdMs)
                {
                    long elapsedSeconds = (now - _lastTimeAccumulatedMs) / 1000;
                    if (elapsedSeconds > 0)
                    {
                        lock (_lock)
                        {
                            _activeSecondsSinceLastHeartbeat += elapsedSeconds;
                            _todayLocalTotalSeconds += elapsedSeconds;
                        }
                        RefreshStatusBarTime();
                    }
                }
            }

            _lastTimeAccumulatedMs = now;
        }

        private void HeartbeatTick()
        {
            long now = NowMs();
            bool hasDoc;
            bool userActive;
            lock (_lock)
            {
                hasDoc = _activeFilePath != null;
                userActive = _isWindowFocused && (now - _lastActivityMs < InactivityThresholdMs);
            }

            if (hasDoc && userActive)
            {
                _ = SendHeartbeatAsync(force: false);
                if (_isWindowFocused)
                {
                    _statusBar.StartTracking();
                }
            }
            else
            {
                _statusBar.StopTracking();
            }
        }

        private void LogStats()
        {
            int offline;
            lock (_offlineLock)
            {
                offline = _offlineHeartbeats.Count;
            }
            Logger.Log(string.Format(
                "Heartbeat stats - Total: {0}, Success: {1}, Failed: {2}, Offline: {3}",
                _heartbeatCount, _successCount, _failureCount, offline));
        }

        private void RolloverDayIfNeeded()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (today != _currentDay)
            {
                _currentDay = today;
                lock (_lock)
                {
                    _todayLocalTotalSeconds = 0;
                }
                RefreshStatusBarTime();
            }
        }

        private void RefreshStatusBarTime()
        {
            long total;
            lock (_lock)
            {
                total = _todayLocalTotalSeconds;
            }
            int hours = (int)(total / 3600);
            int minutes = (int)((total % 3600) / 60);
            _statusBar.UpdateTime(hours, minutes);
        }

        private async Task SendHeartbeatAsync(bool force)
        {
            if (!_sendGate.Wait(0))
            {
                return; // A send is already in flight.
            }

            try
            {
                string file, filePath, language;
                long now = NowMs();
                bool fileChanged, timeThresholdPassed;

                lock (_lock)
                {
                    file = _activeFile;
                    filePath = _activeFilePath;
                    language = _activeLanguage;
                    fileChanged = _lastFile != filePath;
                    timeThresholdPassed = now - _lastHeartbeatMs >= HeartbeatIntervalMs;
                }

                if (filePath == null)
                {
                    return;
                }
                if (!force && !fileChanged && !timeThresholdPassed)
                {
                    return;
                }

                string project = GetProjectName(filePath);
                if (string.IsNullOrEmpty(project))
                {
                    Logger.Log("No project name found for the current file, skipping heartbeat");
                    return;
                }

                string apiKey = Config.GetApiKey();
                string baseUrl = Config.GetBaseUrl();
                if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl))
                {
                    return;
                }

                long durationSeconds;
                lock (_lock)
                {
                    _lastFile = filePath;
                    _lastHeartbeatMs = now;
                    _heartbeatCount++;
                    durationSeconds = _activeSecondsSinceLastHeartbeat;
                    _activeSecondsSinceLastHeartbeat = 0;
                }

                if (durationSeconds <= 0)
                {
                    Logger.Log("Skipping heartbeat: no active seconds accumulated (duration_seconds = 0)");
                    return;
                }

                var heartbeat = new Heartbeat
                {
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Ide = _ideName,
                    Os = _osName,
                    Project = project,
                    Language = language ?? "Unknown",
                    File = file,
                    DurationSeconds = (int)durationSeconds,
                };

                if (!_isOnline)
                {
                    EnqueueOffline(heartbeat);
                    return;
                }

                try
                {
                    await PostHeartbeatAsync(heartbeat, apiKey, baseUrl).ConfigureAwait(false);
                    _successCount++;
                    SetOnlineStatus(true);
                    SetApiKeyStatus(true);
                    Logger.Log(string.Format("Heartbeat sent successfully for {0} ({1}) in project {2}",
                        heartbeat.File, heartbeat.Language, heartbeat.Project));
                }
                catch (InvalidApiKeyException)
                {
                    SetApiKeyStatus(false);
                    EnqueueOffline(heartbeat);
                    Logger.Error("Failed to send heartbeat: invalid API key");
                }
                catch (Exception ex)
                {
                    _failureCount++;
                    SetOnlineStatus(false);
                    EnqueueOffline(heartbeat);
                    Logger.Error("Failed to send heartbeat: " + ex.Message);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task SyncOfflineHeartbeatsAsync()
        {
            string apiKey = Config.GetApiKey();
            string baseUrl = Config.GetBaseUrl();
            if (!_isOnline || string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl))
            {
                return;
            }

            Logger.Log("Syncing offline heartbeats to " + baseUrl);

            while (true)
            {
                Heartbeat next;
                lock (_offlineLock)
                {
                    if (_offlineHeartbeats.Count == 0)
                    {
                        break;
                    }
                    next = _offlineHeartbeats[0];
                }

                try
                {
                    // The API exposes no batch endpoint, so flush one at a time.
                    await PostHeartbeatAsync(next, apiKey, baseUrl).ConfigureAwait(false);
                    lock (_offlineLock)
                    {
                        if (_offlineHeartbeats.Count > 0)
                        {
                            _offlineHeartbeats.RemoveAt(0);
                        }
                        SaveOfflineHeartbeats();
                    }
                    SetApiKeyStatus(true);
                }
                catch (InvalidApiKeyException)
                {
                    SetApiKeyStatus(false);
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error("Error syncing offline heartbeat: " + ex.Message);
                    SetOnlineStatus(false);
                    break;
                }
            }
        }

        private sealed class InvalidApiKeyException : Exception
        {
        }

        private async Task PostHeartbeatAsync(Heartbeat heartbeat, string apiKey, string baseUrl)
        {
            var url = new Uri(new Uri(baseUrl), "/api/v1/heartbeat");
            string json = JsonUtil.Serialize(heartbeat);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        throw new InvalidApiKeyException();
                    }
                    throw new Exception("Failed with status code: " + (int)response.StatusCode);
                }
            }
        }

        private void EnqueueOffline(Heartbeat heartbeat)
        {
            lock (_offlineLock)
            {
                _offlineHeartbeats.Add(heartbeat);
                SaveOfflineHeartbeats();
            }
        }

        private void LoadOfflineHeartbeats()
        {
            try
            {
                if (File.Exists(_offlineQueuePath))
                {
                    var loaded = JsonUtil.Deserialize<List<Heartbeat>>(File.ReadAllText(_offlineQueuePath));
                    lock (_offlineLock)
                    {
                        _offlineHeartbeats.Clear();
                        if (loaded != null)
                        {
                            foreach (var h in loaded)
                            {
                                if (h.DurationSeconds > 0)
                                {
                                    _offlineHeartbeats.Add(h);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error loading offline heartbeats: " + ex.Message);
            }
        }

        private void SaveOfflineHeartbeats()
        {
            // Caller already holds _offlineLock.
            try
            {
                File.WriteAllText(_offlineQueuePath, JsonUtil.Serialize(_offlineHeartbeats));
            }
            catch (Exception ex)
            {
                Logger.Error("Error saving offline heartbeats: " + ex.Message);
            }
        }

        private void MigrateOfflineHeartbeats()
        {
            try
            {
                var legacyPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".epoch", "offline_heartbeats.json");

                if (File.Exists(legacyPath) && !File.Exists(_offlineQueuePath))
                {
                    File.Copy(legacyPath, _offlineQueuePath, overwrite: false);
                    File.Delete(legacyPath);
                    Logger.Log("Migrated offline heartbeats from " + legacyPath + " to " + _offlineQueuePath);

                    try
                    {
                        var legacyDir = Path.GetDirectoryName(legacyPath);
                        if (legacyDir != null && Directory.Exists(legacyDir) &&
                            Directory.GetFileSystemEntries(legacyDir).Length == 0)
                        {
                            Directory.Delete(legacyDir);
                        }
                    }
                    catch { /* best effort */ }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Error migrating offline heartbeats: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolves a project name for the file by walking up to the nearest
        /// <c>.git</c> directory and reading the <c>origin</c> remote URL from its
        /// config. Falls back to the repository folder name, then the file's own
        /// containing folder name. Replaces the VS Code extension's use of the
        /// built-in git extension API.
        /// </summary>
        private string GetProjectName(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                while (!string.IsNullOrEmpty(dir))
                {
                    var gitPath = Path.Combine(dir, ".git");
                    if (Directory.Exists(gitPath) || File.Exists(gitPath))
                    {
                        var fromRemote = ProjectNameFromGitConfig(Path.Combine(gitPath, "config"));
                        if (!string.IsNullOrEmpty(fromRemote))
                        {
                            return fromRemote;
                        }
                        return new DirectoryInfo(dir).Name;
                    }

                    var parent = Path.GetDirectoryName(dir);
                    if (parent == dir)
                    {
                        break;
                    }
                    dir = parent;
                }

                var fileDir = Path.GetDirectoryName(filePath);
                return string.IsNullOrEmpty(fileDir) ? null : new DirectoryInfo(fileDir).Name;
            }
            catch (Exception ex)
            {
                Logger.Error("Error getting project name: " + ex.Message);
                return null;
            }
        }

        private static string ProjectNameFromGitConfig(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    return null;
                }

                string originUrl = null;
                bool inOrigin = false;
                foreach (var raw in File.ReadAllLines(configPath))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        inOrigin = line.Replace(" ", "").Equals("[remote\"origin\"]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (inOrigin && line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = line.IndexOf('=');
                        if (idx >= 0)
                        {
                            originUrl = line.Substring(idx + 1).Trim();
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(originUrl))
                {
                    return null;
                }

                int sep = Math.Max(originUrl.LastIndexOf('/'), originUrl.LastIndexOf(':'));
                if (sep < 0)
                {
                    return null;
                }
                var name = originUrl.Substring(sep + 1);
                if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - 4);
                }
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        private void SetOnlineStatus(bool isOnline)
        {
            if (_isOnline != isOnline)
            {
                _isOnline = isOnline;
                _statusBar.SetOnlineStatus(isOnline);
                Logger.Log("Online status changed to: " + (isOnline ? "online" : "offline"));
                if (isOnline)
                {
                    _ = SyncOfflineHeartbeatsAsync();
                }
            }
        }

        private void SetApiKeyStatus(bool isValid)
        {
            if (_hasValidApiKey != isValid)
            {
                _hasValidApiKey = isValid;
                _statusBar.SetApiKeyStatus(isValid);
                Logger.Log("API key status changed to: " + (isValid ? "valid" : "invalid"));
            }
        }

        public void Dispose()
        {
            _accumulatorTimer?.Dispose();
            _heartbeatTimer?.Dispose();
            _statsTimer?.Dispose();

            if (_windowEvents != null)
            {
                _windowEvents.WindowActivated -= OnWindowActivated;
            }
            if (_textEditorEvents != null)
            {
                _textEditorEvents.LineChanged -= OnLineChanged;
            }
            if (_documentEvents != null)
            {
                _documentEvents.DocumentSaved -= OnDocumentSaved;
            }

            lock (_offlineLock)
            {
                SaveOfflineHeartbeats();
            }
        }
    }
}
