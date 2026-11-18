using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace NFDFix
{
    /// <summary>
    /// Shows the app version, project links and license notices.
    /// </summary>
    public sealed partial class AboutDialog : ContentDialog
    {
        public AboutDialog()
        {
            InitializeComponent();

            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            VersionText.Text = version is null ? string.Empty : Services.Localizer.Format("AboutVersion", version.ToString(3));
        }

        private async void OnLinkClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string url })
                return;

            try
            {
                await Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to open {url}: {ex}");
            }
        }
    }
}
