using AppDock.Core.Docking;
using AppDock.Core.Geometry;
using AppDock.Core.Updates;

namespace AppDock.Core.Tests;

public sealed class UpdatePolicyTests
{
    [Theory]
    [InlineData("1.0.7", "1.0.8", UpdateAction.InstallSilently)]
    [InlineData("1.0.7", "1.1.0", UpdateAction.InstallSilently)]
    [InlineData("1.9.9", "1.10.0", UpdateAction.InstallSilently)] // numeric, not text, comparison
    [InlineData("1.0.7", "2.0.0", UpdateAction.NotifyOnly)]
    [InlineData("1.0.7", "1.0.7", UpdateAction.None)]
    [InlineData("1.0.7", "1.0.6", UpdateAction.None)]
    [InlineData("1.0.7", "0.9.0", UpdateAction.None)]
    [InlineData("1.0.7", "1.1.0-beta", UpdateAction.None)]
    [InlineData("dev", "1.1.0", UpdateAction.None)]
    public void Classify_silently_installs_only_newer_versions_of_the_same_major(string current, string remote, UpdateAction expected) =>
        Assert.Equal(expected, UpdatePolicy.Classify(current, remote));

    private static DockState State(DockMode mode, WindowVisibility visibility, bool present = true) =>
        new(mode, DockEdge.Right, null, visibility, present);

    [Fact]
    public void Restart_keeps_state_when_nothing_is_on_screen()
    {
        Assert.True(UpdatePolicy.RestartKeepsDockState(true, null));
        Assert.True(UpdatePolicy.RestartKeepsDockState(false, State(DockMode.Docked, WindowVisibility.Visible, present: false)));
    }

    [Theory]
    [InlineData(DockMode.Docked, WindowVisibility.Visible, true, true)]
    [InlineData(DockMode.Docked, WindowVisibility.Visible, false, false)] // would come back undocked
    [InlineData(DockMode.Docked, WindowVisibility.Hidden, true, false)] // would come back visible
    [InlineData(DockMode.Floating, WindowVisibility.Visible, false, true)]
    [InlineData(DockMode.Floating, WindowVisibility.Visible, true, false)] // would come back docked
    public void Restart_keeps_state_only_when_settings_describe_it(DockMode mode, WindowVisibility visibility, bool restore, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.RestartKeepsDockState(restore, State(mode, visibility)));

    [Theory]
    [InlineData("AppDock-1.0.8-x64.msi", "1.0.8")]
    [InlineData("appdock-1.2.3-X64.MSI", "1.2.3")]
    [InlineData("AppDock-x-x64.msi", null)]
    [InlineData("AppDock-1.0.8-x64.msi.download", null)]
    [InlineData("Other-1.0.8-x64.msi", null)]
    [InlineData("AppDock-updater.exe", null)]
    public void Reads_version_from_asset_name(string name, string? expected) =>
        Assert.Equal(expected, UpdatePolicy.VersionFromAssetName(name));

    [Fact]
    public void Asset_name_round_trips() =>
        Assert.Equal("1.0.8", UpdatePolicy.VersionFromAssetName(UpdatePolicy.AssetName("1.0.8")));

    [Fact]
    public void Parses_github_digest_as_lowercase_hex() =>
        Assert.Equal(new string('a', 64), UpdatePolicy.ParseSha256Digest("sha256:" + new string('A', 64)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:abc")]
    [InlineData("md5:6a2d07c7e590b1e7159dd92d95a7f2d25745720cbf5e01422c2faec520daed13")]
    [InlineData("sha256:zz2d07c7e590b1e7159dd92d95a7f2d25745720cbf5e01422c2faec520daed13")]
    public void Rejects_other_digests(string? digest) => Assert.Null(UpdatePolicy.ParseSha256Digest(digest));

    [Fact]
    public void Unsigned_file_is_rejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "not an installer");
            Assert.Null(PackageVerifier.SignerThumbprint(path));
            Assert.False(PackageVerifier.IsSignedBySameCertificate(path, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Sha256_is_lowercase_hex()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", PackageVerifier.ComputeSha256(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
