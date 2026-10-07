using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using SdevEng;
using SdevEng.Infrastructure;

public sealed class SkillCostMeasurementTests
{
    [Fact]
    public async Task ExplainUsesDiscoveryActivationEvidenceThroughNormalCommand()
    {
        var toolkit = AgentTool.FindToolkit();
        var catalog = SkillCompatibilityMapReader.ReadMetadataIndex(toolkit).Select(s => new SkillCatalogIdentity(s.Id!, s.Version!)).ToArray();
        var skill = catalog[0];
        using var fixture = new Fixture("S", "R", "M", "private-unloaded-body", "Q", "O");
        var prompt = fixture.Prompt with
        {
            SchemaVersion = 2,
            Components = fixture.Prompt.Components.Select(c => c.Id switch
        {
            PromptComponentId.SkillMetadata => c with { ContentReference = $"{skill.Id}/{skill.Version}/metadata" },
            PromptComponentId.SkillInstructions => c with { ContentReference = $"{skill.Id}/{skill.Version}/instructions", IsLoaded = false },
            _ => c
        }).ToArray()
        };
        var text = new Dictionary<string, string>(fixture.Text) { [$"{skill.Id}/{skill.Version}/metadata"] = "M" };
        var activation = new SkillActivationLoadResult(1, new("coder", [], [], [], []), 100, fixture.Tokenizer,
            [skill.Id], 0, 100, [new(new(skill.Id, null, "omitted", null, "content-unavailable", 0, 0), 0)]);
        var observation = new SkillCostObservation("coder", catalog, [skill.Id, catalog[1].Id], activation);
        var request = new SkillCostExplainRequest(prompt, text, observation, fixture.Tokenizer, fixture.Template);
        var result = fixture.Service.Explain(toolkit, request);
        Assert.Equal(2, result.ConsideredSkills);
        Assert.Equal(1, result.ActivatedSkills);
        Assert.Equal(0, result.InstructionLoadedSkills);
        Assert.Equal(observation.ConsideredSkillIds, result.Identities!.Considered);
        Assert.Contains(result.Components, c => c.ComponentKind == "instructions" && c.Tokens is null && c.OmissionReason == "content-unavailable");
        Assert.Contains(result.Components, c => c.SkillId == catalog[1].Id && c.Tokens is null && c.OmissionReason == "skill-not-activated");
        AssertArtifact(result);
        Assert.Throws<ArgumentException>(() => fixture.Service.Explain(toolkit, request with { Observation = observation with { ConsideredSkillIds = [catalog[1].Id] } }));
        Assert.Throws<ArgumentException>(() => fixture.Service.Explain(toolkit, request with { Observation = observation with { Role = "reviewer" } }));
        Assert.Throws<ArgumentException>(() => fixture.Service.Explain(toolkit, request with { Observation = observation with { Catalog = catalog.Skip(1).ToArray() } }));
        Assert.Throws<ArgumentException>(() => fixture.Service.Explain(toolkit, request with { Prompt = prompt with { Components = prompt.Components.Select(c => c.Id == PromptComponentId.SkillInstructions ? c with { IsLoaded = true } : c).ToArray() } }));
        var unavailable = new SkillCostMeasurementService(new UnavailableCounter()).Explain(toolkit, request);
        Assert.Null(unavailable.TokenShare);
        AssertArtifact(unavailable);
        var zero = new SkillCostMeasurementService(new ExactCounter(0, 0)).Explain(toolkit, request);
        Assert.Null(zero.TokenShare);
        AssertArtifact(zero);
        const string loadedBody = "private-loaded-body";
        var loadedActivation = activation with
        {
            ConsumedTokens = loadedBody.Length,
            RemainingTokens = 100 - loadedBody.Length,
            Evidence = [new(new(skill.Id, null, "loaded", loadedBody, null, loadedBody.Length, loadedBody.Length), loadedBody.Length)]
        };
        var loadedText = new Dictionary<string, string>(text) { [$"{skill.Id}/{skill.Version}/instructions"] = loadedBody };
        var loadedRequest = request with
        {
            TextByContentReference = loadedText,
            Observation = observation with { Activation = loadedActivation },
            Prompt = prompt with { Components = prompt.Components.Select(c => c.Id == PromptComponentId.SkillInstructions ? c with { IsLoaded = true } : c).ToArray() }
        };
        var loadedResult = fixture.Service.Explain(toolkit, loadedRequest);
        Assert.Equal(1, loadedResult.InstructionLoadedSkills);
        Assert.Contains(loadedResult.Components, c => c.ComponentKind == "instructions" && c.Tokens == loadedBody.Length);
        Assert.DoesNotContain(loadedBody, JsonSerializer.Serialize(loadedResult, InfrastructureJson.Options));
        AssertArtifact(loadedResult);
        var path = Path.Combine(fixture.Root, "explain.json");
        var schemaOptions = new EvaluationOptions();
        foreach (var name in new[] { "prompt-manifest", "tokenizer-manifest", "chat-template-manifest", "skill-activation-load-result", "skill-load-result" })
            schemaOptions.SchemaRegistry.Register(new Uri($"https://simplexidev.github.io/sdeveng/schemas/{name}.schema.json"), JsonSchema.FromFile(Path.Combine(toolkit, $"schemas/{name}.schema.json")));
        var requestJson = JsonSerializer.SerializeToNode(request, InfrastructureJson.Options)!;
        var schemaResult = JsonSchema.FromFile(Path.Combine(toolkit, "schemas/skill-cost-explain-request.schema.json")).Evaluate(requestJson, schemaOptions);
        Assert.True(schemaResult.IsValid, JsonSerializer.Serialize(schemaResult));
        File.WriteAllText(path, JsonSerializer.Serialize(request, InfrastructureJson.Options));
        var cli = Cli.Parse(["skills", "explain", "--input", path]);
        var commandResult = await SdevEng.Tests.CommandTestRuntime.Execute(cli, toolkit, toolkit, new(new(), new(), new(), new()));
        Assert.Equal(0, commandResult.ExitCode);
        var json = JsonSerializer.Serialize(commandResult.Data, AgentTool.Json);
        Assert.DoesNotContain("private-unloaded-body", json);
        var replay = JsonSerializer.Deserialize<SkillCostMeasurement>(json, InfrastructureJson.Options)!;
        Assert.Equal(result.TotalInputTokens, replay.TotalInputTokens);
        AssertArtifact(replay);
    }

    [Fact]
    public void EvaluationUsesCanonicalInventoryAndPreservesOmission()
    {
        var root = AgentTool.FindToolkit();
        var scenario = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "evals/skill-cost/fixtures/scenario.json")))!;
        Assert.Equal(1, scenario["schemaVersion"]!.GetValue<int>());
        var inventory = SkillCompatibilityMapReader.ReadMetadataIndex(root);
        var skill = inventory.Single(s => s.Id == scenario["skillId"]!.GetValue<string>());
        using var fixture = new Fixture("S", "R", "", "", "Q", "O");
        var metadataReference = $"{skill.Id}/{skill.Version}/metadata";
        var instructionsReference = $"{skill.Id}/{skill.Version}/instructions";
        var prompt = fixture.Prompt with
        {
            Components = fixture.Prompt.Components.Select(c => c.Id switch
        {
            PromptComponentId.SkillMetadata => c with { ContentReference = metadataReference },
            PromptComponentId.SkillInstructions => c with { ContentReference = instructionsReference, IsLoaded = false },
            _ => c
        }).ToArray()
        };
        fixture.Text[metadataReference] = JsonSerializer.Serialize(skill, InfrastructureJson.Options);
        var result = fixture.Service.Measure("test", "1", fixture.Checksum, prompt, fixture.Text,
            new Dictionary<string, string> { [instructionsReference] = scenario["instructionOmissionReason"]!.GetValue<string>() },
            inventory.Select(s => s.Id!).ToArray(), scenario["consideredSkillIds"]!.Deserialize<string[]>()!, scenario["activatedSkillIds"]!.Deserialize<string[]>()!);
        Assert.Equal(inventory.Length, result.AvailableSkills);
        Assert.Equal(scenario["expectedConsideredSkills"]!.GetValue<int>(), result.ConsideredSkills);
        Assert.Equal(scenario["expectedActivatedSkills"]!.GetValue<int>(), result.ActivatedSkills);
        Assert.Equal(scenario["expectedInstructionLoadedSkills"]!.GetValue<int>(), result.InstructionLoadedSkills);
        Assert.All(result.Components, c => Assert.Equal(skill.Id, c.SkillId));
        Assert.Contains(result.Components, c => c.ComponentKind == "metadata" && c.Tokens > 0);
        Assert.Contains(result.Components, c => c.ComponentKind == "instructions" && c.Tokens is null && c.OmissionReason == "not-requested");
        AssertArtifact(result);
        Assert.Equal(0, Evaluation.Run(root, "skill-cost", null).ExitCode);
    }

    [Fact]
    public void MeasuresLoadedSkillCostsAndSeparatesConsideredFromActivated()
    {
        using var fixture = new Fixture("S", "R", "M", "I", "Q", "O");
        var result = fixture.Service.Measure("test", "1", fixture.Checksum, fixture.Prompt, fixture.Text,
            consideredSkillIds: ["alpha", "beta"], activatedSkillIds: ["alpha"]);

        Assert.Equal(2, result.ConsideredSkills);
        Assert.Equal(1, result.ActivatedSkills);
        Assert.Equal(1, result.InstructionLoadedSkills);
        Assert.Equal(1, result.AvailableSkills);
        Assert.Equal(2, result.SkillTokens);
        Assert.Equal(60, result.TotalInputTokens);
        Assert.Equal(54, result.TemplateOverheadTokens);
        Assert.Equal(4, result.NonSkillInputTokens);
        Assert.Equal(result.TotalInputTokens, result.SkillTokens + result.TemplateOverheadTokens + result.NonSkillInputTokens);
        Assert.Equal(2d / 60, result.TokenShare);
        Assert.Contains(result.Components, c => c.SkillId == "alpha" && c.ComponentKind == "metadata" && c.ContentReference == "alpha/v1/metadata" && c.Tokens == 1);
        Assert.Contains(result.Components, c => c.SkillId == "alpha" && c.ComponentKind == "instructions" && c.ContentReference == "alpha/v1/instructions" && c.Tokens == 1);

        AssertArtifact(result);
    }

    [Fact]
    public void DoesNotCountUnloadedBodyAndUsesNullShareWhenDenominatorUnavailableOrZero()
    {
        using var fixture = new Fixture("S", "R", "M", "I", "Q", "O");
        var unloaded = fixture.Prompt with { Components = fixture.Prompt.Components.Select(c => c.Id == PromptComponentId.SkillInstructions ? c with { IsLoaded = false } : c).ToArray() };
        var unloadedText = fixture.Text.Where(item => item.Key != "alpha/v1/instructions").ToDictionary(item => item.Key, item => item.Value);
        var unloadedResult = fixture.Service.Measure("test", "1", fixture.Checksum, unloaded, unloadedText);
        Assert.DoesNotContain(unloadedResult.Components, c => c.ComponentKind == "instructions" && c.Tokens > 0);
        Assert.Contains(unloadedResult.Components, c => c.ComponentKind == "instructions" && c.Tokens is null);
        Assert.Equal(0, unloadedResult.InstructionLoadedSkills);
        Assert.Equal("exact", unloadedResult.MeasurementKind);
        Assert.DoesNotContain(unloadedResult.InputMeasurement.Attribution!.Components, c => c.Span.ContentReference == "alpha/v1/instructions");
        var expectedPrompt = unloaded with { Components = unloaded.Components.Where(c => c.IsLoaded).ToArray() };
        var expected = fixture.Counter.CountAttributed("test", "1", fixture.Checksum, expectedPrompt, unloadedText);
        Assert.Equal(expected.Tokens, unloadedResult.TotalInputTokens);
        Assert.Equal(JsonSerializer.Serialize(unloadedResult), JsonSerializer.Serialize(fixture.Service.Measure("test", "1", fixture.Checksum, unloaded, fixture.Text)));
        AssertArtifact(unloadedResult);

        using var framed = new Fixture("", "", "", "", "", "");
        var framedResult = framed.Service.Measure("test", "1", framed.Checksum, framed.Prompt, framed.Text);
        Assert.Equal("exact", framedResult.MeasurementKind);
        // Six framed messages: 30 role bytes + 18 special tokens + five separators + generation prefix.
        Assert.Equal(54, framedResult.TotalInputTokens);
        Assert.Equal(0, framedResult.SkillTokens);
        Assert.Equal(0, framedResult.TokenShare);
        AssertArtifact(framedResult);

        var zeroResult = new SkillCostMeasurementService(new ExactCounter(0, 0)).Measure("test", "1", framed.Checksum, framed.Prompt, framed.Text);
        Assert.Equal("exact", zeroResult.MeasurementKind);
        Assert.Null(zeroResult.TokenShare);
        Assert.Equal(0, zeroResult.TotalInputTokens);
        AssertArtifact(zeroResult);

        var unavailable = new SkillCostMeasurementService(new UnavailableCounter()).Measure("test", "1", fixture.Checksum, fixture.Prompt, fixture.Text);
        Assert.Equal("unavailable", unavailable.MeasurementKind);
        Assert.Null(unavailable.TokenShare);
        Assert.Null(unavailable.TotalInputTokens);
        Assert.Equal("counter-unavailable", unavailable.UnavailableReason);
        AssertArtifact(unavailable);
    }

    [Fact]
    public void MissingLoadedContentRemainsUnavailable()
    {
        using var fixture = new Fixture("S", "R", "M", "I", "Q", "O");
        fixture.Text.Remove("alpha/v1/instructions");
        var result = fixture.Service.Measure("test", "1", fixture.Checksum, fixture.Prompt, fixture.Text);
        Assert.Equal("unavailable", result.MeasurementKind);
        Assert.Equal("prompt-content-unavailable", result.UnavailableReason);
        AssertArtifact(result);
    }

    [Fact]
    public void ResourceDescriptorKeepsReferenceIdentityAndDefersBodyCost()
    {
        using var fixture = new Fixture("S", "R", "M", "I", "Q", "O");
        var components = fixture.Prompt.Components.ToList();
        components.Insert(4, new(PromptComponentId.SkillReferences, "coder", "test", "alpha/v1/reference.md", "hash", IsLoaded: false));
        var prompt = fixture.Prompt with { Components = components };
        fixture.Text["alpha/v1/reference.md"] = "resource descriptor";
        var result = fixture.Service.Measure("test", "1", fixture.Checksum, prompt, fixture.Text);
        Assert.Equal("exact", result.MeasurementKind);
        Assert.Equal(2, result.SkillTokens);
        Assert.Contains(result.Components, c => c.ComponentKind == "reference" && c.ContentReference == "alpha/v1/reference.md" &&
            c.Tokens is null && c.OmissionReason == "reference-cost-deferred");
        AssertArtifact(result);
    }

    [Fact]
    public async Task LoadedReferenceUsesCanonicalLazyLoadAndFinalInputAttribution()
    {
        using var fixture = new Fixture("S", "R", "M", "I", "Q", "O");
        var root = Path.Combine(Path.GetTempPath(), "skill-cost-load-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            var directory = Path.Combine(root, "plugins/sdeveng/skills/alpha");
            Directory.CreateDirectory(Path.Combine(directory, "agents"));
            File.WriteAllText(Path.Combine(root, "plugins/sdeveng/plugin.json"), "{\"version\":\"3.0.0\"}");
            File.WriteAllText(Path.Combine(directory, "agents/openai.yaml"), "interface: {}");
            const string body = "reference é";
            var hash = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            var resources = JsonSerializer.Serialize(new[] { new SkillResource("one.md", "reference", hash) }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: alpha\nactivation: '[{{\"id\":\"build\"}}]'\nresources: '{resources}'\n---\nInstructions.");
            File.WriteAllText(Path.Combine(directory, "one.md"), body);
            const string path = "plugins/sdeveng/skills/alpha/SKILL.md";
            File.WriteAllText(Path.Combine(root, "config/skill-compatibility-map.json"), JsonSerializer.Serialize(
                new SkillCompatibilityMap("../schemas/skill-compatibility-map.schema.json", 1,
                    [new("alpha", path, "alpha", path, "3.0.0", [], "retained")]), InfrastructureJson.Options));
            var components = fixture.Prompt.Components.ToList();
            components.Insert(4, new(PromptComponentId.SkillReferences, "coder", "lazy-load", "alpha/3.0.0/one.md", hash, IsLoaded: false));
            var prompt = fixture.Prompt with { SchemaVersion = 2, Components = components };
            var descriptor = fixture.Service.Measure("test", "1", fixture.Checksum, prompt, fixture.Text);
            Assert.Equal("exact", descriptor.MeasurementKind);
            Assert.Null(descriptor.Components.Single(c => c.ComponentKind == "reference").Tokens);
            var load = await new SkillActivationService().LoadReferenceAsync(root, new("coder", ["build"], [], [], []), "alpha", "one.md");
            var result = fixture.Service.MeasureLoadedReferences(root, "test", "1", fixture.Checksum, prompt, fixture.Text, [load]);
            Assert.Equal(1, result.ReferenceLoadedSkills);
            Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(body), result.Components.Single(c => c.ComponentKind == "reference").Tokens);
            Assert.Equal(2 + load.Utf8Bytes, result.SkillTokens);
            Assert.Equal((double)result.SkillTokens! / result.TotalInputTokens!, result.TokenShare);
            AssertArtifact(result);
            AssertArtifact(descriptor);
            Assert.Throws<ArgumentException>(() => (result with
            {
                TemplateOverheadTokens = result.TemplateOverheadTokens + 1,
                NonSkillInputTokens = result.NonSkillInputTokens - 1
            }).Validate());
            var omitted = fixture.Service.MeasureLoadedReferences(root, "test", "1", fixture.Checksum, prompt, fixture.Text,
                [new("alpha", "one.md", "omitted", null, "hash-mismatch", 0, 0)]);
            Assert.Equal("exact", omitted.MeasurementKind);
            Assert.Equal(0, omitted.ReferenceLoadedSkills);
            Assert.Contains(omitted.Components, c => c.ComponentKind == "reference" && c.Tokens is null && c.OmissionReason == "hash-mismatch");
            AssertArtifact(omitted);
            Assert.Throws<ArgumentException>(() => fixture.Service.MeasureLoadedReferences(root, "test", "1", fixture.Checksum, prompt, fixture.Text, [load with { Content = "tampered" }]));
            var loaded = prompt with { Components = components.Select(c => c.Id == PromptComponentId.SkillReferences ? c with { IsLoaded = true } : c).ToArray() };
            Assert.Throws<ArgumentException>(() => (loaded with { SchemaVersion = 1 }).Validate());
            var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/prompt-manifest.schema.json"));
            var json = JsonSerializer.SerializeToNode(loaded, InfrastructureJson.Options)!;
            Assert.True(schema.Evaluate(json).IsValid);
            json.Deserialize<PromptManifest>(InfrastructureJson.Options)!.Validate();
            json["schemaVersion"] = 1;
            Assert.False(schema.Evaluate(json).IsValid);
            Assert.Equal("unavailable", fixture.Service.Measure("test", "1", fixture.Checksum, loaded, fixture.Text).MeasurementKind);
            fixture.Text["alpha/3.0.0/one.md"] = "tampered";
            Assert.Equal("reference-hash-mismatch", fixture.Service.Measure("test", "1", fixture.Checksum, loaded, fixture.Text).UnavailableReason);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(-1, 3, -0.5)]
    [InlineData(3, -1, 1.5)]
    public void PreservesSignedPrefixDeltasAndRejectsBrokenReconciliation(long skillTokens, long overhead, double share)
    {
        using var fixture = new Fixture("S", "R", "M", "I", "Q", "O");
        var result = new SkillCostMeasurementService(new ExactCounter(2, skillTokens)).Measure("test", "1", fixture.Checksum, fixture.Prompt, fixture.Text);
        Assert.Equal(skillTokens, result.SkillTokens);
        Assert.Equal(overhead, result.TemplateOverheadTokens);
        Assert.Equal(share, result.TokenShare);
        AssertArtifact(result);
        Assert.Throws<ArgumentException>(() => (result with { SkillTokens = 0, TemplateOverheadTokens = 2, TokenShare = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (result with { Components = result.Components.Select(c => c with { Tokens = null, OmissionReason = null }).ToArray() }).Validate());
    }

    private static void AssertArtifact(SkillCostMeasurement result)
    {
        result.Validate();
        var options = new EvaluationOptions();
        options.SchemaRegistry.Register(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/rendered-input-token-measurement.schema.json")));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-cost-measurement.schema.json"));
        var json = JsonSerializer.SerializeToNode(result, InfrastructureJson.Options)!;
        Assert.True(schema.Evaluate(json, options).IsValid, json.ToJsonString());
        json.Deserialize<SkillCostMeasurement>(InfrastructureJson.Options)!.Validate();
    }

    [Fact]
    public void ProfileSelectionMeasuresPinnedSavingsAndRejectsCanonicalPolicyChanges()
    {
        using var fixture = new Fixture("system", "role", "metadata", "unused", "request", "output");
        const string canonicalBody = "Canonical instructions with preserved facts.\n";
        const string profileBody = "Concise.\n";
        var qualification = new SkillQualification("synthetic", "revision-1", "coder", "3.0.0", true,
            canonicalBody.Length - profileBody.Length, fixture.Tokenizer.Id, fixture.Tokenizer.Revision,
            fixture.Template.Id, fixture.Template.Revision, ["no-secrets"], ["read-file"], ["preserve-errors"]);
        var profile = new SkillProfile("synthetic", "revision-1", "coder", "3.0.0", profileBody,
            qualification.Safety, qualification.RequiredTools, qualification.RequiredFacts, qualification);
        var entry = new SkillCompatibilityEntry("prepare-commit", "plugins/sdeveng/skills/prepare-commit/SKILL.md",
            "prepare-commit", "plugins/sdeveng/skills/prepare-commit/SKILL.md", "3.0.0", ["alias"], "retained");
        Directory.CreateDirectory(Path.Combine(fixture.Root, "config"));
        File.WriteAllText(Path.Combine(fixture.Root, "config/skill-compatibility-map.json"),
            JsonSerializer.Serialize(new SkillCompatibilityMap("../schemas/skill-compatibility-map.schema.json", 1, [entry]), InfrastructureJson.Options));
        var path = Path.Combine(fixture.Root, entry.CanonicalPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        void Write(SkillProfile variant) => File.WriteAllText(path,
            "---\nname: prepare-commit\nid: prepare-commit\nversion: 3.0.0\nsafety: '[\"no-secrets\"]'\nrequiredTools: '[\"read-file\"]'\nrequiredFacts: '[\"preserve-errors\"]'\nprofiles: '" +
            JsonSerializer.Serialize(new[] { variant }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "'\n---\n" + canonicalBody);
        Write(profile);
        var prompt = fixture.Prompt with
        {
            Components = fixture.Prompt.Components.Select(c => c.Id == PromptComponentId.SkillInstructions
            ? c with { ContentReference = "prepare-commit/3.0.0/instructions" } : c).ToArray()
        };
        var service = new SkillProfileSelectionService(fixture.Counter);
        SkillProfileSelection Select(SkillQualification? evidence, ChatTemplateManifest? template = null) =>
            service.Select(fixture.Root, "alias", "synthetic", "revision-1", "coder", evidence, template ?? fixture.Template, prompt, fixture.Text);
        var result = Select(qualification);
        Assert.Equal(profileBody, result.Body);
        Assert.Equal(qualification.TokensSaved, result.TokensSaved);
        Assert.True(result.CanonicalMeasurement!.FixtureOnly);
        Assert.Equal(result.CanonicalMeasurement.TemplateChecksum, result.ProfileMeasurement!.TemplateChecksum);
        AssertSelectionArtifact(result);
        Assert.Throws<ArgumentException>(() => (result with { TokensSaved = result.TokensSaved + 1 }).Validate());
        foreach (var evidence in new SkillQualification?[] { null, qualification with { Passed = false },
            qualification with { ModelRevision = "old" }, qualification with { SkillVersion = "2.0.0" },
            qualification with { TokenizerRevision = "old" }, qualification with { TemplateRevision = "old" } })
        {
            var fallback = Select(evidence);
            Assert.Equal(canonicalBody, fallback.Body);
            Assert.Null(fallback.TokensSaved);
            AssertSelectionArtifact(fallback);
        }
        var staleTemplate = fixture.Template with { Revision = "old" };
        Assert.Equal(canonicalBody, Select(qualification, staleTemplate with { Checksum = staleTemplate.ComputeChecksum() }).Body);
        var unavailableTemplate = fixture.Template with { MessageStart = "<unregistered>" };
        var unavailable = Select(qualification, unavailableTemplate with { Checksum = unavailableTemplate.ComputeChecksum() });
        Assert.Equal(canonicalBody, unavailable.Body);
        Assert.Null(unavailable.TokensSaved);
        AssertSelectionArtifact(unavailable);
        Write(profile with { Qualification = qualification with { TokensSaved = qualification.TokensSaved + 1 } });
        Assert.Throws<ArgumentException>(() => Select(qualification with { TokensSaved = qualification.TokensSaved + 1 }));
        foreach (var changed in new[] {
            profile with { Safety = [], Qualification = qualification with { Safety = [] } },
            profile with { RequiredTools = ["write-file"], Qualification = qualification with { RequiredTools = ["write-file"] } },
            profile with { RequiredFacts = [], Qualification = qualification with { RequiredFacts = [] } } })
        {
            Write(changed);
            Assert.Throws<ArgumentException>(() => Select(changed.Qualification));
        }
        // The normal reader also rejects a self-consistent qualification that changes canonical policy.
        Assert.Throws<ArgumentException>(() => SkillCompatibilityMapReader.ReadSelectedSkill(fixture.Root, "alias", "synthetic", "revision-1", "coder",
            (profile with { RequiredFacts = [], Qualification = qualification with { RequiredFacts = [] } }).Qualification));
    }

    private static void AssertSelectionArtifact(SkillProfileSelection result)
    {
        result.Validate();
        var options = new EvaluationOptions();
        foreach (var name in new[] { "skill-metadata", "rendered-input-token-measurement" })
            options.SchemaRegistry.Register(JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), $"schemas/{name}.schema.json")));
        var schema = JsonSchema.FromFile(Path.Combine(AgentTool.FindToolkit(), "schemas/skill-profile-selection.schema.json"));
        var json = JsonSerializer.SerializeToNode(result, InfrastructureJson.Options)!;
        Assert.True(schema.Evaluate(json, options).IsValid, json.ToJsonString());
        json.Deserialize<SkillProfileSelection>(InfrastructureJson.Options)!.Validate();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"skill-cost-fixture-{Guid.NewGuid():N}");
        public string Checksum { get; }
        public string Root => _root;
        public TokenizerManifest Tokenizer { get; }
        public ChatTemplateManifest Template { get; }
        public Dictionary<string, string> Text { get; }
        public PromptManifest Prompt { get; }
        public SkillCostMeasurementService Service { get; }
        public IRenderedInputTokenCounter Counter { get; }
        public Fixture(string system, string role, string metadata, string instruction, string request, string output)
        {
            Text = new() { ["system"] = system, ["role"] = role, ["alpha/v1/metadata"] = metadata, ["alpha/v1/instructions"] = instruction, ["request"] = request, ["output"] = output };
            Prompt = new(1,
            [
                new(PromptComponentId.System, "system", "test", "system", "hash"),
                new(PromptComponentId.Role, "role", "test", "role", "hash"),
                new(PromptComponentId.SkillMetadata, "coder", "test", "alpha/v1/metadata", "hash"),
                new(PromptComponentId.SkillInstructions, "coder", "test", "alpha/v1/instructions", "hash"),
                new(PromptComponentId.Request, "coder", "test", "request", "hash"),
                new(PromptComponentId.OutputContract, "coder", "test", "output", "hash", ResponseSchema: "schema:v1")
            ]);
            Directory.CreateDirectory(_root);
            var asset = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(Path.Combine(_root, "vocab.bin"), asset);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(asset)).ToLowerInvariant();
            var manifest = new TokenizerManifest(1, "byte-test", "fixture-model", TokenizerAssetFamily.Fixture, "r1", [new("vocab.bin", hash)], "fixture", ["<s>", "</s>", "|", "<gen>"], TokenizerAdapterId.Fixture, true);
            Tokenizer = manifest;
            var tokenizers = new TokenizerRegistry(_root);
            var tokenizerPath = Path.Combine(_root, "tokenizer.json");
            File.WriteAllText(tokenizerPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            tokenizers.RegisterFile(tokenizerPath);
            var templates = new ChatTemplateRegistry(tokenizers);
            var template = new ChatTemplateManifest(1, "test", "1", new string('0', 64), "byte-test", "r1", ChatTemplateFamily.Fixture, "<s>", "|", "</s>", "\n", "<tools>", "</tools>", "<gen>", true);
            template = template with { Checksum = template.ComputeChecksum() };
            Template = template;
            Checksum = template.Checksum;
            templates.Register(template);
            Counter = new RenderedInputTokenCounter(templates);
            Service = new(Counter);
        }
        public void Dispose() => Directory.Delete(_root, true);
    }

    // A schema-valid injected counter exercises totals that this framed byte fixture cannot produce.
    private sealed class ExactCounter(long total, long metadataDelta) : IRenderedInputTokenCounter
    {
        public RenderedInputTokenMeasurement Count(string templateId, string templateRevision, string templateChecksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference, RenderedInputTokenPolicy? policy = null)
            => throw new NotImplementedException();
        public RenderedInputTokenMeasurement CountAttributed(string templateId, string templateRevision, string templateChecksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference)
        {
            long prefix = metadataDelta < 0 ? -metadataDelta : 0;
            var deltas = new List<ComponentPrefixDelta> { new(new(RenderedComponentSpan.Overhead, "header", 0, 0), prefix, prefix) };
            foreach (var component in prompt.Components)
            {
                var delta = component.Id == PromptComponentId.SkillMetadata ? metadataDelta : 0;
                prefix += delta;
                deltas.Add(new(new(PromptComponentIdJsonConverter.ToWireValue(component.Id), component.ContentReference, 0, 0), prefix, delta));
            }
            deltas.Add(new(new(RenderedComponentSpan.Overhead, "footer", 0, 0), total, total - prefix));
            return new(1, RenderedInputTokenMeasurement.ResultKind, "exact", total, "fixture-byte-v1", templateId, templateRevision, templateChecksum,
                "fixture", "v1", [new("vocab.bin", new string('0', 64))], true, new string('0', 64), 0, null)
            { Attribution = new(1, OrderedComponentAttribution.AlgorithmVersion, 0, 0, total, total, true, deltas) };
        }
    }

    private sealed class UnavailableCounter : IRenderedInputTokenCounter
    {
        public RenderedInputTokenMeasurement Count(string templateId, string templateRevision, string templateChecksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference, RenderedInputTokenPolicy? policy = null)
            => throw new NotImplementedException();
        public RenderedInputTokenMeasurement CountAttributed(string templateId, string templateRevision, string templateChecksum, PromptManifest prompt, IReadOnlyDictionary<string, string> textByContentReference)
            => new(1, RenderedInputTokenMeasurement.ResultKind, "unavailable", null, null, templateId, templateRevision, templateChecksum, null, null, [], false, null, null, "counter-unavailable");
    }
}
