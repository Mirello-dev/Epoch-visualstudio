using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.Interop;

namespace EpochVisualStudio.Services
{
    /// <summary>
    /// Writes diagnostic lines to a dedicated "epoch" pane in the Visual Studio
    /// Output window. Mirrors the VS Code extension's output channel
    /// (<c>log</c> / <c>error</c> / <c>showOutputChannel</c>).
    ///
    /// <see cref="IVsOutputWindowPane.OutputStringThreadSafe"/> is safe to call
    /// from any thread, so callers do not need to switch to the UI thread to log.
    /// Messages logged before the pane exists are buffered and flushed on
    /// <see cref="Initialize"/>.
    /// </summary>
    internal static class Logger
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Buffer = new List<string>();
        private static IVsOutputWindowPane _pane;

        public static void Initialize(IVsOutputWindowPane pane)
        {
            lock (Gate)
            {
                _pane = pane;
                foreach (var line in Buffer)
                {
                    _pane.OutputStringThreadSafe(line);
                }
                Buffer.Clear();
            }
        }

        public static void Log(string message)
        {
            string line = "[" + DateTime.Now + "] " + message + Environment.NewLine;
            lock (Gate)
            {
                if (_pane != null)
                {
                    _pane.OutputStringThreadSafe(line);
                }
                else
                {
                    Buffer.Add(line);
                }
            }
        }

        public static void Error(string message)
        {
            Log("ERROR: " + message);
        }

        /// <summary>Brings the epoch output pane to the front. Must be called on the UI thread.</summary>
        public static void Activate()
        {
            _pane?.Activate();
        }
    }
}
