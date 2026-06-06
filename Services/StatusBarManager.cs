using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace EpochVisualStudio.Services
{
    /// <summary>
    /// Renders today's coding time into the Visual Studio status bar. This is the
    /// counterpart to the VS Code <c>StatusBarManager</c>. Visual Studio's status
    /// bar is a single shared text field (there is no rich, per-extension clickable
    /// item like VS Code), so we just write a text string and refresh it as the
    /// tracked time, online status, and API-key status change.
    /// </summary>
    internal sealed class StatusBarManager : IDisposable
    {
        private readonly IVsStatusbar _bar;

        private long _totalSeconds;
        private DateTime _trackingStartUtc;
        private bool _isTracking;
        private bool _isOnline = true;
        private bool _hasValidApiKey = true;

        public StatusBarManager(IVsStatusbar bar)
        {
            _bar = bar;
        }

        public void StartTracking()
        {
            if (!_isTracking)
            {
                _isTracking = true;
                _trackingStartUtc = DateTime.UtcNow;
                Update();
            }
        }

        public void StopTracking()
        {
            if (_isTracking)
            {
                _isTracking = false;
                Update();
            }
        }

        public void UpdateTime(int hours, int minutes)
        {
            _totalSeconds = hours * 3600L + minutes * 60L;
            _trackingStartUtc = DateTime.UtcNow;
            Update();
        }

        public void SetOnlineStatus(bool isOnline)
        {
            _isOnline = isOnline;
            Update();
        }

        public void SetApiKeyStatus(bool isValid)
        {
            _hasValidApiKey = isValid;
            Update();
        }

        private void Update()
        {
            string text = BuildText();
            // SetText must run on the UI thread.
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    _bar.IsFrozen(out int frozen);
                    if (frozen == 0)
                    {
                        _bar.SetText(text);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Failed to update status bar: " + ex.Message);
                }
            });
        }

        private string BuildText()
        {
            if (!_hasValidApiKey)
            {
                return "epoch: ✕ Unconfigured (set API key)";
            }

            long displaySeconds = _totalSeconds;
            if (_isTracking)
            {
                displaySeconds += (long)(DateTime.UtcNow - _trackingStartUtc).TotalSeconds;
            }

            long hours = displaySeconds / 3600;
            long minutes = (displaySeconds % 3600) / 60;

            if (!_isOnline)
            {
                return string.Format("epoch: {0} hrs {1} mins (offline)", hours, minutes);
            }

            return string.Format("epoch: {0} hrs {1} mins", hours, minutes);
        }

        public void Dispose()
        {
            // The status bar is owned by the shell; nothing to release here.
        }
    }
}
