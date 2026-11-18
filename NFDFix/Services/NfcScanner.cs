using System.Text;

namespace NFDFix.Services
{
    public enum ScanTarget
    {
        FilesAndFolders = 0,
        FilesOnly = 1,
        FoldersOnly = 2,
    }

    public enum ConflictPolicy
    {
        Skip = 0,
        AppendNumber = 1,
    }

    public sealed record ScanOptions(bool Recursive, ScanTarget Target, bool IncludeHidden, ConflictPolicy Conflict);

    public sealed record ScanEntry(string Directory, string OriginalName, string ConvertedName, bool IsFolder, int Depth, bool IsConflict);

    public sealed record ScanResult(IReadOnlyList<ScanEntry> Entries, int FoldersScanned);

    public sealed record RenameRequest(string Directory, string OriginalName, string ConvertedName, bool IsFolder, int Depth);

    public sealed record RenameOutcome(RenameRequest Request, bool Success, string? Error);

    /// <summary>
    /// Finds file and folder names that are not NFC-normalized and renames them.
    /// </summary>
    public static class NfcScanner
    {
        public static bool NeedsConversion(string name) => !name.IsNormalized(NormalizationForm.FormC);

        public static string ToNfc(string name) => name.Normalize(NormalizationForm.FormC);

        public static ScanResult Scan(IEnumerable<string> roots, ScanOptions options, CancellationToken token)
        {
            var entries = new List<ScanEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Targets already claimed by another entry in this scan, so two NFD names
            // that normalize to the same NFC name don't both get it.
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int foldersScanned = 0;

            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = options.IncludeHidden ? 0 : FileAttributes.Hidden | FileAttributes.System,
            };

            var names = new DirectoryNames();

            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                var full = Path.TrimEndingDirectorySeparator(root);

                // Anything inside an OS-managed folder is left alone, even when dropped directly.
                if (IsProtected(full))
                    continue;

                if (Directory.Exists(full))
                {
                    var info = new DirectoryInfo(full);
                    if (info.Parent is not null)
                        Consider(info.Parent.FullName, info.Name, isFolder: true, Depth(full));

                    // A dropped junction or symbolic link is renamed itself but not followed,
                    // the same as links found while walking.
                    if (CanDescend(info))
                        Walk(full, Depth(full));
                }
                else if (File.Exists(full))
                {
                    var dir = Path.GetDirectoryName(full);
                    if (dir is not null)
                        Consider(dir, Path.GetFileName(full), isFolder: false, Depth(full));
                }
            }

            return new ScanResult(entries, foldersScanned);

            void Consider(string dir, string name, bool isFolder, int depth)
            {
                if (isFolder ? options.Target == ScanTarget.FilesOnly : options.Target == ScanTarget.FoldersOnly)
                    return;
                if (!NeedsConversion(name))
                    return;
                if (!seen.Add(Path.Combine(dir, name)))
                    return;

                var nfc = ToNfc(name);
                bool conflict = names.ExistsOther(dir, nfc) || claimed.Contains(Path.Combine(dir, nfc));
                if (conflict && options.Conflict == ConflictPolicy.AppendNumber)
                {
                    nfc = FindFreeName(dir, nfc, isFolder, claimed, names);
                    conflict = false;
                }

                claimed.Add(Path.Combine(dir, nfc));
                entries.Add(new ScanEntry(dir, name, nfc, isFolder, depth, conflict));
            }

            // Depth is carried down from the root (one level per folder), matching Depth() for dropped items.
            void Walk(string dir, int rootDepth)
            {
                var stack = new Stack<(string Path, int Depth, bool IsRoot)>();
                stack.Push((dir, rootDepth, true));

                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var (current, currentDepth, isRoot) = stack.Pop();
                    if (!isRoot)
                        foldersScanned++;

                    List<FileSystemInfo> children;
                    try
                    {
                        children = new DirectoryInfo(current).EnumerateFileSystemInfos("*", enumeration).ToList();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    int depth = currentDepth + 1;
                    foreach (var child in children)
                    {
                        bool isFolder = child is DirectoryInfo;
                        Consider(current, child.Name, isFolder, depth);

                        if (isFolder && options.Recursive && CanDescend(child))
                            stack.Push((child.FullName, depth, false));
                    }
                }
            }
        }

        /// <summary>
        /// Renames the requested entries, deepest first so that renaming a parent never invalidates a child path.
        /// </summary>
        /// <remarks>
        /// Never overwrites: the move itself fails if the target exists, so a file created after the scan is safe.
        /// Each failure is recorded per item and the batch continues.
        /// </remarks>
        public static List<RenameOutcome> Rename(IEnumerable<RenameRequest> requests)
        {
            var outcomes = new List<RenameOutcome>();
            var names = new DirectoryNames();
            foreach (var request in requests.OrderByDescending(r => r.Depth))
            {
                var source = Path.Combine(request.Directory, request.OriginalName);
                var target = Path.Combine(request.Directory, request.ConvertedName);
                try
                {
                    if (names.ExistsOther(request.Directory, request.ConvertedName))
                        throw new IOException(AlreadyExistsMessage);

                    if (Exists(target))
                    {
                        // The volume treats both forms as one name (for example an SMB share backed by APFS),
                        // so the target "exists" only as the source itself. Move through a temporary name.
                        var temp = Path.Combine(request.Directory, $".nfdfix-{Guid.NewGuid():N}");
                        Move(source, temp, request.IsFolder);
                        try
                        {
                            Move(temp, target, request.IsFolder);
                        }
                        catch (Exception moveError)
                        {
                            try
                            {
                                Move(temp, source, request.IsFolder);
                            }
                            catch
                            {
                                // The item is stranded under the temporary name. Say where, or the user can't find it.
                                throw new IOException(Localizer.Format("ErrorLeftAtTemporaryName", temp, moveError.Message), moveError);
                            }
                            throw;
                        }
                    }
                    else
                    {
                        Move(source, target, request.IsFolder);
                    }

                    names.Renamed(request.Directory, request.OriginalName, request.ConvertedName);
                    outcomes.Add(new RenameOutcome(request, true, null));
                }
                catch (IOException ex) when (ex.HResult is HResultAlreadyExists or HResultFileExists)
                {
                    outcomes.Add(new RenameOutcome(request, false, AlreadyExistsMessage));
                }
                catch (Exception ex)
                {
                    outcomes.Add(new RenameOutcome(request, false, ex.Message));
                }
            }
            return outcomes;
        }

        private static string AlreadyExistsMessage => Localizer.GetString("ErrorAlreadyExists");
        private const int HResultFileExists = unchecked((int)0x80070050);
        private const int HResultAlreadyExists = unchecked((int)0x800700B7);

        private static void Move(string source, string target, bool isFolder)
        {
            if (isFolder)
                Directory.Move(source, target);
            else
                File.Move(source, target, overwrite: false);
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private static string FindFreeName(string dir, string nfc, bool isFolder, HashSet<string> claimed, DirectoryNames names)
        {
            var stem = isFolder ? nfc : Path.GetFileNameWithoutExtension(nfc);
            var ext = isFolder ? string.Empty : Path.GetExtension(nfc);
            if (stem.Length == 0)
            {
                // A dot file such as ".한글" has no stem; number the whole name instead of producing " (2).한글".
                stem = nfc;
                ext = string.Empty;
            }
            for (int i = 2; ; i++)
            {
                var candidate = $"{stem} ({i}){ext}";
                if (!names.ExistsOther(dir, candidate) && !claimed.Contains(Path.Combine(dir, candidate)))
                    return candidate;
            }
        }

        /// <summary>
        /// Whether to walk into <paramref name="dir"/>. Junctions and symbolic links are not followed, to avoid
        /// cycles and leaving the dropped tree, and OS-managed folders are never touched.
        /// </summary>
        /// <remarks>
        /// Other reparse points are followed: OneDrive and Dropbox mark every synced folder as a cloud placeholder,
        /// and those are exactly where names from a Mac turn up.
        /// </remarks>
        private static bool CanDescend(FileSystemInfo dir)
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) && IsLink(dir))
                return false;
            return !IsProtected(dir.FullName);
        }

        private static bool IsLink(FileSystemInfo dir)
        {
            try
            {
                return dir.LinkTarget is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true; // Can't tell what it points to; don't follow it.
            }
        }

        private static readonly string[] ProtectedFolderNames = ["System Volume Information", "$Recycle.Bin", "$WinREAgent"];

        private static readonly string[] ProtectedPaths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            // A 32-bit process on 64-bit Windows gets "Program Files (x86)" for ProgramFiles above,
            // so the native "Program Files" comes only from this variable.
            Environment.GetEnvironmentVariable("ProgramW6432"),
        }.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.TrimEndingDirectorySeparator(p!)).ToArray();

        /// <summary>Whether <paramref name="fullPath"/> is an OS-managed folder or anything inside one.</summary>
        private static bool IsProtected(string fullPath)
        {
            var path = Path.TrimEndingDirectorySeparator(fullPath);
            foreach (var p in ProtectedPaths)
                if (IsSameOrInside(path, p))
                    return true;

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root) && path.Length > root.Length)
            {
                // The first folder below the volume root, e.g. "System Volume Information" in "E:\System Volume Information\x".
                var rest = path[root.Length..];
                int separator = rest.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                var top = separator < 0 ? rest : rest[..separator];
                foreach (var n in ProtectedFolderNames)
                    if (string.Equals(top, n, StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            return false;
        }

        private static bool IsSameOrInside(string path, string folder) =>
            path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
            && (path.Length == folder.Length || path[folder.Length] is '\\' or '/');

        /// <summary>
        /// Answers "does another entry already have this name?" without being fooled by volumes that treat
        /// NFD and NFC as the same name. Directory listings are read only when the cheap check hits, then cached.
        /// </summary>
        private sealed class DirectoryNames
        {
            private static readonly EnumerationOptions AllEntries = new()
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0,
            };

            private readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Whether an entry is listed under exactly <paramref name="name"/> (ignoring case). The entry being
            /// renamed is listed under its NFD name, so it never counts even when the volume aliases the two forms.
            /// </summary>
            public bool ExistsOther(string dir, string name)
            {
                if (!Exists(Path.Combine(dir, name)))
                    return false;

                var set = Get(dir);
                if (set is null)
                    return true; // Can't list the folder; assume the worst.
                return set.Contains(name);
            }

            public void Renamed(string dir, string from, string to)
            {
                if (_cache.TryGetValue(dir, out var set))
                {
                    set.Remove(from);
                    set.Add(to);
                }
            }

            private HashSet<string>? Get(string dir)
            {
                if (_cache.TryGetValue(dir, out var set))
                    return set;
                try
                {
                    set = new HashSet<string>(Directory.EnumerateFileSystemEntries(dir, "*", AllEntries).Select(p => Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
                _cache[dir] = set;
                return set;
            }
        }

        /// <summary>
        /// Separators in <paramref name="path"/>, not counting a trailing one, so a volume root ("E:\")
        /// sits one level above its folders ("E:\a") just as "E:\a" sits above "E:\a\b".
        /// </summary>
        private static int Depth(string path)
        {
            int depth = 0;
            for (int i = 0; i < path.Length - 1; i++)
                if (path[i] == Path.DirectorySeparatorChar || path[i] == Path.AltDirectorySeparatorChar)
                    depth++;
            return depth;
        }
    }
}
