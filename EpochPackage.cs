using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using EnvDTE;
using EnvDTE80;
using EpochVisualStudio.Services;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace EpochVisualStudio
{
    /// <summary>
    /// Entry point for the epoch Visual Studio extension. This is the analogue of
    /// the VS Code extension's <c>activate()</c> function: it wires up logging,
    /// configuration, the status bar, the heartbeat tracker, and the command
    /// handlers. The package auto-loads in the background on startup (with and
    /// without a solution open), mirroring VS Code's <c>onStartupFinished</c>.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(EpochGuids.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class EpochPackage : AsyncPackage
    {
        private DTE2 _dte;
        private IVsOutputWindow _outputWindow;
        private StatusBarManager _statusBar;
        private HeartbeatManager _heartbeat;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Output pane (analogue of the VS Code output channel).
            _outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (_outputWindow != null)
            {
                var paneGuid = EpochGuids.OutputPane;
                _outputWindow.CreatePane(ref paneGuid, "epoch", 1, 1);
                _outputWindow.GetPane(ref paneGuid, out IVsOutputWindowPane pane);
                Logger.Initialize(pane);
            }

            Logger.Log("epoch extension activated");

            // Migrate any legacy config and ensure the shared config dir exists.
            Config.EnsureInitialized();

            var bar = await GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
            _statusBar = new StatusBarManager(bar);

            _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
            if (_dte != null)
            {
                _heartbeat = new HeartbeatManager(_dte, _statusBar);
            }
            else
            {
                Logger.Error("Could not obtain the DTE service; activity tracking is disabled.");
            }

            RegisterCommands(await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService);
        }

        private void RegisterCommands(OleMenuCommandService commandService)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (commandService == null)
            {
                Logger.Error("Could not obtain the menu command service; commands are unavailable.");
                return;
            }

            commandService.AddCommand(new MenuCommand(OnSetApiKey, new CommandID(EpochGuids.CmdSet, EpochGuids.CmdSetApiKey)));
            commandService.AddCommand(new MenuCommand(OnSetBaseUrl, new CommandID(EpochGuids.CmdSet, EpochGuids.CmdSetBaseUrl)));
            commandService.AddCommand(new MenuCommand(OnValidateApiKey, new CommandID(EpochGuids.CmdSet, EpochGuids.CmdValidateApiKey)));
            commandService.AddCommand(new MenuCommand(OnOpenDashboard, new CommandID(EpochGuids.CmdSet, EpochGuids.CmdOpenDashboard)));
            commandService.AddCommand(new MenuCommand(OnShowOutput, new CommandID(EpochGuids.CmdSet, EpochGuids.CmdShowOutput)));
        }

        private void OnSetApiKey(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Config.SetApiKey();
        }

        private void OnSetBaseUrl(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Config.SetBaseUrl();
        }

        private void OnValidateApiKey(object sender, EventArgs e)
        {
            _ = JoinableTaskFactory.RunAsync(() => Config.ValidateApiKeyAsync());
        }

        private void OnOpenDashboard(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var baseUrl = Config.GetBaseUrl();
            if (string.IsNullOrEmpty(baseUrl))
            {
                MessageBox.Show("No base URL configured for epoch.", "epoch",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(baseUrl + "/") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to open dashboard: " + ex.Message);
            }
        }

        private void OnShowOutput(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Logger.Activate();
            try
            {
                _dte?.ExecuteCommand("View.Output");
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to show output window: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _heartbeat?.Dispose();
                _statusBar?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
