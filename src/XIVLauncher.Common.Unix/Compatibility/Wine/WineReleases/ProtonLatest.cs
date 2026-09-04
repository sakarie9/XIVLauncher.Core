namespace XIVLauncher.Common.Unix.Compatibility.Wine.Releases;

public sealed class ProtonLatestRelease(string parentFolder) : IWineRelease
{
    public string Label { get; } = "GE-Proton11-6";
    public string Description { get; } = "Compatibility tool for Steam Play based on Wine and additional components";
    public string Name { get; } = "GE-Proton11-6";
    public string ParentFolder { get; } = parentFolder;
    public string DownloadUrl { get; } = "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton11-6/GE-Proton11-6-x86_64.tar.gz";
    public string[] Checksums { get; } = [ "543e3af57bb138b1be5a5b98bba4d39ca59340bfa34ec8c12144f3e16d7434ed75bd7a68eafc228b16695884629595af0905156e5227c1898f93cdbc92cb5fcb" ];
    public bool Lsteamclient { get; } = true;
    public bool IsProton { get; } = true;
}
