using CommunityToolkit.Mvvm.ComponentModel;

namespace NFDFix.Models
{
    public enum ItemStatus
    {
        Pending,
        Conflict,
        Done,
        Failed,
    }

    /// <summary>
    /// A file or folder whose name is not in NFC form.
    /// </summary>
    public sealed partial class RenameItem : ObservableObject
    {
        public RenameItem(string directory, string originalName, string convertedName, bool isFolder, int depth)
        {
            Directory = directory;
            OriginalName = originalName;
            ConvertedName = convertedName;
            IsFolder = isFolder;
            Depth = depth;
        }

        /// <summary>Gets the parent directory at scan time.</summary>
        public string Directory { get; }

        public string OriginalName { get; }

        public bool IsFolder { get; }

        /// <summary>Gets the path depth, used to rename children before their parents.</summary>
        public int Depth { get; }

        public string FullPath => System.IO.Path.Combine(Directory, OriginalName);

        public string TypeGlyph => IsFolder ? "\uE8B7" : "\uE8A5";

        [ObservableProperty]
        public partial string ConvertedName { get; set; }

        [ObservableProperty]
        public partial bool IsChecked { get; set; } = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsConflict), nameof(IsDone), nameof(IsFailed), nameof(ConvertedOpacity))]
        public partial ItemStatus Status { get; set; }

        /// <summary>Gets or sets the error message when the rename failed.</summary>
        [ObservableProperty]
        public partial string? Message { get; set; }

        public bool IsPending => Status == ItemStatus.Pending;
        public bool IsConflict => Status == ItemStatus.Conflict;
        public bool IsDone => Status == ItemStatus.Done;
        public bool IsFailed => Status == ItemStatus.Failed;

        public double ConvertedOpacity => Status is ItemStatus.Conflict or ItemStatus.Failed ? 0.55 : 1.0;
    }
}
