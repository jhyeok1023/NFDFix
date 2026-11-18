using NFDFix.Services;

namespace NFDFix.Tests;

public class UpdateServiceTests
{
    private const string Suffix = "-win-x64-setup.exe";

    private static string Release(string tag, bool draft = false, bool prerelease = false, string assets = "") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/jhyeok1023/NFDFix/releases/tag/{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [{{assets}}]
        }
        """;

    private static string Asset(string name, long size = 100, string? digest = null) => $$"""
        {
          "name": "{{name}}",
          "browser_download_url": "https://github.com/jhyeok1023/NFDFix/releases/download/v1/{{name}}",
          "size": {{size}},
          "digest": {{(digest is null ? "null" : $"\"{digest}\"")}}
        }
        """;

    [Fact]
    public void NewerRelease_PicksInstallerForArchitecture()
    {
        var json = Release("v1.2.0", assets: string.Join(",",
            Asset("NFDFix-1.2.0-win-arm64-setup.exe"),
            Asset("NFDFix-1.2.0-win-x64-setup.exe", 7_400_000, "sha256:ABCDEF")));

        var update = UpdateService.ParseRelease(json, new Version(1, 0, 0, 0), Suffix);

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 2, 0), update.Version);
        Assert.Equal("NFDFix-1.2.0-win-x64-setup.exe", update.Installer?.Name);
        Assert.Equal(7_400_000, update.Installer?.Size);
        Assert.Equal("abcdef", update.Installer?.Sha256);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0")]
    [InlineData("v0.9.5")]
    public void SameOrOlderRelease_IsNotAnUpdate(string tag)
    {
        var json = Release(tag, assets: Asset($"NFDFix-{tag}{Suffix}"));
        Assert.Null(UpdateService.ParseRelease(json, new Version(1, 0, 0, 0), Suffix));
    }

    [Fact]
    public void DraftOrPrerelease_IsNotAnUpdate()
    {
        Assert.Null(UpdateService.ParseRelease(Release("v2.0.0", draft: true), new Version(1, 0, 0), Suffix));
        Assert.Null(UpdateService.ParseRelease(Release("v2.0.0", prerelease: true), new Version(1, 0, 0), Suffix));
    }

    [Fact]
    public void ReleaseWithoutMatchingInstaller_HasNoInstaller()
    {
        var json = Release("v1.1.0", assets: Asset("NFDFix-1.1.0-win-arm64-setup.exe"));

        var update = UpdateService.ParseRelease(json, new Version(1, 0, 0), Suffix);

        Assert.NotNull(update);
        Assert.Null(update.Installer);
    }

    [Theory]
    [InlineData("v1.2.3", true)]
    [InlineData("V1.2", true)]
    [InlineData("release-1", false)]
    [InlineData("", false)]
    public void TryParseTag(string tag, bool expected)
    {
        Assert.Equal(expected, UpdateService.TryParseTag(tag, out _));
    }
}
