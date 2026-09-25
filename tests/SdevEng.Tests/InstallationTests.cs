namespace SdevEng.Tests;

public class InstallationTests
{
    static string Toolkit => AgentTool.FindToolkit();
    [Fact] public void DryRunNeverCreatesHome() { var home = Path.Combine(Path.GetTempPath(), "toolkit-no-write-" + Guid.NewGuid().ToString("N")); Assert.Equal(0, Installer.Run(Toolkit, home, null, "install", true, false).ExitCode); Assert.False(Directory.Exists(home)); }
    [Fact] public void ConflictsMakeNoPartialInstallation() { using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); Directory.CreateDirectory(Path.Combine(home, ".codex")); var file = Path.Combine(home, ".codex/AGENTS.md"); File.WriteAllText(file, "mine"); Assert.Equal(1, Installer.Run(Toolkit, home, null, "install", false, false).ExitCode); Assert.Equal("mine", File.ReadAllText(file)); Assert.False(Directory.Exists(Path.Combine(home, ".agents"))); }
    [Fact] public void InstallUpdateIdempotentAndUninstallPreservesReplacement() { using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); var bin = !OperatingSystem.IsWindows(); Assert.Equal(0, Installer.Run(Toolkit, home, null, "install", false, bin).ExitCode); Assert.Equal(0, Installer.Run(Toolkit, home, null, "update", false, bin).ExitCode); if (bin) { Assert.True(File.Exists(Path.Combine(home, ".local/bin/sdeveng"))); Assert.True(File.Exists(Path.Combine(home, ".local/bin/codex-agent-tool"))); } var file = Path.Combine(home, ".codex/AGENTS.md"); File.Delete(file); File.WriteAllText(file, "my replacement"); Assert.Equal(0, Installer.Run(Toolkit, home, null, "uninstall", false, false).ExitCode); Assert.Equal("my replacement", File.ReadAllText(file)); if (bin) { Assert.False(File.Exists(Path.Combine(home, ".local/bin/sdeveng"))); Assert.False(File.Exists(Path.Combine(home, ".local/bin/codex-agent-tool"))); } }
    [Fact] public async Task CanonicalAndLegacyLaunchersReportTheSameVersion() { if (OperatingSystem.IsWindows()) return; using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); Assert.Equal(0, Installer.Run(Toolkit, home, null, "install", false, true).ExitCode); var bin = Path.Combine(home, ".local/bin"); var canonical = await Processes.Run(Path.Combine(bin, "sdeveng"), ["--version"], repo.Root); var legacy = await Processes.Run(Path.Combine(bin, "codex-agent-tool"), ["--version"], repo.Root); Assert.Equal(0, canonical.ExitCode); Assert.Equal(canonical.Output, legacy.Output); Assert.Equal("sdeveng 3.0.0", canonical.Output.Trim()); }
    [Fact] public void RefusesSymlinkedDestinationParent() { using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); var elsewhere = Path.Combine(repo.Root, "elsewhere"); Directory.CreateDirectory(home); Directory.CreateDirectory(elsewhere); Directory.CreateSymbolicLink(Path.Combine(home, ".codex"), elsewhere); Assert.Throws<IOException>(() => Installer.Run(Toolkit, home, null, "install", false, false)); Assert.Empty(Directory.GetFiles(elsewhere)); }
    [Fact] public void TamperedOwnershipCannotDeleteArbitraryFile() { using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); Installer.Run(Toolkit, home, null, "install", false, false); var path = Path.Combine(home, ".codex/sdeveng-install.json"); var manifest = System.Text.Json.JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), AgentTool.Json)!; var victim = Path.Combine(repo.Root, "victim"); File.WriteAllText(victim, "mine"); manifest.Entries.Add(new(victim, Path.Combine(Toolkit, "global/AGENTS.md"), false)); File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest, AgentTool.Json)); Assert.Throws<IOException>(() => Installer.Run(Toolkit, home, null, "uninstall", false, false)); Assert.Equal("mine", File.ReadAllText(victim)); }
    [Fact] public void UninstallRemovesStaleOwnedLink() { using var repo = new TemporaryGitRepository(); var home = Path.Combine(repo.Root, "home"); Installer.Run(Toolkit, home, null, "install", false, false); var path = Path.Combine(home, ".codex/sdeveng-install.json"); var manifest = System.Text.Json.JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), AgentTool.Json)!; var source = Path.Combine(Toolkit, "agents", "obsolete.toml"); var destination = Path.Combine(home, ".codex", "agents", "obsolete.toml"); File.CreateSymbolicLink(destination, source); manifest.Entries.Add(new(destination, source, false)); File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest, AgentTool.Json)); Assert.Equal(0, Installer.Run(Toolkit, home, null, "uninstall", false, false).ExitCode); Assert.False(File.Exists(destination)); }
    [Fact] public void WindowsRejectsBinByContract() { if (OperatingSystem.IsWindows()) { using var repo = new TemporaryGitRepository(); Assert.Throws<PlatformNotSupportedException>(() => Installer.Run(Toolkit, Path.Combine(repo.Root, "home"), null, "install", false, true)); } }
    [Fact]
    public void UpdateMigratesLegacyManifestAndSkillLinks()
    {
        using var repo = new TemporaryGitRepository();
        var toolkit = Path.Combine(repo.Root, "toolkit");
        Copy(Toolkit, toolkit);
        var home = Path.Combine(repo.Root, "home");
        Installer.Run(toolkit, home, null, "install", false, false);
        var current = Path.Combine(home, ".codex", "sdeveng-install.json");
        var manifest = System.Text.Json.JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(current), AgentTool.Json)!;
        Directory.Move(Path.Combine(toolkit, "plugins", "sdeveng"), Path.Combine(toolkit, "plugins", "codex-toolkit"));
        manifest = manifest with { Entries = manifest.Entries.Select(e => e with { Source = e.Source.Replace("plugins/sdeveng", "plugins/codex-toolkit", StringComparison.Ordinal) }).ToList() };
        foreach (var entry in manifest.Entries.Where(e => e.Directory))
        {
            if (OperatingSystem.IsWindows()) Directory.Delete(entry.Destination);
            else File.Delete(entry.Destination);
            Directory.CreateSymbolicLink(entry.Destination, entry.Source);
        }
        File.WriteAllText(Path.Combine(home, ".codex", "codex-toolkit-install.json"), System.Text.Json.JsonSerializer.Serialize(manifest, AgentTool.Json));
        File.Delete(current);
        Directory.Move(Path.Combine(toolkit, "plugins", "codex-toolkit"), Path.Combine(toolkit, "plugins", "sdeveng"));

        var result = Installer.Run(toolkit, home, null, "update", false, false);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(home, ".codex", "codex-toolkit-install.json")));
        Assert.True(File.Exists(current));
        Assert.Equal(Path.Combine(toolkit, "plugins", "sdeveng", "skills", "benchmark"), Directory.ResolveLinkTarget(Path.Combine(home, ".agents", "skills", "benchmark"), false)!.FullName);
    }
    [Fact] public void UpdateReconcilesRemovedEntriesAndPreservesReplacements() { using var repo = new TemporaryGitRepository(); var toolkit = Path.Combine(repo.Root, "toolkit"); Copy(Toolkit, toolkit); var home = Path.Combine(repo.Root, "home"); Assert.Equal(0, Installer.Run(toolkit, home, null, "install", false, false).ExitCode); File.Delete(Path.Combine(toolkit, "agents", "reviewer.toml")); Directory.Delete(Path.Combine(toolkit, "plugins/sdeveng/skills/benchmark"), true); var agent = Path.Combine(home, ".codex", "agents", "reviewer.toml"); var skill = Path.Combine(home, ".agents", "skills", "benchmark"); var dry = System.Text.Json.JsonSerializer.Serialize(Installer.Run(toolkit, home, null, "update", true, false), AgentTool.Json); Assert.Contains("reviewer.toml", dry, StringComparison.Ordinal); Assert.Contains("benchmark", dry, StringComparison.Ordinal); Assert.Equal(0, Installer.Run(toolkit, home, null, "update", false, false).ExitCode); Assert.False(File.Exists(agent)); Assert.False(Directory.Exists(skill)); var replacement = Path.Combine(home, ".agents", "skills", "sbom"); Directory.Delete(replacement); Directory.CreateDirectory(replacement); File.WriteAllText(Path.Combine(replacement, "mine"), "mine"); Directory.Delete(Path.Combine(toolkit, "plugins/sdeveng/skills/sbom"), true); Assert.Equal(0, Installer.Run(toolkit, home, null, "update", false, false).ExitCode); Assert.Equal("mine", File.ReadAllText(Path.Combine(replacement, "mine"))); Assert.Equal(0, Installer.Run(toolkit, home, null, "update", false, false).ExitCode); Assert.Equal(0, Installer.Run(toolkit, home, null, "uninstall", false, false).ExitCode); Assert.Equal("mine", File.ReadAllText(Path.Combine(replacement, "mine"))); }
    static void Copy(string source, string destination) { Directory.CreateDirectory(destination); foreach (var file in SafeFiles.Enumerate(source)) { var target = Path.Combine(destination, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); } }
}
