using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using NFDFix.Services;
using NFDFix.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace NFDFix
{
    /// <summary>The text of a language menu item, shown by <c>LanguageMenuItemTemplate</c>.</summary>
    public sealed record LanguageMenuEntry(string Name, string Hint);

    /// <summary>
    /// The main content page displayed inside the application window.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        public MainPageViewModel ViewModel { get; } = new();

        public UpdateViewModel Updates => UpdateViewModel.Current;

        public MainPage()
        {
            InitializeComponent();

            // Shortcuts honor CanExecute like the buttons do; a command still running (an open picker) can't start again.
            AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, ViewModel.AddFilesCommand);
            AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, ViewModel.AddFoldersCommand);
            AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, ViewModel.RescanCommand);

            CurrentLanguageText.Text = Localizer.GetString("LanguageCurrentName");
            BuildLanguageMenu();

            // Supports dropping items onto the executable and the "Send to" menu.
            Loaded += OnFirstLoaded;
        }

        /// <summary>
        /// Adds paths passed on the command line and checks for updates in the background.
        /// Runs only once, even if the page is loaded again.
        /// </summary>
        private async void OnFirstLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnFirstLoaded;
            Updates.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(UpdateViewModel.HasUpdate))
                    UpdateAboutButtonName();
            };
            _ = Task.Run(UpdateService.DeleteOldDownloads);
            _ = Updates.CheckAsync(quiet: true);

            try
            {
                var args = Environment.GetCommandLineArgs().Skip(1).Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
                if (args.Count > 0)
                    await ViewModel.AddPathsAsync(args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to add command-line paths: {ex}");
            }
        }

        private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, System.Windows.Input.ICommand command)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, e) =>
            {
                if (command.CanExecute(null))
                    command.Execute(null);
                e.Handled = true;
            };
            KeyboardAccelerators.Add(accelerator);
        }

        /// <summary>Fills the language menu: "system default", a separator, then each supported language.</summary>
        private void BuildLanguageMenu()
        {
            LanguageItemsPanel.Children.Add(CreateLanguageItem(string.Empty, Localizer.GetString("LanguageSystem"), Localizer.GetString("LanguageSystemHint")));
            // Styled by the implicit styles on LanguageItemsPanel.
            LanguageItemsPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Margin = new Thickness(0, 4, 0, 4) });
            foreach (var language in Localizer.SupportedLanguages)
                LanguageItemsPanel.Children.Add(CreateLanguageItem(language.Tag, language.NativeName, Localizer.GetString(language.HintKey)));
        }

        /// <summary>Creates a language menu item; its look comes from the implicit style and template in XAML.</summary>
        private RadioButton CreateLanguageItem(string tag, string name, string hint)
        {
            var item = new RadioButton
            {
                GroupName = "Language",
                Tag = tag,
                Content = new LanguageMenuEntry(name, hint),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, name);
            item.Click += OnLanguageItemClick;
            return item;
        }

        private void OnLanguageFlyoutOpening(object? sender, object e)
        {
            foreach (var item in LanguageItemsPanel.Children.OfType<RadioButton>())
                item.IsChecked = (string)item.Tag == Localizer.PreferredLanguage;
        }

        private void OnLanguageItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton { Tag: string tag })
                Localizer.SetPreferredLanguage(tag);
            LanguageFlyout.Hide();
        }

        /// <summary>Says in the tooltip and to screen readers that a newer version is available, since the badge only shows it visually.</summary>
        private void UpdateAboutButtonName()
        {
            var name = Localizer.GetString(Updates.HasUpdate ? "AboutButtonUpdateAvailable" : "AboutButtonName");
            ToolTipService.SetToolTip(AboutButton, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AboutButton, name);
        }

        private async void OnAboutClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await new AboutDialog { XamlRoot = XamlRoot }.ShowAsync();
            }
            catch (Exception ex)
            {
                // ShowAsync throws if another dialog is already open.
                System.Diagnostics.Debug.WriteLine($"Failed to show the about dialog: {ex}");
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel.SelectedCount = ItemsList.SelectedItems.Count;
        }

        private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Delete && ViewModel.RemoveSelectedCommand.CanExecute(ItemsList.SelectedItems))
            {
                ViewModel.RemoveSelectedCommand.Execute(ItemsList.SelectedItems);
                e.Handled = true;
            }
        }


        private void OnDragOver(object sender, DragEventArgs e)
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            e.AcceptedOperation = DataPackageOperation.Link;
            if (e.DragUIOverride is { } ui)
            {
                ui.Caption = Localizer.GetString("DropCaption");
                ui.IsGlyphVisible = false;
            }
            DropZoneFill.StrokeThickness = 2;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            DropZoneFill.StrokeThickness = 1.5;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            DropZoneFill.StrokeThickness = 1.5;
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;

            var deferral = e.GetDeferral();
            IReadOnlyList<IStorageItem> items;
            try
            {
                items = await e.DataView.GetStorageItemsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to read dropped items: {ex}");
                return;
            }
            finally
            {
                deferral.Complete();
            }

            await ViewModel.AddPathsAsync(items.Select(i => i.Path));
        }
    }
}
