using System.Diagnostics;
using System.Text;
using NFDFix.Services;

namespace NFDFix.Tests;

/// <summary>
/// Tests <see cref="NfcScanner"/> against real files in a temporary folder.
/// </summary>
/// <remarks>
/// Each test gets its own folder, which is deleted afterwards.
/// </remarks>
public sealed class NfcScannerTests : IDisposable
{
    private static readonly string Nfc = "한글";
    private static readonly string Nfd = Nfc.Normalize(NormalizationForm.FormD);

    private static readonly ScanOptions Recursive = new(Recursive: true, ScanTarget.FilesAndFolders, IncludeHidden: false, ConflictPolicy.Skip);

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "NFDFix.Tests", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>Deletes the test folder. A recursive delete removes junctions without following them.</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static ScanResult Scan(string root, ScanOptions? options = null) =>
        NfcScanner.Scan([root], options ?? Recursive, CancellationToken.None);

    private static List<RenameOutcome> RenameAll(ScanResult result) =>
        NfcScanner.Rename(result.Entries.Select(e => new RenameRequest(e.Directory, e.OriginalName, e.ConvertedName, e.IsFolder, e.Depth)));

    [Fact]
    public void Detects_and_converts_nfd_names()
    {
        Assert.True(NfcScanner.NeedsConversion(Nfd));
        Assert.False(NfcScanner.NeedsConversion(Nfc));
        Assert.Equal(Nfc, NfcScanner.ToNfc(Nfd));
    }

    /// <summary>
    /// Renaming the folder first would break the path to the file inside it, so children must go first.
    /// </summary>
    [Fact]
    public void Renames_nested_nfd_folder_and_file_children_first()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, Nfd));
        File.WriteAllText(Path.Combine(folder.FullName, Nfd + ".txt"), "data");

        var outcomes = RenameAll(Scan(_root));

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
        Assert.Equal("data", File.ReadAllText(Path.Combine(_root, Nfc, Nfc + ".txt")));
    }

    /// <summary>
    /// A rename must fail rather than replace a file that already has the target name.
    /// </summary>
    [Fact]
    public void Rename_never_overwrites_an_existing_file()
    {
        // NTFS keeps NFD and NFC names apart, so both can exist side by side.
        File.WriteAllText(Path.Combine(_root, Nfd), "nfd");
        File.WriteAllText(Path.Combine(_root, Nfc), "nfc");

        var outcome = Assert.Single(NfcScanner.Rename([new RenameRequest(_root, Nfd, Nfc, IsFolder: false, Depth: 1)]));

        Assert.False(outcome.Success);
        Assert.Equal("nfd", File.ReadAllText(Path.Combine(_root, Nfd)));
        Assert.Equal("nfc", File.ReadAllText(Path.Combine(_root, Nfc)));
    }

    /// <summary>
    /// A junction must not lead the scan outside the dropped folder or into an endless loop.
    /// </summary>
    [Fact]
    public void Scan_does_not_follow_junctions_out_of_the_tree_or_into_cycles()
    {
        var scanned = Directory.CreateDirectory(Path.Combine(_root, "scanned")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, Nfd), "");
        CreateJunction(Path.Combine(scanned, "escape"), outside);
        CreateJunction(Path.Combine(scanned, "loop"), scanned);

        var result = Scan(scanned);

        Assert.Empty(result.Entries);
    }

    /// <summary>
    /// Conversion must not turn a name into a path that points elsewhere.
    /// </summary>
    /// <remarks>
    /// NFKC would turn the fullwidth solidus into a slash and the two dot leader into two dots. NFC keeps both.
    /// </remarks>
    [Theory]
    [InlineData("a／b")]
    [InlineData("‥")]
    public void Conversion_never_creates_path_separators_or_dot_segments(string name)
    {
        var converted = NfcScanner.ToNfc(name);

        Assert.Equal(name, converted);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, converted);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, converted);
        Assert.NotEqual("..", converted);
    }

    /// <summary>
    /// Scanning and renaming 2,000 entries should take well under a second.
    /// </summary>
    /// <remarks>
    /// The time limit is generous so it catches work that grows with the square of the entry count,
    /// such as listing a folder for every entry, and not timing noise.
    /// </remarks>
    [Fact]
    public void Scans_and_renames_thousands_of_entries_quickly()
    {
        // 20 folders of 100 files each, with every fourth name in NFD.
        for (int d = 0; d < 20; d++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, $"d{d}")).FullName;
            for (int f = 0; f < 100; f++)
                File.Create(Path.Combine(dir, f % 4 == 0 ? $"{Nfd}{f}" : $"n{f}")).Dispose();
        }

        var sw = Stopwatch.StartNew();
        var result = Scan(_root);
        var outcomes = RenameAll(result);
        sw.Stop();

        Assert.Equal(500, result.Entries.Count);
        Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Took {sw.Elapsed}");
    }

    /// <summary>
    /// Cancelling must stop the scan so the user is never stuck waiting on a large folder.
    /// </summary>
    [Fact]
    public void Scan_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => NfcScanner.Scan([_root], Recursive, cts.Token));
    }

    /// <summary>
    /// A volume root ends with a separator, which once gave its folders the same depth as their children,
    /// so a folder could be renamed before the file inside it.
    /// </summary>
    /// <remarks>
    /// Maps the test folder to a free drive letter with <c>subst</c> to scan it as "X:\".
    /// </remarks>
    [Fact]
    public void Renames_nested_entries_when_scanning_a_drive_root()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, Nfd));
        File.WriteAllText(Path.Combine(folder.FullName, Nfd + ".txt"), "data");

        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char letter = "ZYXWVUTSRQPONMLKJIHG".FirstOrDefault(c => !used.Contains(c));
        Assert.True(letter != default, "No free drive letter.");
        var drive = $"{letter}:";

        RunHidden("subst.exe", $"{drive} \"{_root}\"");
        try
        {
            Assert.True(Directory.Exists(drive + "\\"), "Could not map a drive letter.");

            var result = Scan(drive + "\\");
            var outcomes = RenameAll(result);

            Assert.Equal(2, outcomes.Count);
            Assert.All(outcomes, o => Assert.True(o.Success, o.Error));
            Assert.Equal("data", File.ReadAllText(Path.Combine(_root, Nfc, Nfc + ".txt")));
        }
        finally
        {
            RunHidden("subst.exe", $"{drive} /D");
        }
    }

    /// <summary>
    /// Dropping a folder inside an OS-managed folder must not scan or rename anything there.
    /// </summary>
    [Fact]
    public void Scan_skips_folders_inside_protected_paths()
    {
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

        var result = Scan(system32);

        Assert.Empty(result.Entries);
        Assert.Equal(0, result.FoldersScanned);
    }

    /// <summary>
    /// Numbering a dot file must keep its name readable, ".한글 (2)" rather than " (2).한글".
    /// </summary>
    [Fact]
    public void Numbers_dot_files_after_the_whole_name()
    {
        File.WriteAllText(Path.Combine(_root, "." + Nfd), "nfd");
        File.WriteAllText(Path.Combine(_root, "." + Nfc), "nfc");

        var result = Scan(_root, Recursive with { Conflict = ConflictPolicy.AppendNumber });

        var entry = Assert.Single(result.Entries);
        Assert.Equal($".{Nfc} (2)", entry.ConvertedName);
    }

    private static void RunHidden(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
    }

    /// <summary>
    /// Creates a junction, which unlike a symbolic link needs no admin rights or developer mode.
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "Could not create a junction.");
    }
}
