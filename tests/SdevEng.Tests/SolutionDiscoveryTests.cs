namespace SdevEng.Tests;

public class SolutionDiscoveryTests
{
    [Fact] public void FindsBothSolutionFormats() { using var repo = new TemporaryGitRepository(); repo.Write("A.sln", "Microsoft Visual Studio Solution File, Format Version 12.00"); repo.Write("nested/B.slnx", "<Solution />"); repo.Write("obj/ignored.slnx", "<Solution />"); Assert.Equal(2, Projects.Solutions(repo.Root).Length); }
}
