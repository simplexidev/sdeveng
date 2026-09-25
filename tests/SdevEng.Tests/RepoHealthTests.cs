namespace SdevEng.Tests;

public class RepoHealthTests
{
    [Fact] public async Task ReadsInheritedMsbuildConfiguration() { using var repo = new TemporaryGitRepository(); repo.Write("global.json", "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestPatch\"}}"); repo.Write("Directory.Build.props", "<Project><PropertyGroup><Nullable>enable</Nullable><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><Deterministic>true</Deterministic><EnableNETAnalyzers>true</EnableNETAnalyzers></PropertyGroup></Project>"); repo.Write("src/A.csproj", ProjectDiscoveryTests.Project); Assert.Empty(await Projects.Health(repo.Root, new())); }
    [Fact] public async Task ReportsPolicyMismatch() { using var repo = new TemporaryGitRepository(); repo.Write("A.csproj", ProjectDiscoveryTests.Project); Assert.NotEmpty(await Projects.Health(repo.Root, new())); }
}
