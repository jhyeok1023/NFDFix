using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NFDFix.Services
{
    /// <summary>A newer release found on GitHub.</summary>
    /// <param name="Version">The release version, from its tag (<c>v1.2.0</c>).</param>
    /// <param name="ReleasePage">The release's web page, for release notes.</param>
    /// <param name="Installer">The installer for this PC's architecture, or <see langword="null"/> if the release has none.</param>
    public sealed record UpdateInfo(Version Version, Uri ReleasePage, UpdateAsset? Installer);

    /// <param name="Sha256">Lowercase hex SHA-256 that GitHub reports for the file, if any.</param>
    public sealed record UpdateAsset(string Name, Uri DownloadUrl, long Size, string? Sha256);

    /// <summary>Why an update check or download failed; picks the message shown to the user.</summary>
    public enum UpdateError
    {
        Network,
        Corrupt,
        Launch,
    }

    public sealed class UpdateException(UpdateError error, string message, Exception? inner = null) : Exception(message, inner)
    {
        public UpdateError Error { get; } = error;
    }

    /// <summary>
    /// Checks GitHub Releases for a newer version and downloads its installer.
    /// </summary>
    /// <remarks>
    /// Releases are tagged <c>v&lt;version&gt;</c> and carry one Inno Setup installer per architecture,
    /// named <c>NFDFix-&lt;version&gt;-win-&lt;arch&gt;-setup.exe</c> (see <see cref="InstallerSuffix"/>).
    /// <c>installer\NFDFix.iss</c> builds them and <c>.github\workflows\release.yml</c> publishes them.
    /// </remarks>
    public static class UpdateService
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/jhyeok1023/NFDFix/releases/latest";

        private static readonly string LastCheckPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NFDFix", "last-update-check.txt");

        /// <summary>Gets the running version as major.minor.build.</summary>
        public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

        /// <summary>Gets the end of the installer asset name for this PC, such as <c>-win-x64-setup.exe</c>.</summary>
        public static string InstallerSuffix { get; } =
            $"-win-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}-setup.exe";

        /// <summary>Gets when the last check succeeded, in local time, or <see langword="null"/> if never.</summary>
        public static DateTime? LastChecked { get; private set; } = ReadLastCheck();

        // Declared after CurrentVersion: static initializers run in source order and the User-Agent needs it.
        private static readonly HttpClient Http = CreateClient();

        /// <summary>Returns the newer release, or <see langword="null"/> if this is the latest version.</summary>
        /// <exception cref="UpdateException">The release could not be read.</exception>
        public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
        {
            string json;
            try
            {
                using var response = await Http.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false);

                // No published release yet.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    SaveLastCheck();
                    return null;
                }

                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new UpdateException(UpdateError.Network, "Failed to read the latest release.", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new UpdateException(UpdateError.Network, "Timed out reading the latest release.", ex);
            }

            UpdateInfo? update;
            try
            {
                update = ParseRelease(json, CurrentVersion, InstallerSuffix);
            }
            catch (JsonException ex)
            {
                throw new UpdateException(UpdateError.Network, "The release response could not be read.", ex);
            }

            SaveLastCheck();
            return update;
        }

        /// <summary>
        /// Picks the installer for <paramref name="installerSuffix"/> from a GitHub "latest release" response.
        /// Returns <see langword="null"/> when the release is not newer than <paramref name="current"/>.
        /// </summary>
        internal static UpdateInfo? ParseRelease(string json, Version current, string installerSuffix)
        {
            var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
            if (release is null || release.Draft || release.Prerelease)
                return null;
            if (!TryParseTag(release.TagName, out var version) || version <= Normalize(current))
                return null;
            if (!Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page))
                return null;

            UpdateAsset? installer = null;
            foreach (var asset in release.Assets ?? [])
            {
                if (asset.Name is null || !asset.Name.EndsWith(installerSuffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    continue;

                const string Prefix = "sha256:";
                var sha256 = asset.Digest is { } d && d.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    ? d[Prefix.Length..].ToLowerInvariant()
                    : null;
                installer = new UpdateAsset(asset.Name, url, asset.Size, sha256);
                break;
            }

            return new UpdateInfo(version, page, installer);
        }

        /// <summary>
        /// Downloads the installer to the temp folder and returns its path.
        /// Reports the number of bytes received so far.
        /// </summary>
        /// <exception cref="UpdateException">The download failed or the file does not match the release.</exception>
        public static async Task<string> DownloadAsync(UpdateAsset installer, IProgress<long>? progress, CancellationToken cancellationToken)
        {
            var folder = DownloadFolder;
            // The asset name comes from the server; keep only the file name part.
            var path = Path.Combine(folder, Path.GetFileName(installer.Name));
            var partial = path + ".partial";

            try
            {
                Directory.CreateDirectory(folder);

                using var response = await Http.GetAsync(installer.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long received = 0;
                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        received += read;
                        progress?.Report(received);
                    }
                }

                if (installer.Size > 0 && received != installer.Size)
                    throw new UpdateException(UpdateError.Corrupt, $"Expected {installer.Size} bytes but received {received}.");
                if (installer.Sha256 is { } expected && Convert.ToHexStringLower(hash.GetHashAndReset()) != expected)
                    throw new UpdateException(UpdateError.Corrupt, "The installer does not match the release checksum.");

                File.Move(partial, path, overwrite: true);
                return path;
            }
            catch (Exception ex) when (ex is not UpdateException && ex is not OperationCanceledException)
            {
                TryDelete(partial);
                throw new UpdateException(UpdateError.Network, "Failed to download the installer.", ex);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }

        /// <summary>
        /// Deletes installers left from earlier updates. Call at startup: by then the installer
        /// that updated the app has finished.
        /// </summary>
        public static void DeleteOldDownloads()
        {
            try
            {
                if (Directory.Exists(DownloadFolder))
                    Directory.Delete(DownloadFolder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use, for example by an installer that is running; try again next time.
                System.Diagnostics.Debug.WriteLine($"Failed to delete old update downloads: {ex}");
            }
        }

        private static string DownloadFolder => Path.Combine(Path.GetTempPath(), "NFDFix", "Updates");

        /// <summary>Starts the downloaded installer. The caller should exit the app right after.</summary>
        /// <exception cref="UpdateException">The installer could not be started.</exception>
        public static void LaunchInstaller(string path)
        {
            try
            {
                // /SILENT shows only the progress window: the user already chose to update.
                // /RELAUNCH makes installer\NFDFix.iss start the app again when it finishes.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    Arguments = "/SILENT /SP- /NORESTART /RELAUNCH",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                throw new UpdateException(UpdateError.Launch, "Failed to start the installer.", ex);
            }
        }

        internal static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version(0, 0, 0);
            if (string.IsNullOrWhiteSpace(tag))
                return false;

            var text = tag.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(text, out var parsed))
                return false;

            version = Normalize(parsed);
            return true;
        }

        /// <summary>Drops the revision so 1.2.0 and 1.2.0.0 compare equal.</summary>
        private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            // GitHub rejects API requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NFDFix", CurrentVersion.ToString()));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            return client;
        }

        private static DateTime? ReadLastCheck()
        {
            try
            {
                return File.Exists(LastCheckPath)
                    && DateTime.TryParse(File.ReadAllText(LastCheckPath).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
                    ? value.ToLocalTime()
                    : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static void SaveLastCheck()
        {
            var now = DateTime.Now;
            LastChecked = now;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LastCheckPath)!);
                File.WriteAllText(LastCheckPath, now.ToUniversalTime().ToString("O"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save the update check time: {ex}");
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete {path}: {ex}");
            }
        }
    }

    internal sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    internal sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }

    // Source-generated so the JSON reading survives Native AOT trimming.
    [JsonSerializable(typeof(GitHubRelease))]
    internal sealed partial class UpdateJsonContext : JsonSerializerContext;
}
