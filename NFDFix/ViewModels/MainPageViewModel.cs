using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.Storage.Pickers;
using NFDFix.Models;
using NFDFix.Services;

namespace NFDFix.ViewModels
{
    /// <summary>
    /// View model for <see cref="MainPage"/>.
    /// </summary>
    public sealed partial class MainPageViewModel : ObservableObject
    {
        /// <summary>Above this many removals the list is rebuilt instead of removing items one by one.</summary>
        private const int BulkRemoveThreshold = 64;

        private readonly List<string> _roots = [];
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _scanCts;
        private int _foldersScanned;

        // Set when options or roots change during a conversion; the rescan runs once it finishes.
        private bool _rescanPending;

        // While > 0, per-item changes don't trigger a recount; the caller raises once at the end.
        private int _suppressItemNotifications;

        // Cached counts, refreshed in a single pass by RaiseItemStateChanged.
        private int _pendingCount;
        private int _conflictCount;
        private int _checkedPendingCount;

        private ObservableCollection<RenameItem> _items = [];

        public MainPageViewModel()
        {
            _items.CollectionChanged += OnItemsChanged;
        }

        /// <summary>
        /// Gets the scanned items. Bulk changes replace the whole collection so the list isn't updated item by item.
        /// </summary>
        public ObservableCollection<RenameItem> Items
        {
            get => _items;
            private set
            {
                if (ReferenceEquals(_items, value)) return;

                _items.CollectionChanged -= OnItemsChanged;
                foreach (var item in _items)
                    item.PropertyChanged -= OnItemPropertyChanged;

                _items = value;
                foreach (var item in _items)
                    item.PropertyChanged += OnItemPropertyChanged;
                _items.CollectionChanged += OnItemsChanged;

                SelectedCount = 0;
                OnPropertyChanged();
                RaiseItemStateChanged();
            }
        }


        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OptionsSummary))]
        public partial bool Recursive { get; set; } = true;

        /// <summary>Gets or sets the selected <see cref="ScanTarget"/> as a ComboBox index.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OptionsSummary))]
        public partial int TargetIndex { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OptionsSummary))]
        public partial bool IncludeHidden { get; set; }

        /// <summary>Gets or sets the selected <see cref="ConflictPolicy"/> as a ComboBox index.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OptionsSummary))]
        public partial int ConflictIndex { get; set; }

        partial void OnRecursiveChanged(bool value) => RequestRescan();
        partial void OnTargetIndexChanged(int value) => RequestRescan();
        partial void OnIncludeHiddenChanged(bool value) => RequestRescan();
        partial void OnConflictIndexChanged(int value) => RequestRescan();

        public string OptionsSummary
        {
            get
            {
                var parts = new List<string>(4)
                {
                    Localizer.GetString(Recursive ? "SummaryRecursive" : "SummaryNotRecursive"),
                    Localizer.GetString(TargetIndex switch { 1 => "SummaryFilesOnly", 2 => "SummaryFoldersOnly", _ => "SummaryFilesAndFolders" }),
                };
                if (IncludeHidden) parts.Add(Localizer.GetString("SummaryIncludeHidden"));
                parts.Add(Localizer.GetString(ConflictIndex == 1 ? "SummaryConflictAppendNumber" : "SummaryConflictSkip"));
                return string.Join(" · ", parts);
            }
        }


        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy), nameof(StatusText))]
        [NotifyCanExecuteChangedFor(nameof(ConvertCommand), nameof(RescanCommand))]
        public partial bool IsScanning { get; set; }

        /// <summary>Gets or sets whether files are being renamed. Options, adding and removing are locked meanwhile.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsBusy), nameof(StatusText), nameof(CanEditOptions))]
        [NotifyCanExecuteChangedFor(nameof(ConvertCommand), nameof(RescanCommand), nameof(RemoveSelectedCommand), nameof(ClearAllCommand), nameof(AddFilesCommand), nameof(AddFoldersCommand))]
        public partial bool IsConverting { get; set; }

        public bool IsBusy => IsScanning || IsConverting;

        public bool CanEditOptions => !IsConverting;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RemoveSelectedCommand))]
        public partial int SelectedCount { get; set; }

        /// <summary>Gets or sets the summary of the last conversion, cleared on the next scan.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        public partial string? LastResult { get; set; }

        public bool HasRoots => _roots.Count > 0;
        public bool HasItems => Items.Count > 0;
        public bool IsEmpty => Items.Count == 0;

        public int ConvertibleCount => _checkedPendingCount;

        public string ConvertButtonText => ConvertibleCount > 0
            ? Localizer.Format("ConvertButtonWithCount", ConvertibleCount)
            : Localizer.GetString("ConvertButton");

        /// <summary>
        /// Gets or sets whether all pending items are checked; <see langword="null"/> when only some are.
        /// </summary>
        public bool? AllChecked
        {
            get
            {
                if (_pendingCount == 0) return false;
                return _checkedPendingCount == 0 ? false : _checkedPendingCount == _pendingCount ? true : null;
            }
            set
            {
                // A two-state CheckBox clicked while showing "some" sends false; select all instead,
                // as header check boxes usually do.
                bool target = value ?? true;
                if (target == false && AllChecked is null)
                    target = true;
                using (SuppressItemNotifications())
                {
                    foreach (var item in Items)
                        if (item.Status == ItemStatus.Pending)
                            item.IsChecked = target;
                }
                RaiseItemStateChanged();
            }
        }

        public string EmptyTitle => Localizer.GetString(HasRoots ? "EmptyTitleNothingToConvert" : "EmptyTitleNoItems");

        public string EmptyDescription => Localizer.GetString(HasRoots ? "EmptyDescriptionNothingToConvert" : "EmptyDescriptionNoItems");

        public string StatusText
        {
            get
            {
                if (IsBusy) return Localizer.GetString("StatusBusy");
                if (!HasRoots) return Localizer.GetString("StatusNoItems");

                var parts = new List<string> { Localizer.Format("StatusItems", Items.Count) };
                if (_pendingCount > 0) parts.Add(Localizer.Format("StatusPending", _pendingCount));
                if (_conflictCount > 0) parts.Add(Localizer.Format("StatusConflicts", _conflictCount));
                if (_foldersScanned > 0) parts.Add(Localizer.Format("StatusFoldersScanned", _foldersScanned));
                if (LastResult is not null) parts.Add(LastResult);
                return string.Join("  ·  ", parts);
            }
        }


        [RelayCommand(CanExecute = nameof(CanEditOptions))]
        private async Task AddFilesAsync()
        {
            var picker = new FileOpenPicker(App.Window.AppWindow.Id);
            picker.FileTypeFilter.Add("*");
            var results = await picker.PickMultipleFilesAsync();
            if (results is { Count: > 0 })
                await AddPathsAsync(results.Select(r => r.Path));
        }

        [RelayCommand(CanExecute = nameof(CanEditOptions))]
        private async Task AddFoldersAsync()
        {
            var picker = new FolderPicker(App.Window.AppWindow.Id);
            var results = await picker.PickMultipleFoldersAsync();
            if (results is { Count: > 0 })
                await AddPathsAsync(results.Select(r => r.Path));
        }

        [RelayCommand(CanExecute = nameof(CanRemove))]
        private void RemoveSelected(IList<object>? selected)
        {
            if (selected is null || IsConverting) return;
            var removed = selected.OfType<RenameItem>().ToHashSet();
            if (removed.Count == 0) return;

            foreach (var item in removed)
                _excluded.Add(item.FullPath);

            if (removed.Count > BulkRemoveThreshold)
            {
                Items = new ObservableCollection<RenameItem>(Items.Where(i => !removed.Contains(i)));
                return;
            }

            using (SuppressItemNotifications())
            {
                foreach (var item in removed)
                    Items.Remove(item);
            }
            RaiseItemStateChanged();
        }

        private bool CanRemove(IList<object>? _) => SelectedCount > 0 && !IsConverting;

        [RelayCommand(CanExecute = nameof(CanClearAll))]
        private void ClearAll()
        {
            CancelScan();
            _rescanPending = false;
            _roots.Clear();
            _excluded.Clear();
            _foldersScanned = 0;
            LastResult = null;
            Items = [];
            RaiseRootsChanged();
        }

        private bool CanClearAll() => HasRoots && !IsConverting;

        [RelayCommand(CanExecute = nameof(CanRescan))]
        private Task RescanAsync() => ScanAsync(keepResult: false);

        private bool CanRescan() => HasRoots && !IsBusy;

        [RelayCommand(CanExecute = nameof(CanConvert))]
        private async Task ConvertAsync()
        {
            var targets = Items.Where(i => i.IsChecked && i.Status == ItemStatus.Pending).ToList();
            if (targets.Count == 0) return;

            IsConverting = true;
            try
            {
                var lookup = new Dictionary<RenameRequest, RenameItem>();
                foreach (var item in targets)
                    lookup[new RenameRequest(item.Directory, item.OriginalName, item.ConvertedName, item.IsFolder, item.Depth)] = item;

                // No early return here: a rescan requested during the conversion must still run below.
                List<RenameOutcome>? outcomes = null;
                try
                {
                    outcomes = await Task.Run(() => NfcScanner.Rename(lookup.Keys));
                }
                catch (Exception ex)
                {
                    LastResult = Localizer.Format("ResultConvertFailed", ex.Message);
                }

                if (outcomes is not null)
                    ApplyRenameOutcomes(lookup, outcomes);
            }
            finally
            {
                IsConverting = false;
                RaiseItemStateChanged();
            }

            if (_rescanPending)
            {
                _rescanPending = false;
                await ScanAsync(keepResult: true);
            }
        }

        private void ApplyRenameOutcomes(Dictionary<RenameRequest, RenameItem> lookup, List<RenameOutcome> outcomes)
        {
            int ok = 0, failed = 0;
            using (SuppressItemNotifications())
            {
                foreach (var outcome in outcomes)
                {
                    var item = lookup[outcome.Request];
                    item.Status = outcome.Success ? ItemStatus.Done : ItemStatus.Failed;
                    item.Message = outcome.Error;
                    item.IsChecked = false;
                    if (outcome.Success) ok++; else failed++;
                }
            }

            // Keep roots and exclusions valid after their own names (or a parent's) changed.
            foreach (var outcome in outcomes.Where(o => o.Success))
            {
                var from = Path.Combine(outcome.Request.Directory, outcome.Request.OriginalName);
                var to = Path.Combine(outcome.Request.Directory, outcome.Request.ConvertedName);

                for (int i = 0; i < _roots.Count; i++)
                    _roots[i] = RemapPath(_roots[i], from, to);

                if (outcome.Request.IsFolder && _excluded.Count > 0)
                {
                    var remapped = _excluded.Select(p => RemapPath(p, from, to)).ToList();
                    _excluded.Clear();
                    _excluded.UnionWith(remapped);
                }
            }

            LastResult = failed > 0 ? Localizer.Format("ResultConvertedWithFailures", ok, failed) : Localizer.Format("ResultConverted", ok);
        }

        private bool CanConvert() => !IsBusy && ConvertibleCount > 0;


        public async Task AddPathsAsync(IEnumerable<string> paths)
        {
            bool added = false;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                string full;
                try
                {
                    full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
                {
                    continue;
                }

                _excluded.Remove(full);
                if (!_roots.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    _roots.Add(full);
                    added = true;
                }
            }

            if (added)
            {
                RaiseRootsChanged();
                if (IsConverting)
                    _rescanPending = true;
                else
                    await ScanAsync(keepResult: false);
            }
        }

        /// <summary>Rescans after an option change, or once the running conversion has finished.</summary>
        private void RequestRescan()
        {
            if (!HasRoots) return;
            if (IsConverting)
                _rescanPending = true;
            else
                _ = ScanAsync(keepResult: false);
        }

        private void CancelScan()
        {
            if (_scanCts is null) return;
            _scanCts.Cancel();
            _scanCts = null;
            IsScanning = false;
        }

        /// <remarks>Never throws: failures are shown in the status bar.</remarks>
        private async Task ScanAsync(bool keepResult)
        {
            if (!HasRoots || IsConverting) return;

            _scanCts?.Cancel();
            using var cts = new CancellationTokenSource();
            _scanCts = cts;
            var roots = _roots.ToArray();
            var options = new ScanOptions(Recursive, (ScanTarget)TargetIndex, IncludeHidden, (ConflictPolicy)ConflictIndex);

            IsScanning = true;
            try
            {
                var result = await Task.Run(() => NfcScanner.Scan(roots, options, cts.Token), cts.Token);
                if (cts.IsCancellationRequested) return;

                _foldersScanned = result.FoldersScanned;
                if (!keepResult) LastResult = null;

                var items = new List<RenameItem>(result.Entries.Count);
                foreach (var entry in result.Entries)
                {
                    var item = new RenameItem(entry.Directory, entry.OriginalName, entry.ConvertedName, entry.IsFolder, entry.Depth)
                    {
                        Status = entry.IsConflict ? ItemStatus.Conflict : ItemStatus.Pending,
                        IsChecked = !entry.IsConflict,
                    };
                    if (!_excluded.Contains(item.FullPath))
                        items.Add(item);
                }
                Items = new ObservableCollection<RenameItem>(items);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(_scanCts, cts))
                    LastResult = Localizer.Format("ResultScanFailed", ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_scanCts, cts))
                {
                    _scanCts = null;
                    IsScanning = false;
                    RaiseItemStateChanged();
                }
            }
        }

        /// <summary>Rewrites <paramref name="path"/> if it is <paramref name="from"/> or lies under it.</summary>
        private static string RemapPath(string path, string from, string to)
        {
            if (string.Equals(path, from, StringComparison.OrdinalIgnoreCase))
                return to;
            if (path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return to + path[from.Length..];
            return path;
        }


        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
                foreach (RenameItem item in e.OldItems)
                    item.PropertyChanged -= OnItemPropertyChanged;
            if (e.NewItems is not null)
                foreach (RenameItem item in e.NewItems)
                    item.PropertyChanged += OnItemPropertyChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                SelectedCount = 0;

            RaiseItemStateChanged();
        }

        private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(RenameItem.IsChecked) or nameof(RenameItem.Status))
                RaiseItemStateChanged();
        }

        private NotificationScope SuppressItemNotifications()
        {
            _suppressItemNotifications++;
            return new NotificationScope(this);
        }

        private readonly struct NotificationScope(MainPageViewModel owner) : IDisposable
        {
            public void Dispose() => owner._suppressItemNotifications--;
        }

        private void RaiseRootsChanged()
        {
            OnPropertyChanged(nameof(HasRoots));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptyDescription));
            ClearAllCommand.NotifyCanExecuteChanged();
            RescanCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(StatusText));
        }

        /// <summary>Recounts the items in a single pass and notifies every dependent property.</summary>
        private void RaiseItemStateChanged()
        {
            if (_suppressItemNotifications > 0) return;

            int pending = 0, conflict = 0, checkedPending = 0;
            foreach (var item in _items)
            {
                switch (item.Status)
                {
                    case ItemStatus.Pending:
                        pending++;
                        if (item.IsChecked) checkedPending++;
                        break;
                    case ItemStatus.Conflict:
                        conflict++;
                        break;
                }
            }
            _pendingCount = pending;
            _conflictCount = conflict;
            _checkedPendingCount = checkedPending;

            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ConvertibleCount));
            OnPropertyChanged(nameof(ConvertButtonText));
            OnPropertyChanged(nameof(AllChecked));
            OnPropertyChanged(nameof(StatusText));
            ConvertCommand.NotifyCanExecuteChanged();
        }
    }
}
