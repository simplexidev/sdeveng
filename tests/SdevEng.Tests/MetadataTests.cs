using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Tomlyn;
using YamlDotNet.Serialization;

namespace SdevEng.Tests;

public class MetadataTests
{
    static string Root => AgentTool.FindToolkit();
    [Fact]
    public void AllJsonTomlAndYamlParse()
    {
        var yaml = new DeserializerBuilder().Build();
        foreach (var file in SafeFiles.Enumerate(Root))
        {
            if (Path.GetExtension(file) == ".json") { using var doc = JsonDocument.Parse(File.ReadAllText(file)); Assert.NotEqual(JsonValueKind.Undefined, doc.RootElement.ValueKind); }
            if (Path.GetExtension(file) == ".toml" || file.EndsWith(".toml.example", StringComparison.Ordinal)) Assert.False(Toml.Parse(File.ReadAllText(file)).HasErrors, file);
            if (Path.GetExtension(file) is ".yml" or ".yaml") Assert.NotNull(yaml.Deserialize<object>(File.ReadAllText(file)));
        }
    }
    [Fact]
    public void ConfigsConformToSchemas()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "config"), "*.json").Concat(Directory.GetFiles(Path.Combine(Root, "upstream"), "*.json")))
        {
            var instance = JsonNode.Parse(File.ReadAllText(file))!;
            var schemaPath = Path.GetFullPath(instance["$schema"]!.GetValue<string>(), Path.GetDirectoryName(file)!);
            var schema = JsonSchema.FromFile(schemaPath);
            var result = schema.Evaluate(instance, new() { OutputFormat = OutputFormat.List });
            Assert.True(result.IsValid, file + ": " + JsonSerializer.Serialize(result));
        }
    }
    [Fact]
    public void ReleaseIdentityIsThreePointZero()
    {
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/toolkit.json")))!;
        Assert.Equal("3.0.0", config["version"]!.GetValue<string>());
        Assert.Equal(0, Validation.Run(Root).ExitCode);
    }
    [Fact]
    public void PortableManifestIsCanonicalAndCodexCompatibilityManifestIsDerived()
    {
        var plugin = Path.Combine(Root, "plugins", "sdeveng");
        var portable = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, "plugin.json")))!.AsObject();
        var compatibility = JsonNode.Parse(File.ReadAllText(Path.Combine(plugin, ".codex-plugin", "plugin.json")))!;
        var portableSchema = JsonSchema.FromFile(Path.Combine(Root, "schemas", "portable-plugin.schema.json"));
        var compatibilitySchema = JsonSchema.FromFile(Path.Combine(Root, "schemas", "codex-plugin-compatibility.schema.json"));
        Assert.True(portableSchema.Evaluate(portable, new() { OutputFormat = OutputFormat.List }).IsValid);
        Assert.True(compatibilitySchema.Evaluate(compatibility, new() { OutputFormat = OutputFormat.List }).IsValid);
        Assert.Equal(PluginManifests.PortableSchema, portable["$schema"]!.GetValue<string>());
        Assert.Equal(PluginManifests.PluginName, portable["name"]!.GetValue<string>());
        Assert.Equal("SimplexiDev Engineering Toolkit", portable["author"]!["name"]!.GetValue<string>());
        Assert.Equal("https://github.com/simplexidev/sdeveng", portable["homepage"]!.GetValue<string>());
        Assert.Equal("https://github.com/simplexidev/sdeveng", portable["repository"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(PluginManifests.CompatibilityFrom(portable), compatibility));
        Assert.Equal("./skills/", compatibility["skills"]!.GetValue<string>());
        Assert.Equal(portable["extensions"]!["com.openai"]!["interface"]!.ToJsonString(), compatibility["interface"]!.ToJsonString());
        Assert.Contains(Directory.GetDirectories(Path.Combine(plugin, "skills")).Select(path => Path.Combine(path, "SKILL.md")), File.Exists);
    }
    [Fact]
    public void V3EcosystemContractHasOnePluginOneTemplateAndNoSiblingRuntimeDependencies()
    {
        var contract = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/ecosystem.json")))!;
        Assert.Equal("sdeveng", contract["productId"]!.GetValue<string>());
        Assert.Equal("SimplexiDev Engineering Toolkit", contract["productName"]!.GetValue<string>());
        Assert.Equal("simplexidev/sdeveng", contract["productRepository"]!.GetValue<string>());
        Assert.Equal("simplexidev/sdeveng-docs", contract["humanDocumentationRepository"]!.GetValue<string>());
        Assert.Equal("simplexidev/sdeveng-metrics-tooling", contract["metricsRepository"]!.GetValue<string>());
        Assert.Equal("https://simplexidev.github.io/sdeveng-metrics-dashboard/", contract["metricsPagesUrl"]!.GetValue<string>());
        Assert.Equal("simplexidev/codex-toolkit", contract["legacyLocations"]!["productRepository"]!.GetValue<string>());
        Assert.False(contract["siblingRepositoriesAreRuntimeDependencies"]!.GetValue<bool>());
        Assert.Equal("product", contract["agentRuntimeReferencesOwner"]!.GetValue<string>());
        Assert.Equal(new[] { "sdeveng" }, Directory.GetDirectories(Path.Combine(Root, "plugins")).Select(Path.GetFileName).Order());
        Assert.Equal(new[] { "project" }, Directory.GetDirectories(Path.Combine(Root, "templates")).Select(Path.GetFileName).Order());
        Assert.True(File.Exists(Path.Combine(Root, "plugins/sdeveng/references/v2-baseline.md")));
    }
    [Fact]
    public void CapabilityManifestIsCompleteConsistentAndMetricsReady()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/capabilities.json")))!;
        var capabilities = manifest["capabilities"]!.AsArray();
        var ids = capabilities.Select(x => x!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(31, capabilities.Count);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(capabilities.Count, manifest["summary"]!["total"]!.GetValue<int>());
        foreach (var coverage in new[] { "supported", "partial", "gap" })
            Assert.Equal(capabilities.Count(x => x!["coverage"]!.GetValue<string>() == coverage), manifest["summary"]![coverage]!.GetValue<int>());
        foreach (var required in new[]
        {
            "repo-navigation-impact", "git-local-state", "github-issues", "github-pull-requests", "github-reviews", "github-actions", "github-releases",
            "planning", "scoped-edits-refactors", "migrations", "dotnet-csharp", "tests", "msbuild", "diagnostics", "dependencies", "vulnerabilities",
            "licenses", "security-sarif", "docs-impact", "architecture", "api-compatibility", "benchmarks", "packaging", "reproducibility", "sbom", "ci",
            "release-integrity", "generated-dead-files", "merge-conflict-prep", "upstream-maintenance", "agent-skill-maintenance"
        }) Assert.Contains(required, ids);
        Assert.All(capabilities, capability =>
        {
            Assert.NotEmpty(capability!["existingSupport"]!.AsArray());
            Assert.NotNull(capability["opportunities"]!["deterministic"]);
            Assert.NotNull(capability["opportunities"]!["jev"]);
            Assert.NotNull(capability["opportunities"]!["agent"]);
            Assert.NotNull(capability["routing"]!["primary"]);
            Assert.NotNull(capability["staticCost"]!["level"]);
            Assert.NotNull(capability["recommendedAction"]);
        });
        Assert.True(File.Exists(Path.Combine(Root, "plugins/sdeveng/references/capability-coverage.md")));
    }
    [Fact]
    public void SdevengContractsAreVersionedUniqueAndMatchTheCliSurface()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/agent-tool-contracts.json")))!;
        Assert.Equal("sdeveng", manifest["cli"]!.GetValue<string>());
        Assert.Equal(AgentTool.CliVersion, manifest["cliVersion"]!.GetValue<string>());
        Assert.Equal(AgentTool.ResultSchemaVersion, manifest["resultSchemaVersion"]!.GetValue<int>());
        Assert.Equal(AgentTool.CliVersion, JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/toolkit.json")))!["version"]!.GetValue<string>());
        var contracts = manifest["contracts"]!.AsArray();
        var commands = contracts.Select(contract => contract!["command"]!.GetValue<string>()).ToArray();
        var kinds = contracts.Select(contract => contract!["kind"]!.GetValue<string>()).ToArray();
        Assert.Equal(commands.Length, commands.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, contracts.Select(contract => contract!["schemaVersion"]!.GetValue<int>()).Distinct().Single());
        var source = File.ReadAllText(Path.Combine(Root, "tools/AgentTool.cs"));
        Assert.All(commands.Where(command => command != "version"), command => Assert.Contains($"case \"{command}\"", source, StringComparison.Ordinal));
        Assert.Contains("command == \"version\"", source, StringComparison.Ordinal);
        Assert.All(kinds.Distinct(StringComparer.Ordinal), kind => Assert.Contains($"kind = \"{kind}\"", source, StringComparison.Ordinal));
    }
    [Fact]
    public void CliResultEnvelopesConformToThePublishedSchema()
    {
        var schema = JsonSchema.FromFile(Path.Combine(Root, "schemas/cli-result.schema.json"));
        var success = JsonNode.Parse(AgentTool.SerializeEnvelope(Result.Ok(new { kind = "repository-summary" }), "repo summary", null))!;
        var failure = JsonNode.Parse(AgentTool.SerializeEnvelope(new("error", null, 2), "repo locate", new { code = "invalid-invocation", message = "--query is required." }))!;
        Assert.True(schema.Evaluate(success, new() { OutputFormat = OutputFormat.List }).IsValid);
        Assert.True(schema.Evaluate(failure, new() { OutputFormat = OutputFormat.List }).IsValid);
    }
    [Fact]
    public void SkillsUseTheCanonicalStructuredCliContract()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "plugins/sdeveng/skills"), "SKILL.md", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("sdeveng ", StringComparison.Ordinal)) Assert.Contains("--json", text, StringComparison.Ordinal);
            Assert.DoesNotContain("AgentTool `", text, StringComparison.Ordinal);
        }
    }
    [Fact]
    public void DotnetSkillsProvenanceIsPinnedCompleteAndReferenceOnly()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "upstream/dotnet-skills.json")))!;
        Assert.Equal("dotnet/skills", manifest["snapshot"]!["repository"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{40}$", manifest["snapshot"]!["commit"]!.GetValue<string>());
        Assert.Equal("MIT", manifest["snapshot"]!["license"]!["spdx"]!.GetValue<string>());

        var plugins = manifest["plugins"]!.AsArray();
        var decisions = manifest["decisions"]!.AsArray();
        Assert.Equal(manifest["summary"]!["plugins"]!.GetValue<int>(), plugins.Count);
        Assert.Equal(manifest["summary"]!["skills"]!.GetValue<int>(), plugins.Sum(x => x!["skillCount"]!.GetValue<int>()));
        Assert.Equal(manifest["summary"]!["skillBytes"]!.GetValue<int>(), plugins.Sum(x => x!["skillBytes"]!.GetValue<int>()));
        Assert.Equal(manifest["summary"]!["skillWords"]!.GetValue<int>(), plugins.Sum(x => x!["skillWords"]!.GetValue<int>()));
        Assert.Equal(manifest["summary"]!["supportingFiles"]!.GetValue<int>(), plugins.Sum(x => x!["supportingFileCount"]!.GetValue<int>()));
        Assert.Equal(manifest["summary"]!["supportingBytes"]!.GetValue<int>(), plugins.Sum(x => x!["supportingBytes"]!.GetValue<int>()));
        var expectedPaths = plugins.SelectMany(plugin => plugin!["skills"]!.AsArray().Select(skill => $"plugins/{plugin["id"]!.GetValue<string>()}/skills/{skill!.GetValue<string>()}/SKILL.md")).Order(StringComparer.Ordinal).ToArray();
        var decisionPaths = decisions.SelectMany(x => x!["upstreamPaths"]!.AsArray()).Select(x => x!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedPaths, decisionPaths);
        Assert.DoesNotContain(decisions, x => x!["classification"]!.GetValue<string>().StartsWith("VENDOR_", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Root, "plugins/sdeveng/references/dotnet-skills-provenance.md")));
        Assert.False(Directory.Exists(Path.Combine(Root, "plugins/sdeveng/skills/dotnet")));
    }
    [Fact]
    public async Task ReleaseArchiveContainsUserFacingMetadataAndExcludesDevelopmentState()
    {
        using var repo = new TemporaryGitRepository(); var archive = Path.Combine(repo.Root, "sdeveng.zip");
        var result = await AgentTool.Execute(Cli.Parse(["release", "--output", archive]), Root, Root, Settings.Load(Root));
        Assert.Equal(0, result.ExitCode);
        using var zip = System.IO.Compression.ZipFile.OpenRead(archive); var entries = zip.Entries.Select(x => x.FullName).ToArray();
        Assert.Contains("CHANGELOG.md", entries); Assert.Contains("plugins/sdeveng/.codex-plugin/plugin.json", entries);
        Assert.Contains("docs/jev.md", entries);
        foreach (var humanManual in new[] { "architecture", "configuration", "evaluation", "installation", "model-routing", "project-integration", "release-process", "security", "skill-authoring", "token-efficiency", "troubleshooting", "upstream-integrations" })
            Assert.DoesNotContain($"docs/{humanManual}.md", entries);
        Assert.DoesNotContain(entries, x => x.StartsWith("tests/", StringComparison.Ordinal) || x.StartsWith(".agent-results/", StringComparison.Ordinal) || x.StartsWith("artifacts/", StringComparison.Ordinal));
    }
    [Fact]
    public void SkillMetadataAndReferencesExist()
    {
        var yaml = new DeserializerBuilder().Build();
        foreach (var dir in Directory.GetDirectories(Path.Combine(Root, "plugins/sdeveng/skills")))
        {
            var text = File.ReadAllText(Path.Combine(dir, "SKILL.md")); var front = text.Split("---", 3)[1];
            var metadata = yaml.Deserialize<Dictionary<string, object>>(front);
            Assert.Equal(Path.GetFileName(dir), metadata["name"]); Assert.InRange(metadata["description"].ToString()!.Length, 20, 250);
            Assert.True(File.Exists(Path.Combine(dir, "agents/openai.yaml")));
            Assert.True(File.Exists(Path.Combine(Root, "evals", Path.GetFileName(dir), "eval.yaml")));
        }
        Assert.Equal(0, Validation.Run(Root).ExitCode);
    }
    [Fact]
    public void NativeAgentsHaveRequiredFields()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Root, "agents"), "*.toml"))
        {
            var agent = Toml.ToModel(File.ReadAllText(file));
            Assert.True(agent.ContainsKey("name")); Assert.True(agent.ContainsKey("description")); Assert.True(agent.ContainsKey("developer_instructions")); Assert.Equal("read-only", agent["sandbox_mode"]);
        }
        Assert.Contains("Do not delegate, spawn subagents", File.ReadAllText(Path.Combine(Root, "agents", "reviewer.toml")), StringComparison.Ordinal);
    }
    [Fact]
    public void AgentCandidateManifestIsBoundedMetricsReadyAndDoesNotInstallCandidates()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/agent-candidates.json")))!;
        var builtIns = manifest["builtInRoles"]!.AsArray();
        var toolkit = manifest["toolkitRoles"]!.AsArray();
        var candidates = manifest["candidateRoles"]!.AsArray();
        var roles = builtIns.Concat(toolkit).Concat(candidates).ToArray();
        var ids = roles.Select(x => x!["id"]!.GetValue<string>()).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "reviewer.toml" }, Directory.GetFiles(Path.Combine(Root, "agents"), "*.toml").Select(Path.GetFileName).Order());
        Assert.Contains(toolkit, x => x!["id"]!.GetValue<string>() == "code-mapper" && x["disposition"]!.GetValue<string>() == "REPLACE_WITH_BUILTIN");
        Assert.Contains(toolkit, x => x!["id"]!.GetValue<string>() == "log-analyzer" && x["disposition"]!.GetValue<string>() == "RETIRE");
        Assert.Contains(builtIns, x => x!["id"]!.GetValue<string>() == "built-in-explorer" && x["availability"]!.GetValue<string>() == "not-exposed");
        Assert.All(candidates, x => Assert.Equal("INSUFFICIENT_EVIDENCE", x!["disposition"]!.GetValue<string>()));
        Assert.Contains("eval-judge", manifest["policy"]!["runtimeDefaultExclusions"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("root-only; delegated agents must not delegate", manifest["policy"]!["delegation"]!.GetValue<string>());
        Assert.Contains(manifest["evidence"]!.AsArray(), x => x!["id"]!.GetValue<string>() == "agent-capability-evaluation-2026-09-23");
        var scenarios = manifest["scenarios"]!.AsArray();
        Assert.Equal(new[] { "build-diagnostician", "code-mapper-vs-built-in-explorer", "log-analyzer-vs-runtime-diagnostician", "release-auditor", "reviewer-vs-security-reviewer", "test-specialist", "upstream-reviewer" }, scenarios.Select(x => x!["id"]!.GetValue<string>()).Order());
        Assert.All(scenarios, scenario => Assert.InRange(scenario!["arms"]!.AsArray().Count, 2, 3));
    }
    [Fact]
    public void SkillActivationMetadataStaysNarrowAndCredentialFree()
    {
        var yaml = new DeserializerBuilder().Build();
        Assert.False(Directory.Exists(Path.Combine(Root, "plugins/sdeveng/skills/repo-locate")));
        Assert.False(Directory.Exists(Path.Combine(Root, "evals/repo-locate")));
        Assert.Equal(new[] { "reviewer.toml" }, Directory.GetFiles(Path.Combine(Root, "agents"), "*.toml").Select(Path.GetFileName).Order());
        foreach (var skill in Directory.GetDirectories(Path.Combine(Root, "plugins/sdeveng/skills")))
        {
            var name = Path.GetFileName(skill);
            var metadata = yaml.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(skill, "agents/openai.yaml")));
            var ui = (Dictionary<object, object>)metadata["interface"];
            Assert.Equal($"Use ${name}.", ui["default_prompt"]);
            Assert.DoesNotContain("...", ui["short_description"].ToString());
            Assert.DoesNotContain("TYPESAFE_API_KEY", File.ReadAllText(Path.Combine(skill, "SKILL.md")));
            Assert.DoesNotContain("Commands above use", File.ReadAllText(Path.Combine(skill, "SKILL.md")));
        }
        Assert.Contains("allow_implicit_invocation: false", File.ReadAllText(Path.Combine(Root, "plugins/sdeveng/skills/jev-judgment/agents/openai.yaml")));
        Assert.Contains("allow_implicit_invocation: false", File.ReadAllText(Path.Combine(Root, "plugins/sdeveng/skills/package-audit/agents/openai.yaml")));
    }

    [Fact]
    public void OptimizedDotnetTestSkillsStayCompactAttributedAndLazy()
    {
        var skillsRoot = Path.Combine(Root, "plugins/sdeveng/skills");
        var names = Directory.GetDirectories(skillsRoot).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        foreach (var name in new[] { "run-dotnet-tests", "write-dotnet-tests", "dotnet-test-quality", "dotnet-coverage" }) Assert.Contains(name, names);
        Assert.DoesNotContain("dotnet-verify", names); Assert.DoesNotContain("test-quality", names);

        var files = new[]
        {
            "plugins/sdeveng/skills/run-dotnet-tests/SKILL.md",
            "plugins/sdeveng/skills/write-dotnet-tests/SKILL.md",
            "plugins/sdeveng/skills/dotnet-test-quality/SKILL.md",
            "plugins/sdeveng/skills/dotnet-coverage/SKILL.md",
            "plugins/sdeveng/references/test-platform-edge-cases.md",
            "plugins/sdeveng/references/test-framework-edge-cases.md",
            "plugins/sdeveng/references/test-quality-checks.md"
        };
        Assert.InRange(files.Sum(file => new FileInfo(Path.Combine(Root, file)).Length), 1, 12_000);
        Assert.All(files.Take(4), file => Assert.InRange(new FileInfo(Path.Combine(Root, file)).Length, 1, 2_500));
        foreach (var file in files.Skip(4))
        {
            var text = File.ReadAllText(Path.Combine(Root, file));
            Assert.Contains("4ed5f7c121da8dd31af31a35cef05070948c6556", text, StringComparison.Ordinal);
            Assert.Contains("plugins/dotnet-test/skills/", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BuildDiagnosticsPerformanceAndAdvancedDotnetStayCompactAndLazy()
    {
        var files = new[]
        {
            "plugins/sdeveng/skills/diagnose-build/SKILL.md",
            "plugins/sdeveng/skills/optimize-build/SKILL.md",
            "plugins/sdeveng/skills/diagnose-dotnet/SKILL.md",
            "plugins/sdeveng/skills/investigate-dotnet-performance/SKILL.md",
            "plugins/sdeveng/references/msbuild-diagnostics.md",
            "plugins/sdeveng/references/msbuild-performance.md",
            "plugins/sdeveng/references/advanced-dotnet-routing.md"
        };
        Assert.All(files.Take(4), file => Assert.InRange(new FileInfo(Path.Combine(Root, file)).Length, 1, 2_500));
        Assert.InRange(files.Sum(file => new FileInfo(Path.Combine(Root, file)).Length), 1, 12_000);
        foreach (var file in files.Skip(4))
        {
            var text = File.ReadAllText(Path.Combine(Root, file));
            Assert.Contains("4ed5f7c121da8dd31af31a35cef05070948c6556", text, StringComparison.Ordinal);
            Assert.Contains("plugins/dotnet-", text, StringComparison.Ordinal);
        }
        var build = File.ReadAllText(Path.Combine(Root, files[0]));
        Assert.True(build.IndexOf("structurally", StringComparison.Ordinal) < build.IndexOf("raw-log", StringComparison.Ordinal));
        Assert.DoesNotContain("diagnostics", Directory.GetDirectories(Path.Combine(Root, "plugins/sdeveng/skills")).Select(Path.GetFileName));
        Assert.DoesNotContain("performance-investigation", Directory.GetDirectories(Path.Combine(Root, "plugins/sdeveng/skills")).Select(Path.GetFileName));
    }

    [Fact]
    public void GeneralRepositoryCapabilitiesUseOneNarrowNewSkill()
    {
        var skills = Directory.GetDirectories(Path.Combine(Root, "plugins/sdeveng/skills")).Select(Path.GetFileName).ToArray();
        Assert.Contains("ci-triage", skills);
        Assert.DoesNotContain("artifact-inspection", skills); Assert.DoesNotContain("sarif-diff", skills); Assert.DoesNotContain("merge-conflicts", skills); Assert.DoesNotContain("repository-cleanup", skills);
        Assert.InRange(new FileInfo(Path.Combine(Root, "plugins/sdeveng/skills/ci-triage/SKILL.md")).Length, 1, 2_500);
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "config/capabilities.json")))!;
        Assert.Equal(1, manifest["summary"]!["gap"]!.GetValue<int>());
        Assert.Equal("licenses", manifest["capabilities"]!.AsArray().Single(x => x!["coverage"]!.GetValue<string>() == "gap")!["id"]!.GetValue<string>());
    }

    [Fact]
    public void LiveJevWorkflowIsManuallyOrLowFrequencyTriggeredAndSecretIsStepScoped()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github/workflows/jev-integration.yml"));
        Assert.Contains("workflow_dispatch:", workflow);
        Assert.Contains("cron: '23 10 * * 3'", workflow);
        Assert.DoesNotContain("pull_request:", workflow);
        Assert.Contains("environment: jev-integration", workflow);
        Assert.Contains("TYPESAFE_API_KEY: ${{ secrets.TYPESAFE_API_KEY }}", workflow);
        Assert.Contains("JEV_MODE: required", workflow);
        Assert.DoesNotContain("permissions: write-all", workflow);
        Assert.DoesNotContain("echo $TYPESAFE_API_KEY", workflow);
        Assert.DoesNotContain("printf '%s\\n' \"$TYPESAFE_API_KEY\"", workflow);
    }

    [Fact]
    public void ReleaseChecksumsUseDownloadableAssetNames()
    {
        var workflow = File.ReadAllText(Path.Combine(Root, ".github/workflows/release.yml"));
        Assert.Contains("(cd artifacts && sha256sum sdeveng.zip > SHA256SUMS)", workflow);
        Assert.DoesNotContain("sha256sum artifacts/sdeveng.zip > artifacts/SHA256SUMS", workflow);
    }
}

public class RepositoryIntegrityTests
{
    [Fact]
    public void RuntimeReferenceValidationDetectsBrokenSkillPaths()
    {
        using var repo = new TemporaryGitRepository();
        var skill = Path.Combine(repo.Root, "plugins/sdeveng/skills/example");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "Read references/missing.md and [also missing](references/other.md).\n");
        var errors = RuntimeReferences.Missing(repo.Root);
        Assert.Contains(errors, error => error.Contains("references/missing.md", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("references/other.md", StringComparison.Ordinal));
    }

    [Fact]
    public void LocalMarkdownLinksResolve()
    {
        var root = AgentTool.FindToolkit();
        foreach (var file in SafeFiles.Enumerate(root).Where(p => p.EndsWith(".md", StringComparison.Ordinal)))
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"\]\(([^)]+)\)"))
            {
                var target = match.Groups[1].Value.Split('#')[0];
                if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal)) continue;
                var resolved = Path.GetFullPath(target, Path.GetDirectoryName(file)!);
                Assert.True(File.Exists(resolved) || Directory.Exists(resolved), $"Broken link in {file}: {target}");
            }
        }
    }
    [Fact]
    public async Task GeneratedStateIsIgnored()
    {
        var root = AgentTool.FindToolkit();
        var result = await Processes.Run("git", new[] { "check-ignore", ".agent-tool/jev-cache/example.json", "artifacts/release.zip", "TestResults/result.trx" }, root);
        Assert.Equal(0, result.ExitCode); Assert.Equal(3, result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    [Fact]
    public void SchemaRejectsInvalidConfiguration()
    {
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/jev-config.schema.json"));
        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(AgentTool.FindToolkit(), "config/jev.json")))!;
        config["timeoutSeconds"] = -1;
        Assert.False(schema.Evaluate(config).IsValid);
    }
}
