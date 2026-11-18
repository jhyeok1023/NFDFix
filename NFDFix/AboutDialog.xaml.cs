using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NFDFix.Services;
using NFDFix.ViewModels;
using Windows.System;

namespace NFDFix
{
    /// <summary>
    /// Shows the app version, update status, project links and license notices.
    /// </summary>
    public sealed partial class AboutDialog : ContentDialog
    {
        private UpdateViewModel Updates => UpdateViewModel.Current;

        public AboutDialog()
        {
            InitializeComponent();

            VersionText.Text = Localizer.Format("AboutVersion", UpdateService.CurrentVersion.ToString(3));

            Updates.PropertyChanged += OnUpdatesChanged;
            Closed += (_, _) => Updates.PropertyChanged -= OnUpdatesChanged;
            ApplyUpdateState();
        }

        private void OnUpdatesChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UpdateViewModel.DownloadedBytes))
                ApplyDownloadProgress();
            else
                ApplyUpdateState();
        }

        private void ApplyUpdateState()
        {
            var state = Updates.State;
            var update = Updates.Update;
            bool hasInstaller = update?.Installer is not null;

            bool failed = state is UpdateState.CheckFailed or UpdateState.DownloadFailed;
            bool hasNewVersion = state is UpdateState.Available or UpdateState.Downloading;

            UpdateRing.IsActive = state == UpdateState.Checking;
            UpdateRing.Visibility = VisibleIf(state == UpdateState.Checking);
            UpToDateIcon.Visibility = VisibleIf(state == UpdateState.UpToDate);
            AvailableIcon.Visibility = VisibleIf(hasNewVersion);
            FailedIcon.Visibility = VisibleIf(failed);

            UpdateTitle.Text = state switch
            {
                UpdateState.Checking => Localizer.GetString("UpdateCheckingTitle"),
                UpdateState.Available => Localizer.Format("UpdateAvailableTitle", update!.Version.ToString(3)),
                UpdateState.Downloading => DownloadTitle(),
                UpdateState.CheckFailed => Localizer.GetString("UpdateCheckFailedTitle"),
                UpdateState.DownloadFailed => Localizer.GetString("UpdateDownloadFailedTitle"),
                _ => Localizer.GetString("UpdateUpToDateTitle"),
            };

            UpdateDescription.Text = state switch
            {
                UpdateState.Checking => Localizer.GetString("UpdateCheckingDescription"),
                UpdateState.CheckFailed or UpdateState.DownloadFailed => Localizer.GetString(Updates.Error switch
                {
                    UpdateError.Corrupt => "UpdateErrorCorrupt",
                    UpdateError.Launch => "UpdateErrorLaunch",
                    _ => "UpdateErrorNetwork",
                }),
                _ => LastCheckedText(),
            };
            UpdateDescription.Visibility = VisibleIf(!hasNewVersion);
            ReleaseNotesLink.Visibility = VisibleIf(state == UpdateState.Available);

            UpdateProgress.Visibility = VisibleIf(state == UpdateState.Downloading);
            if (state == UpdateState.Downloading)
                ApplyDownloadProgress();

            // The accent button is the call to action when a new version is ready to install.
            bool accent = state == UpdateState.Available;
            var button = accent ? UpdateAccentButton : UpdateButton;
            var other = accent ? UpdateButton : UpdateAccentButton;
            // Hiding the focused button would send focus to the next control; keep it on the update button.
            bool moveFocus = other.FocusState != FocusState.Unfocused;
            button.Visibility = Visibility.Visible;
            other.Visibility = Visibility.Collapsed;
            if (moveFocus)
                button.Focus(FocusState.Programmatic);
            button.Content = Localizer.GetString(state switch
            {
                UpdateState.Available => hasInstaller ? "UpdateInstallButton" : "UpdateOpenPageButton",
                UpdateState.Downloading => "UpdateCancelButton",
                UpdateState.CheckFailed or UpdateState.DownloadFailed => "UpdateRetryButton",
                _ => "UpdateCheckButton",
            });
            button.IsEnabled = state != UpdateState.Checking;
        }

        private static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

        private void ApplyDownloadProgress()
        {
            long total = Updates.Update?.Installer?.Size ?? 0;
            UpdateProgress.IsIndeterminate = total <= 0;
            UpdateProgress.Value = total > 0 ? Math.Min(100.0, Updates.DownloadedBytes * 100.0 / total) : 0;
            UpdateTitle.Text = DownloadTitle();
        }

        private string DownloadTitle()
        {
            long total = Updates.Update?.Installer?.Size ?? 0;
            return total > 0
                ? Localizer.Format("UpdateDownloadingTitle", FormatSize(Updates.DownloadedBytes), FormatSize(total))
                : Localizer.GetString("UpdateDownloadingTitleNoSize");
        }

        private static string LastCheckedText()
        {
            if (UpdateService.LastChecked is not { } time)
                return Localizer.GetString("UpdateNeverChecked");

            return time.Date == DateTime.Today
                ? Localizer.Format("UpdateLastCheckedToday", time.ToString("t", CultureInfo.CurrentCulture))
                : Localizer.Format("UpdateLastChecked", time.ToString("g", CultureInfo.CurrentCulture));
        }

        private static string FormatSize(long bytes) =>
            (bytes / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB";

        private async void OnUpdateButtonClick(object sender, RoutedEventArgs e)
        {
            switch (Updates.State)
            {
                case UpdateState.Downloading:
                    Updates.CancelDownload();
                    break;

                case UpdateState.Available when Updates.Update is { Installer: null } update:
                    await OpenAsync(update.ReleasePage);
                    break;

                case UpdateState.Available or UpdateState.DownloadFailed:
                    if (await Updates.DownloadAndInstallAsync())
                        Application.Current.Exit();
                    break;

                default:
                    await Updates.CheckAsync();
                    break;
            }
        }

        private async void OnReleaseNotesClick(object sender, RoutedEventArgs e)
        {
            if (Updates.Update is { } update)
                await OpenAsync(update.ReleasePage);
        }

        private async void OnLinkClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string url })
                await OpenAsync(new Uri(url));
        }

        private static async Task OpenAsync(Uri uri)
        {
            try
            {
                await Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open {uri}: {ex}");
            }
        }
    }
}
