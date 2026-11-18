using CommunityToolkit.Mvvm.ComponentModel;
using NFDFix.Services;

namespace NFDFix.ViewModels
{
    public enum UpdateState
    {
        /// <summary>No newer version known: never checked, or the last check found none.</summary>
        UpToDate,
        Checking,
        Available,
        Downloading,
        CheckFailed,
        DownloadFailed,
    }

    /// <summary>
    /// Update state shared by the title bar badge and the about dialog. Use it from the UI thread.
    /// </summary>
    public sealed partial class UpdateViewModel : ObservableObject
    {
        public static UpdateViewModel Current { get; } = new();

        private CancellationTokenSource? _downloadCts;

        private UpdateViewModel()
        {
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUpdate))]
        public partial UpdateState State { get; private set; }

        /// <summary>Gets the newer release, once a check has found one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUpdate))]
        public partial UpdateInfo? Update { get; private set; }

        [ObservableProperty]
        public partial long DownloadedBytes { get; private set; }

        /// <summary>Gets why the last check or download failed.</summary>
        [ObservableProperty]
        public partial UpdateError Error { get; private set; }

        /// <summary>Gets whether a newer version is known, for the title bar badge.</summary>
        public bool HasUpdate => Update is not null;

        /// <summary>
        /// Looks for a newer release. A <paramref name="quiet"/> check (at startup) keeps the
        /// current state on failure instead of showing an error.
        /// </summary>
        public async Task CheckAsync(bool quiet = false)
        {
            if (State is UpdateState.Checking or UpdateState.Downloading)
                return;

            var previous = State;
            State = UpdateState.Checking;
            try
            {
                Update = await UpdateService.CheckAsync();
                State = Update is null ? UpdateState.UpToDate : UpdateState.Available;
            }
            catch (UpdateException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
                if (quiet)
                {
                    State = previous;
                }
                else
                {
                    Error = ex.Error;
                    State = UpdateState.CheckFailed;
                }
            }
        }

        /// <summary>
        /// Downloads the installer and starts it. Returns <see langword="true"/> when the installer is
        /// running and the app should exit.
        /// </summary>
        public async Task<bool> DownloadAndInstallAsync()
        {
            if (Update?.Installer is not { } installer || State == UpdateState.Downloading)
                return false;

            using var cts = new CancellationTokenSource();
            _downloadCts = cts;
            DownloadedBytes = 0;
            State = UpdateState.Downloading;
            try
            {
                // Progress<T> raises on the UI thread that created it.
                var progress = new Progress<long>(bytes => DownloadedBytes = bytes);
                var path = await UpdateService.DownloadAsync(installer, progress, cts.Token);
                UpdateService.LaunchInstaller(path);
                return true;
            }
            catch (OperationCanceledException)
            {
                State = UpdateState.Available;
                return false;
            }
            catch (UpdateException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Update download failed: {ex}");
                Error = ex.Error;
                State = UpdateState.DownloadFailed;
                return false;
            }
            finally
            {
                _downloadCts = null;
            }
        }

        public void CancelDownload() => _downloadCts?.Cancel();
    }
}
