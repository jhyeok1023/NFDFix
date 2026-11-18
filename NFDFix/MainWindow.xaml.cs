using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace NFDFix
{
    /// <summary>
    /// The application window that hosts <see cref="MainPage"/>.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private const int DesignWidth = 780;
        private const int DesignHeight = 720;
        private const int MinWidth = 600;
        private const int MinHeight = 480;

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // A relative path would resolve against the working directory, not the exe folder.
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

            // AppWindow sizes are in physical pixels.
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
            AppWindow.Resize(new SizeInt32((int)(DesignWidth * scale), (int)(DesignHeight * scale)));
            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
                presenter.PreferredMinimumHeight = (int)(MinHeight * scale);
            }

            RootFrame.Navigate(typeof(MainPage));
        }

        [LibraryImport("user32.dll")]
        private static partial uint GetDpiForWindow(nint hwnd);
    }
}
