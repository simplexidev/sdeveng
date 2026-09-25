#!/usr/bin/env dotnet
#:property TargetFramework=net10.0
#:property Nullable=enable
#:property ImplicitUsings=enable
#:property PublishAot=false

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SdevEng;

public static class AgentTool
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public const string Help = """
        sdeveng — SimplexiDev Engineering Toolkit deterministic → JEV → Codex
        dotnet tools/AgentTool.cs -- <command> [options]

        install | update [--home DIR] [--codex-home DIR] [--dry-run] [--bin]
        uninstall [--home DIR] [--codex-home DIR] [--dry-run]
        doctor
        repo changed-files [--base REF] | summary [--base REF] | locate --query TEXT | health | hygiene
        repo affected-projects [--base REF] | ownership --file PATH
        git state | summary [--base REF] | conflict-forecast --base REF | prepare-commit | issue-start --issue NUMBER --branch NAME
        github pr-status | review-comments --pr NUMBER | prepare-pr
        github actions [--run-id NUMBER] [--failed-logs]
        dotnet inspect [--project PATH] | build-plan [--base REF] [--project PATH] [--configuration NAME] [--binlog]
        dotnet test-plan [--base REF] [--project PATH] [--configuration NAME]
            [--test NAME | --class NAME | --category NAME | --filter EXPR]
        dotnet diagnostics-plan [--process-id NUMBER] [--signal counters|cpu|contention|allocations|managed-memory|crash|hang]
            [--duration-seconds NUMBER]
        dotnet verify [--base REF] [--project PATH] | format --project PATH [--apply]
        dotnet dependencies --project PATH | package-audit --project PATH | api-check --project PATH | release-verify --project PATH
        logs summarize --file PATH | sarif summarize --file PATH [--baseline PATH]
        artifact inspect --file PATH | verify --file PATH --sha256 HEX
        test-results summarize --file PATH | coverage summarize --file PATH
        jev noul|choice|score --input PATH [--dry-run] [--safe-input]
        jev screen --input PATH [--dry-run] [--safe-input] | cache-clear
        upstream status | update [--dry-run] | dotnet-skills <status|diff|check> [--dry-run]
        validate | eval [--skill NAME] [--results PATH] | release --output ZIP
        results init | new <audit|handoff|review|report> <name>
        results list [audit|handoff|review|report] [--json] | latest <type> [--json]
        results context <type> [--json] | clean [--dry-run]

        Common: --root DIR (target repository), --toolkit DIR, --json, --help
        JEV input: {"capability":"configured-id","purpose":"allowed-purpose","deterministicNarrowed":true,"state":"sanitized excerpt","instructions":"bounded question","criteria":...}
        Screen input: same routing metadata plus {"query":"question","candidates":[{"id":"path","text":"safe excerpt"}]}
        JEV defaults to auto; missing/invalid/uncertain answers return REVIEW for Codex.
        No command merges PRs, commits, pushes, installs external tools, or pulls Git updates.
        """;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var c = Cli.Parse(args);
            if (c.Flag("help") || c.Words.Count == 0 || c.Words[0] == "help") { Console.WriteLine(Help); return 0; }
            var toolkit = FindToolkit(c.Get("toolkit"));
            var root = Path.GetFullPath(c.Get("root") ?? Environment.CurrentDirectory);
            var command = c.Words.FirstOrDefault() is "results" ? string.Join(' ', c.Words.Take(2)) : c.Words.FirstOrDefault() == "upstream" && c.Words.ElementAtOrDefault(1) == "dotnet-skills" ? string.Join(' ', c.Words.Take(2)) : string.Join(' ', c.Words);
            var settings = Settings.LoadFor(toolkit, command);
            var result = await Execute(c, toolkit, root, settings);
            Console.WriteLine(Render(result, root, settings.Output));
            return result.ExitCode;
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or JsonException or FormatException or System.Xml.XmlException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "error", message = Secrets.Redact(e.Message) }, Json));
            return 2;
        }
    }

    public static string Render(Result result, string root, OutputSettings output)
    {
        var rendered = Secrets.RedactJson(JsonSerializer.Serialize(result, Json));
        if (rendered.Length <= output.MaxOutputChars) return rendered;
        var report = Path.Combine(root, ".agent-tool", $"result-{Guid.NewGuid():N}.json");
        SafeFiles.Atomic(report, rendered);
        return Secrets.RedactJson(JsonSerializer.Serialize(new { result.Status, result.ExitCode, truncated = true, characters = rendered.Length, artifact = report }, Json));
    }

    public static string FindToolkit(string? explicitRoot = null, [System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var path = explicitRoot ?? Environment.GetEnvironmentVariable("SDEVENG_ROOT") ?? Environment.GetEnvironmentVariable("CODEX_TOOLKIT_ROOT");
        if (path is null)
        {
            if (File.Exists(source)) source = File.ResolveLinkTarget(source, true)?.FullName ?? source;
            for (var parent = Path.GetDirectoryName(source); parent is not null; parent = Path.GetDirectoryName(parent))
                if (File.Exists(Path.Combine(parent, "config", "toolkit.json"))) { path = parent; break; }
            for (var parent = Environment.CurrentDirectory; path is null && parent is not null; parent = Directory.GetParent(parent)?.FullName)
                if (File.Exists(Path.Combine(parent, "config", "toolkit.json"))) { path = parent; break; }
        }
        path ??= Environment.CurrentDirectory;
        path = Path.GetFullPath(path);
        if (!File.Exists(Path.Combine(path, "config", "toolkit.json"))) throw new ArgumentException("Toolkit root not found; pass --toolkit DIR, SDEVENG_ROOT, or legacy CODEX_TOOLKIT_ROOT.");
        return path;
    }

    public static async Task<Result> Execute(Cli c, string toolkit, string root, Settings settings)
    {
        var command = c.Words.FirstOrDefault() is "results" ? string.Join(' ', c.Words.Take(2)) : c.Words.FirstOrDefault() == "upstream" && c.Words.ElementAtOrDefault(1) == "dotnet-skills" ? string.Join(' ', c.Words.Take(2)) : string.Join(' ', c.Words);
        c.ValidateCommand(command);
        var artifacts = Path.Combine(root, ".agent-tool");
        switch (command)
        {
            case "install":
            case "update":
            case "uninstall":
                return Installer.Run(toolkit, c.Get("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), c.Get("codex-home") ?? (c.Get("home") is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null), command, c.Flag("dry-run"), c.Flag("bin"));
            case "doctor": return await Doctor(toolkit, settings, c);
            case "git state": return Result.Ok(await Git.State(root));
            case "git summary": return Result.Ok(await Repository.Summary(root, c.Get("base"), settings.Output));
            case "git conflict-forecast": return Result.Ok(await Git.ConflictForecast(root, c.Require("base"), settings.Output));
            case "git prepare-commit":
            case "github prepare-pr":
                await Git.EnsureSafe(root, false);
                var diff = await Processes.Run("git", ["diff", "--check"], root);
                var staged = await Processes.Run("git", ["diff", "--cached", "--check"], root);
                return new(diff.ExitCode != 0 || staged.ExitCode != 0 ? "failed" : "ok", new { state = await Git.State(root), whitespace = Output.Compact(diff.Output + staged.Output, settings.Output), next = "Review explicit file scope before staging. Commit/push/PR creation remains caller-controlled; never merge without approval." }, diff.ExitCode != 0 || staged.ExitCode != 0 ? 1 : 0);
            case "git issue-start":
                await Git.EnsureSafe(root, true);
                var issue = c.PositiveInt("issue"); var branch = c.Require("branch");
                await Git.Require(root, "check-ref-format", "--branch", branch);
                var info = await Processes.Run("gh", ["issue", "view", issue.ToString(CultureInfo.InvariantCulture), "--json", "state"], root);
                if (info.ExitCode != 0 || JsonNode.Parse(info.Output)?["state"]?.GetValue<string>() != "OPEN") throw new InvalidOperationException("Issue is unavailable or not open; no branch created.");
                await Git.Require(root, "switch", "-c", branch);
                return Result.Ok(new { branch, issue });
            case "repo changed-files": return Result.Ok(await Git.Changed(root, c.Get("base")));
            case "repo summary": return Result.Ok(await Repository.Summary(root, c.Get("base"), settings.Output));
            case "repo locate":
                var files = (await Git.Files(root)).Where(x => !SafeFiles.IsDiscoveryExcluded(x)).ToArray();
                return Result.Ok(new { matches = files.Where(x => x.Contains(c.Require("query"), StringComparison.OrdinalIgnoreCase)).Take(settings.Output.MaxItems), total = files.Count(x => x.Contains(c.Require("query"), StringComparison.OrdinalIgnoreCase)), scope = "Git tracked + untracked, nonignored path names excluding the managed result store; use rg for symbols." });
            case "repo affected-projects": return Result.Ok(await Projects.Affected(root, await Git.Changed(root, c.Get("base"))));
            case "repo ownership": return Result.Ok(await Projects.Ownership(root, c.Require("file")));
            case "repo health":
                var health = await Projects.Health(root, settings.Health);
                return new(health.Count == 0 ? "ok" : "findings", health, health.Count == 0 ? 0 : 1);
            case "repo hygiene": return Result.Ok(await Repository.Hygiene(root, settings.Output));
            case "github pr-status": return await RunArtifact("gh", ["pr", "status", "--json", "headRefName,author,reviewDecision,statusCheckRollup"], root, artifacts, settings.Output);
            case "github review-comments":
                var pr = c.PositiveInt("pr").ToString(CultureInfo.InvariantCulture);
                return await RunArtifact("gh", ["api", $"repos/{{owner}}/{{repo}}/pulls/{pr}/comments", "--paginate"], root, artifacts, settings.Output);
            case "github actions": return await GitHub.Actions(root, artifacts, c.Get("run-id"), c.Flag("failed-logs"), settings.Output);
            case "dotnet verify":
            case "dotnet format":
            case "dotnet package-audit":
            case "dotnet dependencies":
            case "dotnet api-check":
            case "dotnet release-verify":
                return await Dotnet(command, c, root, artifacts, settings);
            case "dotnet inspect": return Result.Ok(await DotnetFacts.Inspect(root, c.Get("project")));
            case "dotnet build-plan": return Result.Ok(await DotnetFacts.BuildPlan(root, c.Get("project"), c.Get("base"), c.Get("configuration") ?? "Debug", c.Flag("binlog")));
            case "dotnet test-plan": return Result.Ok(await DotnetFacts.TestPlan(root, c.Get("project"), c.Get("base"), c.Get("configuration") ?? "Debug", new(c.Get("test"), c.Get("class"), c.Get("category"), c.Get("filter"))));
            case "dotnet diagnostics-plan": return Result.Ok(await DotnetFacts.DiagnosticsPlan(c.Get("process-id"), c.Get("signal"), c.Get("duration-seconds"), root));
            case "logs summarize":
                return Result.Ok(Output.SummarizeFile(c.Require("file"), settings.Output));
            case "sarif summarize": return Result.Ok(Output.Sarif(c.Require("file"), settings.Output, c.Get("baseline")));
            case "artifact inspect": return Result.Ok(Artifacts.Inspect(c.Require("file"), settings.Output));
            case "artifact verify": return Artifacts.Verify(c.Require("file"), c.Require("sha256"));
            case "test-results summarize": return Result.Ok(DotnetArtifacts.TestResults(c.Require("file"), settings.Output));
            case "coverage summarize": return Result.Ok(DotnetArtifacts.Coverage(c.Require("file"), settings.Output));
            case "jev noul":
            case "jev choice":
            case "jev score":
            case "jev screen":
                return await JevCommand(c, command[4..], root, settings.Jev);
            case "jev cache-clear":
                var cachePath = Path.Combine(artifacts, "jev-cache");
                SafeFiles.NoLinks(cachePath);
                if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);
                return Result.Ok(new { cleared = cachePath });
            case "upstream status": return Result.Ok(new { plugins = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/dotnet-skills.json"))), tools = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/tools.json"))), versions = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/versions.json"))) });
            case "upstream update": return await Upstream(toolkit, artifacts, c.Flag("dry-run"));
            case "upstream dotnet-skills": return await DotnetSkillsDrift.Run(toolkit, artifacts, c.Words.Skip(2).SingleOrDefault(), c.Flag("dry-run"));
            case "validate": return Validation.Run(toolkit);
            case "eval": return Evaluation.Run(toolkit, c.Get("skill"), c.Get("results"));
            case "release": return Release(toolkit, c.Require("output"));
            case "results init": Results.RequireWords(c.Words.Skip(2).ToArray(), 0, "Usage: results init."); return Results.Init(root);
            case "results new": return await Results.New(root, c.Words.Skip(2).ToArray());
            case "results list": return Results.List(root, c.Words.Skip(2).ToArray());
            case "results latest": return Results.Latest(root, c.Words.Skip(2).ToArray());
            case "results context": return Results.Context(root, c.Words.Skip(2).ToArray());
            case "results clean": Results.RequireWords(c.Words.Skip(2).ToArray(), 0, "Usage: results clean [--dry-run]."); return Results.Clean(root, c.Flag("dry-run"));
            default: throw new ArgumentException("Unknown command. Use --help.");
        }
    }

    static async Task<Result> Doctor(string toolkit, Settings settings, Cli c)
    {
        var checks = new List<object>(); bool requiredOk = true;
        foreach (var (tool, args, required) in new[] { ("dotnet", new[] { "--version" }, true), ("git", new[] { "--version" }, true), ("gh", new[] { "--version" }, false), ("codex", new[] { "--version" }, false) })
        {
            try
            {
                var r = await Processes.Run(tool, args, toolkit);
                var ok = r.ExitCode == 0 && (tool != "dotnet" || Version.TryParse(r.Output.Trim().Split('-')[0], out var v) && v.Major >= 10);
                checks.Add(new { tool, required, available = ok, summary = Output.Compact(r.Output, settings.Output) });
                if (required && !ok) requiredOk = false;
            }
            catch (System.ComponentModel.Win32Exception) { checks.Add(new { tool, required, available = false }); if (required) requiredOk = false; }
        }
        var home = c.Get("home") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var codex = c.Get("codex-home") ?? (c.Get("home") is null ? Environment.GetEnvironmentVariable("CODEX_HOME") : null) ?? Path.Combine(home, ".codex");
        return new(requiredOk ? "ok" : "failed", new { checks, toolkit, codex, skills = Path.Combine(home, ".agents/skills"), installation = Installer.Inspect(codex), jev = new { settings.Jev.Mode, credentials = JevCredentials.Status(), settings.Jev.Model }, upstream = "Run upstream status for integration policy; listed integrations are not automatically installed.", optionalTools = settings.Toolkit.OptionalTools.Select(t => new { name = t, available = Processes.OnPath(t) }) }, requiredOk ? 0 : 1);
    }

    static async Task<Result> Dotnet(string command, Cli c, string root, string artifacts, Settings settings)
    {
        var explicitProject = c.Get("project");
        if (command != "dotnet verify" && explicitProject is null) throw new ArgumentException("This command requires --project PATH (project or solution).");
        var targets = explicitProject is not null ? new[] { Path.GetFullPath(explicitProject, root) } : (await Projects.Affected(root, await Git.Changed(root, c.Get("base")))).Projects.ToArray();
        if (targets.Any(x => !File.Exists(x))) throw new ArgumentException("Project or solution does not exist.");
        var results = new List<Result>();
        foreach (var project in targets)
        {
            if (command == "dotnet format")
            {
                var changed = await Git.Changed(root, c.Get("base"));
                var code = changed.Where(x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(root, x))).Select(x => Path.Combine(root, x)).ToArray();
                if (code.Length == 0) continue;
                var args = new List<string> { "format", project, "--include" }; args.AddRange(code);
                if (!c.Flag("apply")) args.Add("--verify-no-changes");
                results.Add(await RunArtifact("dotnet", args, root, artifacts, settings.Output));
            }
            else if (command == "dotnet dependencies")
            {
                var r = await RunArtifact("dotnet", ["package", "list", "--project", project, "--include-transitive", "--format", "json", "--output-version", "1"], root, artifacts, settings.Output);
                if (r.ExitCode == 0 && r.Data is ProcessReport p) r = Result.Ok(DotnetArtifacts.Dependencies(p.Artifact, root, settings.Output));
                results.Add(r);
            }
            else if (command == "dotnet package-audit")
            {
                var r = await RunArtifact("dotnet", ["package", "list", "--project", project, "--vulnerable", "--include-transitive", "--format", "json"], root, artifacts, settings.Output);
                if (r.ExitCode == 0 && r.Data is ProcessReport p)
                {
                    var report = JsonNode.Parse(File.ReadAllText(p.Artifact));
                    var vulnerabilities = Audit.Count(report);
                    r = new(vulnerabilities > 0 ? "vulnerable" : "ok", new { vulnerabilities, p.Artifact }, vulnerabilities > 0 ? 1 : 0);
                }
                results.Add(r);
            }
            else if (command == "dotnet api-check")
            {
                if (!await Projects.HasApiChecks(root, project)) throw new InvalidOperationException("Configure PublicApiAnalyzers or EnablePackageValidation with a baseline first; api-check cannot certify an unconfigured project.");
                results.Add(await RunArtifact("dotnet", ["pack", project, "-p:EnablePackageValidation=true", "-p:TreatWarningsAsErrors=true"], root, artifacts, settings.Output));
            }
            else
            {
                var steps = new List<string[]> { new[] { "build", project, "--nologo" } };
                if (command == "dotnet release-verify") steps.Insert(0, ["restore", project, "-p:NuGetAudit=true", "-p:NuGetAuditMode=all"]);
                if (command == "dotnet release-verify") steps.Add(["format", project, "--verify-no-changes"]);
                var isSolution = project.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || project.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
                if (isSolution || await Projects.IsTest(root, project)) steps.Add(["test", project, "--no-build", "--nologo"]);
                else if (command == "dotnet release-verify")
                {
                    var tests = await Projects.DependentTests(root, project);
                    if (tests.Length == 0) throw new InvalidOperationException("Release verification cannot establish test coverage for this project. Pass a solution or add a dependent test project.");
                    steps.AddRange(tests.Select(test => new[] { "test", test, "--nologo" }));
                }
                foreach (var step in steps)
                {
                    var r = await RunArtifact("dotnet", step, root, artifacts, settings.Output); results.Add(r);
                    if (r.ExitCode != 0) break;
                }
                if (command == "dotnet release-verify" && results.All(r => r.ExitCode == 0))
                    results.Add(await Dotnet("dotnet package-audit", c, root, artifacts, settings));
            }
        }
        return new(results.Any(x => x.ExitCode != 0) ? "failed" : "ok", new { targets, results, scope = command == "dotnet release-verify" ? "restore, build, format, established test coverage, vulnerability audit; API/SBOM/reproducibility gates are separate on-demand skills" : "targeted" }, results.Any(x => x.ExitCode != 0) ? 1 : 0);
    }

    public static async Task<Result> RunArtifact(string executable, IEnumerable<string> args, string root, string artifacts, OutputSettings limits)
    {
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var path = Path.Combine(artifacts, $"{executable}-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}.log");
        var r = await Processes.Run(executable, args, root, path, TimeSpan.FromMinutes(20));
        return new(r.ExitCode == 0 ? "ok" : "failed", new ProcessReport(r.ExitCode, Output.SummarizeFile(path, limits), path), r.ExitCode == 0 ? 0 : 1);
    }

    static async Task<Result> JevCommand(Cli c, string kind, string root, JevSettings settings)
    {
        var inputPath = c.Require("input");
        if (Path.GetFileName(inputPath).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return Result.Review("Sensitive input path refused.");
        if (new FileInfo(inputPath).Length > settings.MaxInputBytes) return Result.Review("Input exceeds configured limit.");
        var input = JsonNode.Parse(File.ReadAllText(inputPath)) as JsonObject;
        if (input is null)
        {
            if (kind == "screen") return Result.Review("Screen input must be an object; no candidate discarded.");
            throw new ArgumentException("Input object required.");
        }
        var capability = input["capability"] is JsonValue capabilityValue && capabilityValue.TryGetValue<string>(out var capabilityText) ? capabilityText : "";
        var purpose = input["purpose"] is JsonValue purposeValue && purposeValue.TryGetValue<string>(out var purposeText) ? purposeText : "";
        settings.Capabilities.TryGetValue(capability, out var policy);
        if (policy is null) return JevClient.PolicyReview(null, capability, purpose, "A configured JEV capability is required.");
        if (!policy.Purposes.Contains(purpose, StringComparer.Ordinal)) return JevClient.PolicyReview(policy, capability, purpose, "Purpose is not allowed for this JEV capability.");
        if (!policy.Allowed) return JevClient.PolicyReview(policy, capability, purpose, "JEV is disallowed for this capability; use deterministic tooling or GPT reasoning.");
        var deterministicallyNarrowed = input["deterministicNarrowed"] is JsonValue narrowedValue && narrowedValue.TryGetValue<bool>(out var narrowed) && narrowed;
        if (policy.DeterministicFirst && !deterministicallyNarrowed) return JevClient.PolicyReview(policy, capability, purpose, "Deterministic narrowing is required before JEV.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds) };
        var client = new JevClient(http, settings, Path.Combine(root, ".agent-tool/jev-cache"));
        if (kind == "screen")
        {
            if (input["candidates"] is not JsonArray candidates) return JevClient.PolicyReview(policy, capability, purpose, "Screen candidates array is required; no candidate discarded.");
            var candidateLimit = Math.Min(settings.MaxCandidates, Math.Min(policy.MaxCandidates, policy.MaxCalls));
            if (candidates.Count > candidateLimit) return JevClient.PolicyReview(policy, capability, purpose, "Too many candidates for the capability call budget; narrow deterministic search first.");
            var query = input["query"] is JsonValue queryValue && queryValue.TryGetValue<string>(out var queryText) ? queryText : null;
            if (string.IsNullOrWhiteSpace(query)) return JevClient.PolicyReview(policy, capability, purpose, "Screen query is required; no candidate discarded.");
            var prepared = new List<(string Id, string Text)>();
            foreach (var candidate in candidates)
            {
                if (candidate is not JsonObject item || item["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || item["text"] is not JsonValue textValue || !textValue.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
                    return JevClient.PolicyReview(policy, capability, purpose, "Every screen candidate requires a non-empty string id and text; no candidate discarded.");
                if (Secrets.LooksSensitive(id)) return JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected in candidate id; no candidate discarded.");
                prepared.Add((id, text));
            }
            if (prepared.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != prepared.Count)
                return JevClient.PolicyReview(policy, capability, purpose, "Screen candidate ids must be unique; no candidate discarded.");
            var answers = new List<object>(); int exitCode = 0, remoteCalls = 0, cacheHits = 0, fallbacks = 0, escalations = 0, uncertain = 0, contextAvoidedBytes = 0;
            foreach (var candidate in prepared)
            {
                var request = JevClient.Request("noul", candidate.Text, $"Is this candidate relevant to: {query}", null, settings.Model);
                var judgment = Secrets.LooksSensitive(request.ToJsonString()) ? JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected; request refused.") : c.Flag("dry-run") ? Result.Ok(request) : !c.Flag("safe-input") ? JevClient.PolicyReview(policy, capability, purpose, "Use --safe-input only after minimizing and reviewing supplied text for external transmission.") : await client.Judge(request, policy, purpose, capability);
                exitCode = Math.Max(exitCode, judgment.ExitCode);
                answers.Add(new { id = candidate.Id, judgment });
                if (judgment.Status == "EXCLUDE") contextAvoidedBytes += Encoding.UTF8.GetByteCount(candidate.Text);
                if (judgment.Status == "REVIEW") uncertain++;
                var telemetry = JsonSerializer.SerializeToNode(judgment.Data, Json)?["instrumentation"];
                remoteCalls += telemetry?["counts"]?["remoteCalls"]?.GetValue<int>() ?? 0;
                cacheHits += telemetry?["counts"]?["cacheHits"]?.GetValue<int>() ?? 0;
                fallbacks += telemetry?["counts"]?["fallbacks"]?.GetValue<int>() ?? 0;
                escalations += telemetry?["counts"]?["escalations"]?.GetValue<int>() ?? 0;
            }
            return new(exitCode == 0 ? "ok" : "REVIEW", new
            {
                judgments = answers,
                instrumentation = new
                {
                    schemaVersion = 1,
                    capability,
                    purpose,
                    privacy = policy.Privacy,
                    budget = new { expectedCalls = policy.ExpectedCalls, maxCalls = policy.MaxCalls },
                    bounds = new { policy.DeterministicFirst, policy.MaxInputBytes, policy.MaxCandidates },
                    counts = new { candidates = prepared.Count, judgments = c.Flag("dry-run") ? 0 : prepared.Count, remoteCalls, cacheHits, fallbacks, escalations },
                    confidence = new { reported = (double?)null, minimum = policy.MinConfidence, uncertain },
                    fallback = new { used = fallbacks > 0, target = fallbacks > 0 ? "GPT" : null },
                    escalation = new { required = escalations > 0, target = escalations > 0 ? policy.GptEscalation + "-gpt" : null },
                    contextAvoidedBytes,
                    payloadCaptured = false
                }
            }, exitCode);
        }
        if (policy.MaxCalls < 1) return JevClient.PolicyReview(policy, capability, purpose, "JEV call budget is zero for this capability.");
        var payload = JevClient.Request(kind, input["state"]?.GetValue<string>() ?? "", input["instructions"]?.GetValue<string>() ?? "", input["criteria"], settings.Model);
        if (c.Flag("dry-run")) return Secrets.LooksSensitive(payload.ToJsonString()) ? JevClient.PolicyReview(policy, capability, purpose, "Potential secret detected; request refused.") : Result.Ok(payload);
        if (!c.Flag("safe-input")) return JevClient.PolicyReview(policy, capability, purpose, "Use --safe-input only after reviewing and minimizing supplied text for external transmission.");
        return await client.Judge(payload, policy, purpose, capability);
    }

    static async Task<Result> Upstream(string toolkit, string artifacts, bool dryRun)
    {
        var versions = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/versions.json")))!["repositories"]!.AsArray();
        var rows = new List<object>();
        foreach (var entry in versions)
        {
            var repo = entry!["repository"]!.GetValue<string>(); var pinned = entry["revision"]?.GetValue<string>();
            if (dryRun) { rows.Add(new { repo, pinned, query = $"gh api repos/{repo}/commits/HEAD --jq .sha" }); continue; }
            var r = await Processes.Run("gh", ["api", $"repos/{repo}/commits/HEAD", "--jq", ".sha"], toolkit);
            rows.Add(new { repo, pinned, latest = r.ExitCode == 0 ? r.Output.Trim() : null, status = r.ExitCode != 0 ? "unavailable" : r.Output.Trim() == pinned ? "current" : "review-update" });
        }
        if (dryRun) return Result.Ok(rows);
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var report = Path.Combine(artifacts, "upstream-drift.json"); SafeFiles.Atomic(report, JsonSerializer.Serialize(rows, Json));
        return Result.Ok(new { rows, report, policy = "Report only; no downloads, manifest edits or merges." });
    }

    static Result Release(string toolkit, string output)
    {
        var valid = Validation.Run(toolkit); if (valid.ExitCode != 0) return valid;
        output = Path.GetFullPath(output); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var zip = System.IO.Compression.ZipFile.Open(output, System.IO.Compression.ZipArchiveMode.Create);
        var roots = new[] { "agents", "config", "docs", "evals", "global", "plugins", "schemas", "templates", "tools", "upstream" };
        foreach (var file in roots.SelectMany(x => SafeFiles.Enumerate(Path.Combine(toolkit, x))).Concat(new[] { "README.md", "CHANGELOG.md", "LICENSE", "NOTICE.md", "THIRD-PARTY-NOTICES.md", "global.json", ".agents/plugins/marketplace.json" }.Select(x => Path.Combine(toolkit, x))))
            System.IO.Compression.ZipFileExtensions.CreateEntryFromFile(zip, file, Path.GetRelativePath(toolkit, file).Replace('\\', '/'));
        return Result.Ok(new { archive = output });
    }
}

public record Result(string Status, object? Data, int ExitCode = 0)
{
    public static Result Ok(object? data) => new("ok", data);
    public static Result Review(string reason) => new("REVIEW", new { reason, fallback = "Codex" });
}
public record ProcessReport(int ProcessExitCode, object Summary, string Artifact);
public record ProcessResult(int ExitCode, string Output);

public static class DotnetSkillsDrift
{
    // This compares only public Git metadata. It deliberately never checks out, runs, or imports upstream files.
    public static async Task<Result> Run(string toolkit, string artifacts, string? operation, bool dryRun)
    {
        if (operation is not ("status" or "diff" or "check")) throw new ArgumentException("Usage: upstream dotnet-skills <status|diff|check> [--dry-run].");
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "upstream/dotnet-skills.json"))) ?? throw new FormatException("Dotnet skills provenance manifest is empty.");
        var snapshot = manifest["snapshot"]?.AsObject() ?? throw new FormatException("Dotnet skills provenance snapshot is missing.");
        var repository = Required(snapshot, "repository"); var pinned = Required(snapshot, "commit");
        if (operation == "status")
        {
            if (dryRun) return Result.Ok(new { kind = "dotnet-skills-status", repository, pinned, query = $"gh api repos/{repository}/commits/HEAD --jq .sha", policy = "Public metadata only; no source is downloaded or executed." });
            var head = await Processes.Run("gh", ["api", $"repos/{repository}/commits/HEAD", "--jq", ".sha"], toolkit);
            return new(head.ExitCode == 0 ? "ok" : "unavailable", new { kind = "dotnet-skills-status", repository, pinned, latest = head.ExitCode == 0 ? head.Output.Trim() : null, status = head.ExitCode == 0 && head.Output.Trim() == pinned ? "current" : "review-update", policy = "Public metadata only; no source is downloaded or executed." }, head.ExitCode == 0 ? 0 : 1);
        }
        if (dryRun) return Result.Ok(new { kind = "dotnet-skills-diff", repository, pinned, query = $"gh api repos/{repository}/compare/{pinned}...HEAD", policy = "Only changed decision paths are reported; no upstream code is executed, merged, or copied." });
        var response = await Processes.Run("gh", ["api", $"repos/{repository}/compare/{pinned}...HEAD"], toolkit);
        if (response.ExitCode != 0) return new("unavailable", new { kind = "dotnet-skills-diff", repository, pinned, policy = "Comparison unavailable; no source was downloaded or executed." }, 1);
        var analysis = Analyze(manifest, JsonNode.Parse(response.Output) ?? throw new FormatException("Upstream comparison is empty."));
        SafeFiles.NoLinks(artifacts); Directory.CreateDirectory(artifacts);
        var report = Path.Combine(artifacts, "dotnet-skills-drift.json"); SafeFiles.Atomic(report, JsonSerializer.Serialize(analysis, AgentTool.Json));
        var review = analysis["classification"]?.GetValue<string>() is "relevant" or "review-required";
        return new(review && operation == "check" ? "review-required" : "ok", new { kind = "dotnet-skills-drift", analysis, report, policy = "Report only. Review listed public paths before any separate, explicit integration change." }, review && operation == "check" ? 1 : 0);
    }

    public static JsonObject Analyze(JsonNode manifest, JsonNode authoritative)
    {
        var snapshot = manifest["snapshot"]?.AsObject() ?? throw new FormatException("Provenance snapshot is missing.");
        var repository = Required(snapshot, "repository"); var pinned = Required(snapshot, "commit");
        var files = authoritative["files"]?.AsArray() ?? throw new FormatException("Authoritative comparison has no files array.");
        var paths = manifest["decisions"]?.AsArray().SelectMany(d => d?["upstreamPaths"]?.AsArray() ?? throw new FormatException("Decision has no upstreamPaths.")).Select(p => p?.GetValue<string>() ?? throw new FormatException("Decision path is invalid.")).ToHashSet(StringComparer.Ordinal) ?? throw new FormatException("Provenance decisions are missing.");
        var relevant = new JsonArray(); var irrelevant = 0;
        foreach (var item in files)
        {
            var file = item?.AsObject() ?? throw new FormatException("Authoritative comparison file is invalid.");
            var path = Required(file, "filename"); var status = Required(file, "status");
            if (status is not ("added" or "modified" or "removed" or "renamed")) throw new FormatException("Authoritative comparison has an unknown file status.");
            var previous = file["previous_filename"]?.GetValue<string>();
            if (!paths.Contains(path) && (previous is null || !paths.Contains(previous))) { irrelevant++; continue; }
            var disposition = status is "removed" or "renamed" ? "review-required" : "relevant";
            relevant.Add(new JsonObject { ["path"] = path, ["status"] = status, ["previousPath"] = previous, ["classification"] = disposition, ["inspect"] = "Fetch this one public path only if a maintainer needs its diff." });
        }
        var classification = relevant.Count == 0 ? (files.Count == 0 ? "no-change" : "irrelevant") : relevant.Any(f => f!["classification"]!.GetValue<string>() == "review-required") ? "review-required" : "relevant";
        return new JsonObject { ["schemaVersion"] = 1, ["kind"] = "dotnet-skills-drift", ["repository"] = repository, ["pinned"] = pinned, ["authoritativeHead"] = authoritative["head_commit"]?["sha"]?.GetValue<string>(), ["classification"] = classification, ["changedFiles"] = files.Count, ["relevant"] = relevant, ["irrelevantCount"] = irrelevant, ["automaticAction"] = "none" };
    }

    static string Required(JsonObject value, string name) => value[name]?.GetValue<string>() is { Length: > 0 } text ? text : throw new FormatException($"Missing {name}.");
}

public sealed class Cli
{
    public void ValidateCommand(string command)
    {
        var allowed = new HashSet<string>(new[] { "root", "toolkit", "json", "help" });
        string[] specific = command switch
        {
            "install" or "update" => ["home", "codex-home", "dry-run", "bin"],
            "uninstall" => ["home", "codex-home", "dry-run"],
            "doctor" => ["home", "codex-home"],
            "repo changed-files" or "repo affected-projects" or "repo summary" or "git summary" or "git conflict-forecast" => ["base"],
            "repo locate" => ["query"],
            "repo ownership" => ["file"],
            "git issue-start" => ["issue", "branch"],
            "github review-comments" => ["pr"],
            "github actions" => ["run-id", "failed-logs"],
            "dotnet verify" => ["base", "project"],
            "dotnet inspect" => ["project"],
            "dotnet build-plan" => ["base", "project", "configuration", "binlog"],
            "dotnet test-plan" => ["base", "project", "configuration", "test", "class", "category", "filter"],
            "dotnet diagnostics-plan" => ["process-id", "signal", "duration-seconds"],
            "dotnet format" => ["base", "project", "apply"],
            "dotnet dependencies" or "dotnet package-audit" or "dotnet api-check" or "dotnet release-verify" => ["project"],
            "logs summarize" or "test-results summarize" or "coverage summarize" or "artifact inspect" => ["file"],
            "sarif summarize" => ["file", "baseline"],
            "artifact verify" => ["file", "sha256"],
            "jev noul" or "jev choice" or "jev score" or "jev screen" => ["input", "dry-run", "safe-input"],
            "upstream update" or "upstream dotnet-skills" => ["dry-run"],
            "eval" => ["skill", "results"],
            "release" => ["output"],
            "results clean" => ["dry-run"],
            _ => []
        };
        allowed.UnionWith(specific);
        foreach (var option in Options.Keys)
            if (!allowed.Contains(option)) throw new ArgumentException($"--{option} is not supported by this command.");
    }
    public List<string> Words { get; } = [];
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);
    static readonly HashSet<string> Flags = ["json", "help", "dry-run", "bin", "apply", "safe-input", "binlog", "failed-logs"];
    static readonly HashSet<string> Values = ["root", "toolkit", "home", "codex-home", "base", "baseline", "query", "issue", "branch", "pr", "run-id", "project", "file", "sha256", "input", "output", "skill", "results", "configuration", "test", "class", "category", "filter", "process-id", "signal", "duration-seconds"];
    public string? Get(string name) => Options.GetValueOrDefault(name);
    public bool Flag(string name) => Options.ContainsKey(name);
    public string Require(string name) => Get(name) is { Length: > 0 } v ? v : throw new ArgumentException($"--{name} is required.");
    public int PositiveInt(string name) => int.TryParse(Require(name), out var i) && i > 0 ? i : throw new ArgumentException($"--{name} must be a positive integer.");
    public static Cli Parse(string[] args)
    {
        var result = new Cli();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "-h") arg = "--help";
            if (!arg.StartsWith('-')) { result.Words.Add(arg); continue; }
            if (!arg.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Only long options are supported.");
            var parts = arg[2..].Split('=', 2); var name = parts[0];
            if (!Flags.Contains(name) && !Values.Contains(name)) throw new ArgumentException($"Unknown option --{name}.");
            if (result.Options.ContainsKey(name)) throw new ArgumentException($"Duplicate --{name}.");
            if (Flags.Contains(name)) { if (parts.Length != 1) throw new ArgumentException($"--{name} takes no value."); result.Options[name] = null; }
            else
            {
                var value = parts.Length == 2 ? parts[1] : ++i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i] : throw new ArgumentException($"Missing value for --{name}.");
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Empty --{name}.");
                result.Options[name] = value;
            }
        }
        return result;
    }
}

public static class Processes
{
    public static bool OnPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, name)) || OperatingSystem.IsWindows() && File.Exists(Path.Combine(p, name + ".exe")));
    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string cwd)
    {
        var info = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in args)
        {
            if (JevCredentials.Contains(arg)) throw new InvalidOperationException("Refusing to place JEV credentials in child-process arguments.");
            info.ArgumentList.Add(arg);
        }
        info.Environment.Remove(JevCredentials.EnvironmentVariable);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return info;
    }
    public static async Task<ProcessResult> Run(string exe, IEnumerable<string> args, string cwd, string? artifact = null, TimeSpan? timeout = null)
    {
        var info = StartInfo(exe, args, cwd);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        using var timer = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        using var writer = artifact is null ? null : new StreamWriter(artifact, false, new UTF8Encoding(false));
        var gate = new SemaphoreSlim(1); var buffer = new StringBuilder(); bool overflow = false;
        async Task Drain(StreamReader reader)
        {
            var chars = new char[4096]; int count;
            while ((count = await reader.ReadAsync(chars, timer.Token)) > 0)
            {
                await gate.WaitAsync(timer.Token);
                try
                {
                    if (writer is not null) await writer.WriteAsync(chars.AsMemory(0, count), timer.Token);
                    else if (buffer.Length + count <= 16 * 1024 * 1024) buffer.Append(chars, 0, count);
                    else overflow = true;
                }
                finally { gate.Release(); }
            }
        }
        var reads = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try { await Task.WhenAll(reads, process.WaitForExitAsync(timer.Token)); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            return new(124, "Operation timed out; process tree terminated.");
        }
        if (overflow) throw new InvalidOperationException("Structured command output exceeded 16 MiB; narrow scope.");
        return new(process.ExitCode, buffer.ToString());
    }
}

public static class Git
{
    public static async Task<string> Require(string root, params string[] args)
    {
        var r = await Processes.Run("git", args, root);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Git {args[0]} failed: {Secrets.Redact(r.Output.Trim())}");
        return r.Output;
    }
    public static async Task<GitState> State(string root)
    {
        var actual = (await Require(root, "rev-parse", "--show-toplevel")).Trim();
        var operations = new List<string>();
        foreach (var op in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_LOG", "sequencer", "index.lock" })
        {
            var path = (await Require(root, "rev-parse", "--git-path", op)).Trim();
            path = Path.GetFullPath(path, root);
            if (File.Exists(path) || Directory.Exists(path)) operations.Add(op);
        }
        var status = await Require(actual, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        var branch = await Processes.Run("git", ["symbolic-ref", "--quiet", "--short", "HEAD"], root);
        return new(actual, branch.ExitCode == 0 ? branch.Output.Trim() : null, status.Length == 0, operations, status.Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }
    public static async Task EnsureSafe(string root, bool requireClean)
    {
        var state = await State(root);
        if (state.Operations.Count != 0) throw new InvalidOperationException("Unfinished Git operation detected; no mutation performed.");
        if (state.Branch is null) throw new InvalidOperationException("Detached HEAD; no mutation performed.");
        if (requireClean && !state.Clean) throw new InvalidOperationException("Working tree has changes; preserve them before starting an issue.");
    }
    public static async Task<string[]> Files(string root) => (await Require(root, "ls-files", "--cached", "--others", "--exclude-standard", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static async Task<string[]> Tracked(string root) => (await Require(root, "ls-files", "--cached", "-z")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static async Task<string[]> Changed(string root, string? baseRef = null)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        async Task Add(params string[] args) { foreach (var p in (await Require(root, args)).Split('\0', StringSplitOptions.RemoveEmptyEntries)) names.Add(p); }
        if (baseRef is not null)
        {
            var sha = (await Require(root, "rev-parse", "--verify", "--end-of-options", baseRef + "^{commit}")).Trim();
            var ancestor = (await Require(root, "merge-base", sha, "HEAD")).Trim();
            await Add("diff", "--name-only", "--no-renames", "-z", ancestor, "--");
        }
        else
        {
            await Add("diff", "--name-only", "--no-renames", "-z", "--");
            await Add("diff", "--cached", "--name-only", "--no-renames", "-z", "--");
        }
        await Add("ls-files", "--others", "--exclude-standard", "-z");
        return names.Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<object> ConflictForecast(string root, string baseRef, OutputSettings limits)
    {
        var target = (await Require(root, "rev-parse", "--verify", "--end-of-options", baseRef + "^{commit}")).Trim();
        var head = (await Require(root, "rev-parse", "HEAD^{commit}")).Trim();
        var mergeBase = (await Require(root, "merge-base", head, target)).Trim();
        var result = await Processes.Run("git", ["merge-tree", "--write-tree", "--name-only", "--messages", head, target], root);
        if (result.ExitCode is not (0 or 1)) throw new InvalidOperationException("Git merge-tree could not forecast conflicts: " + Secrets.Redact(string.Join(' ', Output.Compact(result.Output, limits))));
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var conflicts = lines.Where(line => line.StartsWith("CONFLICT ", StringComparison.Ordinal)).ToArray();
        var paths = conflicts.Select(line => Regex.Match(line, @"(?: in |delete/modify: )(.+?)(?: deleted|$)").Groups[1].Value.Trim()).Where(path => path.Length > 0).Distinct(StringComparer.Ordinal).Take(limits.MaxItems).ToArray();
        return new { schemaVersion = 1, kind = "git-conflict-forecast", baseRef, head, target, mergeBase, hasConflicts = result.ExitCode == 1, conflictCount = conflicts.Length, paths, pathsTruncated = conflicts.Length > paths.Length, evidence = conflicts.Take(limits.MaxItems), note = "Forecast only: refs, index and worktree were not changed. Rename and custom merge-driver behavior may differ in a real merge." };
    }
}
public record GitState(string Root, string? Branch, bool Clean, List<string> Operations, string[] Entries);

public static class Repository
{
    public static async Task<object> Summary(string root, string? baseRef, OutputSettings limits)
    {
        var state = await Git.State(root);
        var changed = await Git.Changed(root, baseRef);
        var upstream = await Processes.Run("git", ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}"], root);
        int? ahead = null, behind = null; string? upstreamName = null;
        if (upstream.ExitCode == 0)
        {
            upstreamName = upstream.Output.Trim();
            var divergence = await Processes.Run("git", ["rev-list", "--left-right", "--count", $"HEAD...{upstreamName}"], root);
            var counts = divergence.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (divergence.ExitCode == 0 && counts.Length == 2 && int.TryParse(counts[0], out var local) && int.TryParse(counts[1], out var remote)) { ahead = local; behind = remote; }
        }
        var byExtension = changed.GroupBy(path => Path.GetExtension(path).ToLowerInvariant() is { Length: > 0 } extension ? extension : "(none)", StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var byArea = changed.GroupBy(path => path.Replace('\\', '/').Split('/', 2)[0], StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return new
        {
            schemaVersion = 1,
            kind = "repository-summary",
            state = new { state.Branch, state.Clean, state.Operations, entries = state.Entries.Take(limits.MaxItems), entriesTruncated = state.Entries.Length > limits.MaxItems },
            upstream = new { name = upstreamName, ahead, behind },
            changes = new { baseRef, count = changed.Length, files = changed.Take(limits.MaxItems), truncated = changed.Length > limits.MaxItems, byExtension, byArea }
        };
    }
    public static async Task<object> Hygiene(string root, OutputSettings limits)
    {
        var tracked = await Git.Tracked(root); var candidates = new List<object>(); int total = 0;
        foreach (var relative in tracked)
        {
            var normalized = relative.Replace('\\', '/'); var file = Path.Combine(root, relative); string? reason = null;
            if (Regex.IsMatch(normalized, @"(^|/)(tests?|specs?)/fixtures/", RegexOptions.IgnoreCase)) continue;
            if (Regex.IsMatch(normalized, @"(^|/)(bin|obj|dist|coverage|TestResults)/", RegexOptions.IgnoreCase)) reason = "tracked-output-directory";
            else if (Regex.IsMatch(normalized, @"(^|/)(\.DS_Store|Thumbs\.db)$|\.(tmp|bak|orig|rej|log)$", RegexOptions.IgnoreCase)) reason = "temporary-or-tool-artifact";
            else if (File.Exists(file) && new FileInfo(file).Length <= 1_000_000)
            {
                using var reader = new StreamReader(file); var prefix = new char[2048]; var read = reader.Read(prefix, 0, prefix.Length);
                var header = string.Join('\n', new string(prefix, 0, read).Split('\n').Take(5));
                if (Regex.IsMatch(header, @"(?im)^\s*(?://+|#+|/\*+|<!--)\s*(?:<auto-generated|auto[- ]generated|generated (?:code|file)|this file (?:is|was) generated|do not edit)")) reason = "generated-content-marker";
            }
            if (reason is null) continue; total++;
            if (candidates.Count < limits.MaxItems) candidates.Add(new { path = normalized, reason });
        }
        return new { schemaVersion = 1, kind = "repository-hygiene", trackedFiles = tracked.Length, candidateCount = total, candidates, truncated = total > candidates.Count, policy = "Evidence-backed candidates only; no file is deleted and reachability is not inferred." };
    }
}

public static class GitHub
{
    public static async Task<Result> Actions(string root, string artifacts, string? runId, bool failedLogs, OutputSettings limits)
    {
        if (failedLogs && runId is null) throw new ArgumentException("--failed-logs requires --run-id.");
        if (runId is not null && (!long.TryParse(runId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)) throw new ArgumentException("--run-id must be a positive integer.");
        if (failedLogs) return await AgentTool.RunArtifact("gh", ["run", "view", runId!, "--log-failed"], root, artifacts, limits);
        var fields = "databaseId,name,displayTitle,workflowName,status,conclusion,event,headBranch,headSha,url,createdAt,updatedAt";
        var arguments = runId is null ? new[] { "run", "list", "--limit", limits.MaxItems.ToString(CultureInfo.InvariantCulture), "--json", fields } : ["run", "view", runId, "--json", fields + ",jobs"];
        var result = await Processes.Run("gh", arguments, root);
        if (result.ExitCode != 0) return new("failed", new { evidence = Output.Compact(result.Output, limits) }, 1);
        return Result.Ok(ParseActions(result.Output, runId is null, limits));
    }

    public static object ParseActions(string json, bool list, OutputSettings limits)
    {
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        var runs = list ? root.EnumerateArray().ToArray() : [root];
        var rows = runs.Take(limits.MaxItems).Select(run => new
        {
            id = Value(run, "databaseId"),
            workflow = Text(run, "workflowName") ?? Text(run, "name"),
            title = Text(run, "displayTitle"),
            status = Text(run, "status"),
            conclusion = Text(run, "conclusion"),
            @event = Text(run, "event"),
            branch = Text(run, "headBranch"),
            sha = Text(run, "headSha"),
            url = Text(run, "url"),
            createdAt = Text(run, "createdAt"),
            updatedAt = Text(run, "updatedAt")
        }).ToArray();
        var jobs = new List<object>(); int jobCount = 0, failedJobs = 0, cancelledJobs = 0;
        if (!list && root.TryGetProperty("jobs", out var jobArray) && jobArray.ValueKind == JsonValueKind.Array)
            foreach (var job in jobArray.EnumerateArray())
            {
                jobCount++; var conclusion = Text(job, "conclusion");
                if (conclusion is "failure" or "timed_out" or "action_required") failedJobs++;
                if (conclusion == "cancelled") cancelledJobs++;
                if (jobs.Count >= limits.MaxItems) continue;
                var failedSteps = job.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                    ? steps.EnumerateArray().Where(step => Text(step, "conclusion") is "failure" or "cancelled" or "timed_out").Take(limits.MaxItems).Select(step => new { name = Text(step, "name"), number = Value(step, "number"), conclusion = Text(step, "conclusion") }).ToArray() : [];
                jobs.Add(new { id = Value(job, "databaseId"), name = Text(job, "name"), status = Text(job, "status"), conclusion, startedAt = Text(job, "startedAt"), completedAt = Text(job, "completedAt"), url = Text(job, "url"), failedSteps });
            }
        return new { schemaVersion = 1, kind = "github-actions-summary", mode = list ? "runs" : "run", runCount = runs.Length, runs = rows, runsTruncated = runs.Length > rows.Length, jobCount, failedJobs, cancelledJobs, jobs, jobsTruncated = jobCount > jobs.Count, next = failedJobs > 0 ? "Fetch --failed-logs for this run and diagnose the earliest causal failure." : null };
    }
    static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    static object? Value(JsonElement element, string property) => element.TryGetProperty(property, out var value) ? value.ValueKind switch { JsonValueKind.Number when value.TryGetInt64(out var number) => number, JsonValueKind.String => value.GetString(), _ => value.ToString() } : null;
}

public static class Results
{
    static readonly Dictionary<string, string> Persistent = new(StringComparer.Ordinal) { ["audit"] = "audits", ["handoff"] = "handoffs", ["review"] = "reviews", ["report"] = "reports" };
    static readonly string[] Directories = ["audits", "handoffs", "reviews", "reports", "evals", "logs", "traces", "sarif", "binlogs", "test-results", "tmp"];
    static readonly string[] Transient = ["evals/generated", "logs", "traces", "sarif", "binlogs", "test-results", "tmp"];
    static readonly Regex Name = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);
    static string Root(string root) => Path.Combine(Path.GetFullPath(root), ".agent-results");
    static string Type(string value) => Persistent.TryGetValue(value, out var directory) ? directory : throw new ArgumentException("Result type must be audit, handoff, review, or report.");
    public static void RequireWords(string[] words, int count, string usage) { if (words.Length != count) throw new ArgumentException(usage); }
    static string SafeName(string name) => Name.IsMatch(name) && name.Length <= 80 ? name : throw new ArgumentException("Result name must be 1-80 lowercase letters, numbers, and single hyphens.");
    static string Readme => """
        # Agent results

        Durable handoffs, audits, reviews, and reports live here. Large or transient output belongs in the named transient directories and is ignored. Normal repository discovery excludes this directory; reference an artifact by path when it matters to a later independent chat.
        """;
    public static Result Init(string root)
    {
        var results = Root(root); SafeFiles.NoLinks(results); Directory.CreateDirectory(results);
        foreach (var directory in Directories) Directory.CreateDirectory(Path.Combine(results, directory));
        Directory.CreateDirectory(Path.Combine(results, "evals", "generated"));
        var readme = Path.Combine(results, "README.md"); var created = !File.Exists(readme);
        if (created) SafeFiles.Atomic(readme, Readme);
        return Result.Ok(new { initialized = results, directories = Directories, readmeCreated = created });
    }
    public static async Task<Result> New(string root, string[] words)
    {
        RequireWords(words, 2, "Usage: results new <audit|handoff|review|report> <name>.");
        var type = words[0]; var directory = Type(type); var name = SafeName(words[1]); Init(root); var now = DateTimeOffset.UtcNow;
        var head = (await Git.Require(root, "rev-parse", "HEAD")).Trim(); var branch = (await Processes.Run("git", ["branch", "--show-current"], root)).Output.Trim();
        var file = Path.Combine(Root(root), directory, $"{now:yyyyMMddTHHmmssZ}-{name}.md"); if (File.Exists(file)) throw new IOException("A result already exists for this timestamp and name; retry.");
        var title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(type) + ": " + name.Replace('-', ' ');
        SafeFiles.Atomic(file, $"""
            # {title}

            - Time (UTC): {now:O}
            - HEAD: {head}
            - Branch: {branch}
            - Status: draft

            ## Purpose

            ## Findings or summary

            ## Decisions

            ## Unresolved

            ## Follow-up

            ## Artifact paths
            """);
        return Result.Ok(new { path = file, type, name, timestampUtc = now, head, branch, status = "draft" });
    }
    static ResultFile[] Files(string root, string? type = null)
    {
        var results = Root(root); if (!Directory.Exists(results)) return [];
        var folders = type is null ? Persistent.Select(x => x.Value) : [Type(type)];
        return folders.SelectMany(folder => Directory.Exists(Path.Combine(results, folder)) ? Directory.EnumerateFiles(Path.Combine(results, folder), "*.md") : [])
            .Select(ResultFile.From).OrderByDescending(x => x.TimestampUtc).ThenByDescending(x => x.Path, StringComparer.Ordinal).ToArray();
    }
    public static Result List(string root, string[] words) { if (words.Length > 1) throw new ArgumentException("Usage: results list [audit|handoff|review|report]."); var files = Files(root, words.FirstOrDefault()); return Result.Ok(new { count = files.Length, results = files }); }
    public static Result Latest(string root, string[] words) { RequireWords(words, 1, "Usage: results latest <audit|handoff|review|report>."); return Result.Ok(new { result = Files(root, words[0]).FirstOrDefault() }); }
    public static Result Context(string root, string[] words) { RequireWords(words, 1, "Usage: results context <audit|handoff|review|report>."); var file = Files(root, words[0]).FirstOrDefault(); return Result.Ok(new { result = file is null ? null : new { file.Path, file.Type, file.TimestampUtc, file.Status, carryForward = file.CarryForward } }); }
    public static Result Clean(string root, bool dryRun)
    {
        var results = Root(root); if (!Directory.Exists(results)) return Result.Ok(new { dryRun, removed = 0, paths = Array.Empty<string>() }); SafeFiles.NoLinks(results); var paths = new List<string>();
        foreach (var directory in Transient) { var target = Path.Combine(results, directory); SafeFiles.NoLinks(target); if (Directory.Exists(target)) paths.AddRange(EnumerateTransient(target)); }
        if (!dryRun) foreach (var path in paths) File.Delete(path); return Result.Ok(new { dryRun, removed = paths.Count, paths });
    }
    static IEnumerable<string> EnumerateTransient(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
        foreach (var child in Directory.EnumerateDirectories(directory)) if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) foreach (var file in EnumerateTransient(child)) yield return file;
    }
}
public record ResultFile(string Path, string Type, DateTimeOffset TimestampUtc, string? Status, string CarryForward)
{
    public static ResultFile From(string path)
    {
        var stamp = System.IO.Path.GetFileNameWithoutExtension(path).Split('-', 2)[0]; if (!DateTimeOffset.TryParseExact(stamp, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)) timestamp = File.GetLastWriteTimeUtc(path);
        var text = File.ReadAllText(path); var statusMatch = Regex.Match(text, @"(?m)^- Status: (.+)$");
        var carry = string.Join("\n", Regex.Matches(text, @"(?ms)^## (?:Unresolved|Follow-up)\r?\n(.*?)(?=^## |\z)").SelectMany(x => x.Groups[1].Value.Split('\n')).Select(x => x.Trim()).Where(x => x.Length > 0).Take(8));
        var directory = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)!); var type = new Dictionary<string, string> { ["audits"] = "audit", ["handoffs"] = "handoff", ["reviews"] = "review", ["reports"] = "report" }.GetValueOrDefault(directory, directory);
        return new(path, type, timestamp, statusMatch.Success ? statusMatch.Groups[1].Value : null, carry);
    }
}

public static class Projects
{
    public static string[] Discover(string root) => SafeFiles.Enumerate(root).Where(x => Path.GetExtension(x) is ".csproj" or ".fsproj" or ".vbproj").Order(StringComparer.Ordinal).ToArray();
    public static string[] Solutions(string root) => SafeFiles.Enumerate(root).Where(x => Path.GetExtension(x) is ".sln" or ".slnx").Order(StringComparer.Ordinal).ToArray();
    public static async Task<string[]> InSolution(string root, string solution)
    {
        var full = Path.GetFullPath(solution, root); if (!File.Exists(full)) throw new ArgumentException("Solution does not exist.");
        var result = await Processes.Run("dotnet", ["sln", full, "list"], root);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not list projects in {Path.GetFileName(full)}.");
        var directory = Path.GetDirectoryName(full)!;
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => Path.GetExtension(line.Trim('"')) is ".csproj" or ".fsproj" or ".vbproj")
            .Select(line => Path.GetFullPath(line.Trim('"'), directory)).Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<JsonNode> Evaluate(string root, string project)
    {
        var result = await Processes.Run("dotnet", ["msbuild", project, "-nologo", "-getProperty:TargetFramework,TargetFrameworks,OutputType,IsTestProject,Nullable,ManagePackageVersionsCentrally,Deterministic,EnableNETAnalyzers,RestorePackagesWithLockFile,EnablePackageValidation,PackageValidationBaselineVersion,IsTestingPlatformApplication,TestingPlatformDotnetTestSupport,UseMicrosoftTestingPlatformRunner", "-getItem:ProjectReference,Compile,PackageReference"], root);
        if (result.ExitCode != 0) throw new InvalidOperationException($"MSBuild evaluation failed for {Path.GetFileName(project)}; graph cannot safely be narrowed.");
        return JsonNode.Parse(result.Output) ?? throw new InvalidOperationException("Empty MSBuild response.");
    }
    public static async Task<bool> IsTest(string root, string project) => (await Evaluate(root, project))["Properties"]?["IsTestProject"]?.GetValue<string>().Equals("true", StringComparison.OrdinalIgnoreCase) == true;
    public static async Task<Affected> Affected(string root, string[] changed)
    {
        root = Path.GetFullPath(root);
        changed = changed.Where(x => !SafeFiles.IsDiscoveryExcluded(x)).ToArray();
        var projects = Discover(root);
        if (changed.Length == 0) return new([], "No build-relevant changed files.");
        var broad = changed.Any(x => x.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(x) is "global.json" or "NuGet.Config" or "nuget.config" || x.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        if (broad) return new(projects, "Shared build, solution or project metadata changed; conservative full graph.");
        var selected = new HashSet<string>(StringComparer.Ordinal); var references = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var paths = changed.Select(x => Path.GetFullPath(x, root)).ToHashSet(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var evaluation = await Evaluate(root, project);
            if (!string.IsNullOrEmpty(evaluation["Properties"]?["TargetFrameworks"]?.GetValue<string>())) return new(projects, "Multi-targeted graph; conservative full graph (conditional inner builds may differ).");
            var items = evaluation["Items"];
            var compiles = items?["Compile"]?.AsArray().Select(x => x?["FullPath"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            references[project] = items?["ProjectReference"]?.AsArray().Select(x => x?["FullPath"]?.GetValue<string>()).OfType<string>().ToArray() ?? [];
            if (compiles.Any(paths.Contains) || paths.Any(p => p.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))) selected.Add(project);
        }
        // Removed linked files and custom build inputs cannot always be inferred from evaluated Compile items.
        if (paths.Any(p => !projects.Any(project => p.StartsWith(Path.GetDirectoryName(project)! + Path.DirectorySeparatorChar, StringComparison.Ordinal))))
            return new(projects, "Change outside project directories; conservative full graph for custom or removed linked inputs.");
        bool added;
        do { added = false; foreach (var p in projects) if (references[p].Any(selected.Contains)) added |= selected.Add(p); } while (added);
        return new(selected.Order(StringComparer.Ordinal).ToArray(), "Evaluated Compile/ProjectReference graph including transitive dependents.");
    }
    public static async Task<bool> HasApiChecks(string root, string path)
    {
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return false;
        var evaluation = await Evaluate(root, path);
        var properties = evaluation["Properties"];
        return string.Equals(properties?["EnablePackageValidation"]?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(properties?["PackageValidationBaselineVersion"]?.GetValue<string>())
            || evaluation["Items"]?["PackageReference"]?.AsArray().Any(item => string.Equals(item?["Identity"]?.GetValue<string>(), "Microsoft.CodeAnalysis.PublicApiAnalyzers", StringComparison.OrdinalIgnoreCase)) == true;
    }
    public static async Task<string[]> DependentTests(string root, string project)
    {
        var target = Path.GetFullPath(project);
        var projects = Discover(root); var references = new Dictionary<string, string[]>(StringComparer.Ordinal); var tests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in projects)
        {
            var evaluation = await Evaluate(root, candidate);
            references[candidate] = evaluation["Items"]?["ProjectReference"]?.AsArray().Select(x => Path.GetFullPath(x?["FullPath"]?.GetValue<string>() ?? "", root)).Where(File.Exists).ToArray() ?? [];
        }
        bool DependsOn(string candidate, string wanted, HashSet<string> visiting)
        {
            if (!visiting.Add(candidate)) return false;
            return references[candidate].Any(reference => string.Equals(reference, wanted, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || references.ContainsKey(reference) && DependsOn(reference, wanted, visiting));
        }
        foreach (var candidate in projects) if (await IsTest(root, candidate) && DependsOn(candidate, target, [])) tests.Add(candidate);
        return tests.Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<object> Ownership(string root, string file)
    {
        root = Path.GetFullPath(root); var full = Path.GetFullPath(file, root); var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) throw new ArgumentException("File must be inside the repository root.");
        var projects = Discover(root); var compileOwners = new List<string>(); var directoryOwners = new List<string>();
        foreach (var project in projects)
        {
            var evaluation = await Evaluate(root, project);
            var compiles = evaluation["Items"]?["Compile"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>() ?? [];
            if (compiles.Any(path => string.Equals(Path.GetFullPath(path), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) compileOwners.Add(project);
            var directory = Path.GetDirectoryName(project)!;
            if (full.StartsWith(directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) directoryOwners.Add(project);
        }
        var owners = compileOwners.Count > 0 ? compileOwners : directoryOwners.OrderByDescending(path => Path.GetDirectoryName(path)!.Length).Take(1).ToList();
        var impacted = new HashSet<string>(owners, StringComparer.Ordinal);
        foreach (var owner in owners) foreach (var dependent in await Dependents(root, owner)) impacted.Add(dependent);
        string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
        return new { schemaVersion = 1, kind = "file-ownership", file = relative, exists = File.Exists(full), basis = compileOwners.Count > 0 ? "evaluated-compile-item" : owners.Count > 0 ? "nearest-project-directory" : "unowned", owners = owners.Select(Rel), impactedProjects = impacted.Order(StringComparer.Ordinal).Select(Rel) };
    }
    public static async Task<string[]> Dependents(string root, string project)
    {
        var projects = Discover(root); var selected = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(project) }; var references = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var candidate in projects) references[candidate] = (await Evaluate(root, candidate))["Items"]?["ProjectReference"]?.AsArray().Select(x => Path.GetFullPath(x?["FullPath"]?.GetValue<string>() ?? "", root)).Where(File.Exists).ToArray() ?? [];
        bool added; do { added = false; foreach (var candidate in projects) if (references[candidate].Any(selected.Contains)) added |= selected.Add(candidate); } while (added);
        selected.Remove(Path.GetFullPath(project)); return selected.Order(StringComparer.Ordinal).ToArray();
    }
    public static async Task<List<object>> Health(string root, HealthSettings policy)
    {
        var findings = new List<object>(); var projects = Discover(root);
        if (projects.Length == 0) findings.Add(new { rule = "projects", message = "No project files found; file-based apps are not project-scanned." });
        foreach (var project in projects)
        {
            var props = (await Evaluate(root, project))["Properties"]!;
            foreach (var (name, expected, enabled) in new[] { ("Nullable", "enable", policy.RequireNullable), ("ManagePackageVersionsCentrally", "true", policy.RequireCentralPackages), ("Deterministic", "true", policy.RequireDeterministic), ("EnableNETAnalyzers", "true", policy.RequireAnalyzers), ("RestorePackagesWithLockFile", "true", policy.RequireLockFiles) })
                if (enabled && !string.Equals(props[name]?.GetValue<string>(), expected, StringComparison.OrdinalIgnoreCase)) findings.Add(new { project, rule = name, expected, actual = props[name]?.GetValue<string>() });
            var frameworks = (props["TargetFrameworks"]?.GetValue<string>() is { Length: > 0 } multi ? multi : props["TargetFramework"]?.GetValue<string>() ?? "").Split(';');
            foreach (var tfm in frameworks) if (policy.AllowedFrameworks.Length > 0 && !policy.AllowedFrameworks.Contains(tfm)) findings.Add(new { project, rule = "TargetFramework", actual = tfm });
        }
        if (!File.Exists(Path.Combine(root, "global.json"))) findings.Add(new { rule = "sdk", message = "Missing global.json SDK policy." });
        return findings;
    }
}
public record Affected(string[] Projects, string Reason);

public static class DotnetFacts
{
    public static async Task<object> Inspect(string root, string? explicitProject)
    {
        root = Path.GetFullPath(root);
        var projects = explicitProject is null ? Projects.Discover(root) : IsSolution(explicitProject) ? await Projects.InSolution(root, explicitProject) : ResolveTargets(root, explicitProject);
        var sdkVersion = await Processes.Run("dotnet", ["--version"], root);
        var sdks = await Processes.Run("dotnet", ["--list-sdks"], root);
        var runtimes = await Processes.Run("dotnet", ["--list-runtimes"], root);
        var rows = new List<object>(); var edges = new List<object>();
        foreach (var project in projects)
        {
            var evaluation = await Projects.Evaluate(root, project); var properties = evaluation["Properties"]!; var items = evaluation["Items"];
            var targetFrameworks = Frameworks(properties).ToArray();
            var references = items?["ProjectReference"]?.AsArray().Select(item => item?["FullPath"]?.GetValue<string>()).OfType<string>().Select(path => Rel(root, path)).Order(StringComparer.Ordinal).ToArray() ?? [];
            var packages = items?["PackageReference"]?.AsArray().Select(item => new { id = item?["Identity"]?.GetValue<string>(), version = ItemValue(item, "Version") }).OrderBy(item => item.id, StringComparer.Ordinal).ToArray() ?? [];
            var profile = TestProfileFor(root, properties, packages.Select(package => package.id).OfType<string>());
            rows.Add(new { path = Rel(root, project), language = Path.GetExtension(project) switch { ".fsproj" => "F#", ".vbproj" => "Visual Basic", _ => "C#" }, targetFrameworks, isTest = IsTrue(properties["IsTestProject"]), testPlatform = profile.Platform, testFramework = profile.Framework, testCommandMode = profile.CommandMode, projectReferences = references, packageReferences = packages });
            edges.AddRange(references.Select(reference => new { from = Rel(root, project), to = reference }));
        }
        JsonNode? globalJson = null; var globalJsonPath = Path.Combine(root, "global.json"); if (File.Exists(globalJsonPath)) globalJson = JsonNode.Parse(File.ReadAllText(globalJsonPath));
        return new
        {
            schemaVersion = 1,
            kind = "dotnet-inspection",
            environment = new { sdk = sdkVersion.ExitCode == 0 ? sdkVersion.Output.Trim() : null, installedSdks = Lines(sdks), installedRuntimes = Lines(runtimes), processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription, globalJson },
            projects = rows,
            graph = new { nodes = projects.Select(project => Rel(root, project)), edges }
        };
    }

    public static async Task<object> BuildPlan(string root, string? explicitProject, string? baseRef, string configuration, bool binlog)
    {
        ValidateConfiguration(configuration); root = Path.GetFullPath(root);
        var affected = explicitProject is null ? await Projects.Affected(root, await Git.Changed(root, baseRef)) : null;
        var targets = explicitProject is null ? affected!.Projects : ResolveTargets(root, explicitProject);
        var commands = new List<object>();
        foreach (var target in targets)
        {
            var relative = Rel(root, target); commands.Add(Command("restore", relative));
            var args = new List<string> { "build", relative, "--configuration", configuration, "--no-restore", "--nologo" };
            if (binlog) args.Add($"-bl:.agent-tool/binlogs/{SafeArtifactName(relative)}.binlog");
            commands.Add(Command(args));
        }
        return new { schemaVersion = 1, kind = "dotnet-build-plan", configuration, baseRef, targets = targets.Select(target => Rel(root, target)), selection = explicitProject is null ? affected!.Reason : "Explicit project or solution.", commands, artifacts = binlog ? new { binlogs = ".agent-tool/binlogs/*.binlog", retention = "Large binary artifacts stay outside model context; never send binlogs to JEV or a model.", analysisOrder = new[] { "structured-binlog-query", "bounded-text-log-fallback" }, structuredAnalyzer = "Microsoft.AITools.BinlogMcp (optional upstream integration)" } : null };
    }

    public static async Task<object> TestPlan(string root, string? explicitProject, string? baseRef, string configuration, TestSelection requested)
    {
        ValidateConfiguration(configuration); root = Path.GetFullPath(root); requested.Validate();
        string[] tests; string scope;
        if (explicitProject is null)
        {
            var affected = await Projects.Affected(root, await Git.Changed(root, baseRef));
            var selected = new List<string>(); foreach (var project in affected.Projects) if (await Projects.IsTest(root, project)) selected.Add(project);
            tests = selected.Order(StringComparer.Ordinal).ToArray(); scope = affected.Reason;
        }
        else
        {
            var target = Path.GetFullPath(explicitProject, root); if (!File.Exists(target)) throw new ArgumentException("Project or solution does not exist.");
            if (IsSolution(target))
            {
                var selected = new List<string>(); foreach (var project in await Projects.InSolution(root, target)) if (await Projects.IsTest(root, project)) selected.Add(project); tests = selected.ToArray();
            }
            else if (await Projects.IsTest(root, target)) tests = [target];
            else tests = await Projects.DependentTests(root, target);
            scope = "Explicit target and its transitive dependent test projects.";
        }
        var rows = new List<object>();
        foreach (var test in tests)
        {
            var evaluation = await Projects.Evaluate(root, test); var packages = evaluation["Items"]?["PackageReference"]?.AsArray().Select(item => item?["Identity"]?.GetValue<string>()).OfType<string>() ?? [];
            var profile = TestProfileFor(root, evaluation["Properties"]!, packages);
            var args = TestCommand(Rel(root, test), configuration, profile, requested);
            rows.Add(new { project = Rel(root, test), platform = profile.Platform, framework = profile.Framework, commandMode = profile.CommandMode, targetFrameworks = Frameworks(evaluation["Properties"]!).ToArray(), command = Command(args) });
        }
        return new { schemaVersion = 1, kind = "dotnet-test-plan", configuration, filter = requested.Filter, selection = scope, requestedSelection = requested, tests = rows, artifacts = new { results = ".agent-tool/test-results/**/*.trx", coverage = ".agent-tool/test-results/**/coverage.*.xml" } };
    }

    static string[] TestCommand(string project, string configuration, TestProfile profile, TestSelection selection)
    {
        if (profile.Platform is not ("vstest" or "microsoft-testing-platform"))
            throw new InvalidOperationException($"{project} has no recognized test platform. Load the test-platform edge-case reference; do not guess a command.");
        if (profile.Platform == "microsoft-testing-platform" && profile.CommandMode == "unconfigured")
            throw new InvalidOperationException($"{project} uses Microsoft.Testing.Platform but has neither SDK 10 native mode nor an executable VSTest bridge. Load the test-platform edge-case reference; do not guess a command.");
        var args = new List<string> { "test" };
        if (profile.CommandMode == "mtp-native") { args.Add("--project"); args.Add(project); }
        else args.Add(project);
        args.Add("--configuration"); args.Add(configuration); args.Add("--nologo");

        var runner = new List<string>();
        if (profile.Platform == "vstest") { runner.Add("--logger"); runner.Add("trx"); runner.Add("--results-directory"); runner.Add(".agent-tool/test-results"); }
        else { runner.Add("--report-trx"); runner.Add("--results-directory"); runner.Add(".agent-tool/test-results"); }
        runner.AddRange(FilterArguments(profile, selection));
        if (profile.CommandMode == "mtp-bridge") args.Add("--");
        args.AddRange(runner);
        return args.ToArray();
    }

    static string[] FilterArguments(TestProfile profile, TestSelection selection)
    {
        var value = selection.Value; if (value is null) return [];
        if (selection.Filter is not null)
        {
            if (profile.Platform == "microsoft-testing-platform" && profile.Framework is "xunit-v3" or "tunit" or "unknown")
                throw new InvalidOperationException($"Raw --filter is not portable to {profile.Framework} on Microsoft.Testing.Platform. Use --test, --class, or --category, or load the test-platform edge-case reference.");
            return ["--filter", value];
        }
        if (profile.Platform == "microsoft-testing-platform" && profile.Framework == "xunit-v3")
            return selection.Test is not null ? ["--filter-method", value] : selection.Class is not null ? ["--filter-class", value] : ["--filter-trait", $"Category={value}"];
        if (profile.Platform == "microsoft-testing-platform" && profile.Framework == "tunit")
            return ["--treenode-filter", selection.Test is not null ? $"/*/*/*/{value}" : selection.Class is not null ? $"/*/*/{value}/*" : $"/*/*/*/*[Category={value}]"];
        var expression = selection.Test is not null ? $"FullyQualifiedName={value}" : selection.Class is not null ? $"FullyQualifiedName~{value}" : $"TestCategory={value}";
        return ["--filter", expression];
    }

    public static async Task<object> DiagnosticsPlan(string? processId, string? requestedSignal = null, string? requestedDurationSeconds = null, string? workingDirectory = null)
    {
        int? pid = null;
        if (processId is not null)
        {
            if (!int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) throw new ArgumentException("--process-id must be a positive integer.");
            pid = parsed;
        }
        var signal = requestedSignal ?? "counters";
        if (signal is not ("counters" or "cpu" or "contention" or "allocations" or "managed-memory" or "crash" or "hang"))
            throw new ArgumentException("--signal must be counters, cpu, contention, allocations, managed-memory, crash, or hang.");
        var durationSeconds = 30;
        if (requestedDurationSeconds is not null && (!int.TryParse(requestedDurationSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out durationSeconds) || durationSeconds is < 5 or > 300))
            throw new ArgumentException("--duration-seconds must be an integer from 5 through 300.");
        var tools = new[] { "dotnet-counters", "dotnet-trace", "dotnet-dump", "dotnet-gcdump", "dotnet-monitor" }.Select(name => new { name, available = Processes.OnPath(name) }).ToArray();
        var dotnetVersion = await Processes.Run("dotnet", ["--version"], workingDirectory ?? Environment.CurrentDirectory);
        var runtimes = await Processes.Run("dotnet", ["--list-runtimes"], workingDirectory ?? Environment.CurrentDirectory);
        var duration = TimeSpan.FromSeconds(durationSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        object[] plans = pid is null ? [] : signal switch
        {
            "counters" => [ToolCommand("dotnet-counters", "monitor", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--duration", duration)],
            "managed-memory" => [ToolCommand("dotnet-gcdump", "collect", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--output", $".agent-tool/traces/process-{pid}-memory.gcdump")],
            "crash" or "hang" => [ToolCommand("dotnet-dump", "collect", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--output", $".agent-tool/traces/process-{pid}-{signal}.dmp")],
            _ => [ToolCommand("dotnet-trace", "collect", "--process-id", pid.Value.ToString(CultureInfo.InvariantCulture), "--duration", duration, "--output", $".agent-tool/traces/process-{pid}-{signal}.nettrace")]
        };
        var selectedTool = signal switch { "counters" => "dotnet-counters", "managed-memory" => "dotnet-gcdump", "crash" or "hang" => "dotnet-dump", _ => "dotnet-trace" };
        return new
        {
            schemaVersion = 1,
            kind = "dotnet-diagnostics-plan",
            processId = pid,
            signal,
            durationSeconds,
            environment = new { os = RuntimeInformation.OSDescription, processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), osArchitecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription, sdk = dotnetVersion.ExitCode == 0 ? dotnetVersion.Output.Trim() : null, installedRuntimes = Lines(runtimes) },
            tools,
            collection = new { selectedTool, available = tools.Single(tool => tool.name == selectedTool).available, plans, artifactDirectory = ".agent-tool/traces", executesCollection = false },
            analysis = new { order = new[] { "existing-artifact", "bounded-structured-analysis", "collect-smallest-missing-signal" }, modelsReceive = "bounded sanitized summaries only" },
            safety = "Collection can expose secrets and personal data. Keep traces and dumps local, inspect size and sensitivity, and never send raw binary artifacts to JEV or a model."
        };
    }

    static string[] ResolveTargets(string root, string path)
    {
        var full = Path.GetFullPath(path, root); if (!File.Exists(full)) throw new ArgumentException("Project or solution does not exist."); return [full];
    }
    static bool IsSolution(string path) => path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
    static object Command(params string[] args) => Command((IEnumerable<string>)args);
    static object Command(IEnumerable<string> args) => new { executable = "dotnet", arguments = args.ToArray() };
    static object ToolCommand(string executable, params string[] args) => new { executable, arguments = args };
    static string Rel(string root, string path) => Path.GetRelativePath(root, Path.GetFullPath(path)).Replace('\\', '/');
    static string SafeArtifactName(string path) => Regex.Replace(path.Replace('\\', '-').Replace('/', '-'), "[^A-Za-z0-9_.-]", "-");
    static void ValidateConfiguration(string configuration) { if (!Regex.IsMatch(configuration, "^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("Configuration must contain only letters, numbers, dot, underscore, or hyphen."); }
    static bool IsTrue(JsonNode? value) => string.Equals(value?.GetValue<string>(), "true", StringComparison.OrdinalIgnoreCase);
    static IEnumerable<string> Frameworks(JsonNode properties) => (properties["TargetFrameworks"]?.GetValue<string>() is { Length: > 0 } frameworks ? frameworks : properties["TargetFramework"]?.GetValue<string>() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    static string[] Lines(ProcessResult result) => result.ExitCode == 0 ? result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    static string? ItemValue(JsonNode? item, string name) => item?[name]?.GetValue<string>() ?? item?["Metadata"]?[name]?.GetValue<string>();
    static TestProfile TestProfileFor(string root, JsonNode properties, IEnumerable<string> packages)
    {
        var names = packages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var framework = names.Any(name => name.Equals("TUnit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TUnit.", StringComparison.OrdinalIgnoreCase)) ? "tunit"
            : names.Any(name => name.Equals("xunit.v3", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)) ? "xunit-v3"
            : names.Any(name => name.Equals("xunit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase)) ? "xunit-v2"
            : names.Any(name => name.Equals("NUnit", StringComparison.OrdinalIgnoreCase) || name.StartsWith("NUnit.", StringComparison.OrdinalIgnoreCase)) ? "nunit"
            : names.Any(name => name.Equals("MSTest", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MSTest.", StringComparison.OrdinalIgnoreCase)) ? "mstest" : "unknown";
        var mtp = IsTrue(properties["IsTestingPlatformApplication"]) || IsTrue(properties["UseMicrosoftTestingPlatformRunner"]) || names.Contains("Microsoft.Testing.Platform") || names.Contains("MSTest.Sdk") || framework == "tunit";
        if (!mtp) return new(IsTrue(properties["IsTestProject"]) && names.Contains("Microsoft.NET.Test.Sdk") ? "vstest" : IsTrue(properties["IsTestProject"]) ? "unknown" : "not-test-project", framework, "vstest");
        var native = GlobalUsesNativeMtp(root);
        var bridge = IsTrue(properties["TestingPlatformDotnetTestSupport"]) && string.Equals(properties["OutputType"]?.GetValue<string>(), "Exe", StringComparison.OrdinalIgnoreCase);
        return new("microsoft-testing-platform", framework, native ? "mtp-native" : bridge ? "mtp-bridge" : "unconfigured");
    }

    static bool GlobalUsesNativeMtp(string root)
    {
        var path = Path.Combine(root, "global.json"); if (!File.Exists(path)) return false;
        var global = JsonNode.Parse(File.ReadAllText(path));
        var runner = global?["test"]?["runner"]?.GetValue<string>();
        var version = global?["sdk"]?["version"]?.GetValue<string>();
        return runner?.Equals("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase) == true
            && Version.TryParse(version?.Split('-')[0], out var sdk) && sdk.Major >= 10;
    }
}

public record TestProfile(string Platform, string Framework, string CommandMode);
public record TestSelection(string? Test, string? Class, string? Category, string? Filter)
{
    public string? Value => Test ?? Class ?? Category ?? Filter;
    public void Validate()
    {
        var values = new[] { Test, Class, Category, Filter }.Where(value => value is not null).ToArray();
        if (values.Length > 1) throw new ArgumentException("Use only one of --test, --class, --category, or --filter.");
        if (values.Length == 0) return;
        if (string.IsNullOrWhiteSpace(values[0]) || values[0]!.Length > 4096 || values[0]!.Any(char.IsControl)) throw new ArgumentException("Test selection must be 1-4096 printable characters.");
    }
}

public static class SafeFiles
{
    public static bool IsDiscoveryExcluded(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.Equals(".agent-results", StringComparison.Ordinal) || normalized.StartsWith(".agent-results/", StringComparison.Ordinal);
    }

    public static IEnumerable<string> Enumerate(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root)) if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (new[] { ".git", ".agent-tool", ".agent-results", "bin", "obj", "node_modules", "artifacts", "TestResults" }.Contains(Path.GetFileName(dir)) || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var file in Enumerate(dir)) yield return file;
        }
    }
    public static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (new FileInfo(current).LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) throw new IOException("Refusing to write through a symlink in a managed state path.");
    }
    public static void Atomic(string path, string content)
    {
        NoLinks(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class Output
{
    public static string[] Compact(string text, OutputSettings limits) => text.Split('\n').Select(x => Secrets.Redact(x.TrimEnd('\r'))).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).Take(limits.MaxLines).Select(x => x.Length <= limits.MaxLineLength ? x : x[..limits.MaxLineLength] + "…").ToArray();
    public static object SummarizeFile(string path, OutputSettings limits)
    {
        var interesting = new List<string>(); var tail = new Queue<string>(); int lines = 0, errors = 0, warnings = 0;
        foreach (var line in File.ReadLines(path))
        {
            lines++;
            var zeroCount = Regex.IsMatch(line, @"^\s*0\s+(errors?|warnings?)\b", RegexOptions.IgnoreCase);
            var error = !zeroCount && Regex.IsMatch(line, @"\b(error|failed|failure|fatal)\b", RegexOptions.IgnoreCase);
            var warning = !zeroCount && Regex.IsMatch(line, @"\bwarning\b", RegexOptions.IgnoreCase);
            if (error) errors++; if (warning) warnings++;
            if ((error || warning) && interesting.Count < limits.MaxLines) interesting.Add(line);
            tail.Enqueue(line); if (tail.Count > limits.MaxLines) tail.Dequeue();
        }
        return new { lines, errorLines = errors, warningLines = warnings, evidence = Compact(string.Join('\n', interesting.Count > 0 ? interesting.AsEnumerable() : tail), limits), artifact = Path.GetFullPath(path), note = "Text counts are matching lines, not a build success verdict." };
    }
    public static object Sarif(string path, OutputSettings limits, string? baseline = null)
    {
        var current = SarifFindings(path, limits); var prior = baseline is null ? [] : SarifFindings(baseline, limits);
        var priorKeys = prior.Select(x => x.Key).ToHashSet(StringComparer.Ordinal); var currentKeys = current.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var added = current.Where(x => !priorKeys.Contains(x.Key)).ToArray(); var fixedFindings = prior.Where(x => !currentKeys.Contains(x.Key)).ToArray();
        return new
        {
            schemaVersion = 1,
            kind = "sarif-summary",
            count = current.Length,
            results = current.Take(limits.MaxItems).Select(x => x.Evidence),
            truncated = current.Length > limits.MaxItems,
            baseline = baseline is null ? null : new { artifact = Path.GetFullPath(baseline), count = prior.Length, added = added.Length, unchanged = current.Length - added.Length, fixedCount = fixedFindings.Length, newResults = added.Take(limits.MaxItems).Select(x => x.Evidence), newResultsTruncated = added.Length > limits.MaxItems, fixedResults = fixedFindings.Take(limits.MaxItems).Select(x => x.Evidence), fixedResultsTruncated = fixedFindings.Length > limits.MaxItems },
            artifact = Path.GetFullPath(path)
        };
    }
    static SarifFinding[] SarifFindings(string path, OutputSettings limits)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) throw new FormatException("SARIF has no runs array.");
        var rows = new List<SarifFinding>();
        foreach (var run in runs.EnumerateArray())
            if (run.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array) foreach (var result in results.EnumerateArray())
                {
                    var rule = result.TryGetProperty("ruleId", out var ruleValue) ? ruleValue.GetString() : null;
                    var level = result.TryGetProperty("level", out var levelValue) ? levelValue.GetString() : "warning";
                    var message = result.TryGetProperty("message", out var messageValue) && messageValue.TryGetProperty("text", out var text) ? text.GetString() ?? "" : result.TryGetProperty("message", out messageValue) ? messageValue.ToString() : "";
                    string? uri = null; int? line = null;
                    if (result.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array && locations.GetArrayLength() > 0)
                    {
                        var physical = locations[0].TryGetProperty("physicalLocation", out var physicalValue) ? physicalValue : default;
                        if (physical.ValueKind == JsonValueKind.Object && physical.TryGetProperty("artifactLocation", out var artifactLocation) && artifactLocation.TryGetProperty("uri", out var uriValue)) uri = uriValue.GetString();
                        if (physical.ValueKind == JsonValueKind.Object && physical.TryGetProperty("region", out var region) && region.TryGetProperty("startLine", out var lineValue) && lineValue.TryGetInt32(out var parsedLine)) line = parsedLine;
                    }
                    var fingerprint = result.TryGetProperty("partialFingerprints", out var fingerprints) && fingerprints.ValueKind == JsonValueKind.Object
                        ? string.Join('|', fingerprints.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + "=" + x.Value.ToString())) : null;
                    var key = fingerprint is { Length: > 0 } ? rule + "|" + fingerprint : string.Join('|', rule, uri, line?.ToString(CultureInfo.InvariantCulture), message);
                    var suppression = result.TryGetProperty("suppressions", out var suppressions) && suppressions.ValueKind == JsonValueKind.Array ? suppressions.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
                    rows.Add(new(key, new { rule, level, message = Compact(message, limits), location = new { uri, startLine = line }, suppressions = suppression }));
                }
        return rows.ToArray();
    }
    sealed record SarifFinding(string Key, object Evidence);
}

public static class Artifacts
{
    public static object Inspect(string path, OutputSettings limits)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new ArgumentException("Artifact does not exist.");
        var info = new FileInfo(path); var sha256 = Hash(path); var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension != ".zip") return new { schemaVersion = 1, kind = "artifact-inspection", path, format = "file", info.Length, sha256 };
        using var archive = System.IO.Compression.ZipFile.OpenRead(path); var names = new HashSet<string>(StringComparer.Ordinal); var entries = new List<object>(); var issues = new List<object>(); long unpacked = 0; int issueCount = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/'); unpacked += entry.Length; var reasons = new List<string>();
            if (name.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(name, @"^[A-Za-z]:/") || name.Split('/').Contains("..", StringComparer.Ordinal)) reasons.Add("path-traversal");
            if (!names.Add(name)) reasons.Add("duplicate-path");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) reasons.Add("symbolic-link");
            if (entry.Length > 1_000_000_000 || entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 1000) reasons.Add("expansion-risk");
            if (entries.Count < limits.MaxItems) entries.Add(new { path = name, entry.Length, entry.CompressedLength });
            issueCount += reasons.Count; foreach (var reason in reasons) if (issues.Count < limits.MaxItems) issues.Add(new { path = name, reason });
        }
        return new { schemaVersion = 1, kind = "artifact-inspection", path, format = "zip", info.Length, sha256, entryCount = archive.Entries.Count, unpackedBytes = unpacked, entries, entriesTruncated = archive.Entries.Count > entries.Count, issueCount, issues, issuesTruncated = issueCount > issues.Count, safeToExtract = issueCount == 0 };
    }
    public static Result Verify(string path, string expected)
    {
        if (!Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)) throw new ArgumentException("--sha256 must be 64 hexadecimal characters.");
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new ArgumentException("Artifact does not exist.");
        var actual = Hash(path); var matches = CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected));
        return new(matches ? "ok" : "mismatch", new { schemaVersion = 1, kind = "artifact-verification", path, algorithm = "SHA-256", expected = expected.ToLowerInvariant(), actual, matches }, matches ? 0 : 1);
    }
    static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}

public static class DotnetArtifacts
{
    public static object Dependencies(string path, string root, OutputSettings limits)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var rows = new List<object>(); int direct = 0, transitive = 0;
        if (!document.RootElement.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Array) throw new FormatException("NuGet package-list JSON has no projects array.");
        foreach (var project in projects.EnumerateArray())
        {
            var projectPath = project.TryGetProperty("path", out var projectValue) ? Relative(root, projectValue.GetString()) : null;
            if (!project.TryGetProperty("frameworks", out var frameworks)) continue;
            foreach (var framework in frameworks.EnumerateArray())
            {
                var tfm = framework.TryGetProperty("framework", out var frameworkValue) ? frameworkValue.GetString() : null;
                Add("topLevelPackages", true); Add("transitivePackages", false);
                void Add(string property, bool topLevel)
                {
                    if (!framework.TryGetProperty(property, out var packages)) return;
                    foreach (var package in packages.EnumerateArray())
                    {
                        if (topLevel) direct++; else transitive++;
                        if (rows.Count < limits.MaxItems) rows.Add(new { project = projectPath, framework = tfm, id = package.TryGetProperty("id", out var id) ? id.GetString() : null, direct = topLevel, requestedVersion = package.TryGetProperty("requestedVersion", out var requested) ? requested.GetString() : null, resolvedVersion = package.TryGetProperty("resolvedVersion", out var resolved) ? resolved.GetString() : null });
                    }
                }
            }
        }
        return new { schemaVersion = 1, kind = "dependency-inventory", direct, transitive, total = direct + transitive, packages = rows, truncated = direct + transitive > rows.Count, artifact = Path.GetFullPath(path) };
    }
    public static object TestResults(string path, OutputSettings limits)
    {
        var document = XDocument.Load(path, LoadOptions.None); var root = document.Root ?? throw new FormatException("Test results XML has no root element.");
        if (root.Name.LocalName == "TestRun") return Trx(path, root, limits);
        if (root.Name.LocalName is "testsuite" or "testsuites") return JUnit(path, root, limits);
        throw new FormatException("Unsupported test result XML. Expected TRX, JUnit testsuite, or JUnit testsuites.");
    }
    static object Trx(string path, XElement root, OutputSettings limits)
    {
        var results = root.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
        var outcomes = results.GroupBy(result => (string?)result.Attribute("outcome") ?? "Unknown", StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var failures = results.Where(result => !string.Equals((string?)result.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)).Take(limits.MaxItems).Select(result => new
        {
            test = (string?)result.Attribute("testName"),
            outcome = (string?)result.Attribute("outcome"),
            duration = (string?)result.Attribute("duration"),
            message = Output.Compact(result.Descendants().FirstOrDefault(element => element.Name.LocalName == "Message")?.Value ?? "", limits)
        }).ToArray();
        var duration = results.Select(result => TimeSpan.TryParse((string?)result.Attribute("duration"), CultureInfo.InvariantCulture, out var value) ? value : TimeSpan.Zero).Aggregate(TimeSpan.Zero, (total, value) => total + value);
        return new { schemaVersion = 1, kind = "test-results", format = "trx", total = results.Length, outcomes, durationMilliseconds = duration.TotalMilliseconds, failures, truncated = results.Count(result => !string.Equals((string?)result.Attribute("outcome"), "Passed", StringComparison.OrdinalIgnoreCase)) > failures.Length, artifact = Path.GetFullPath(path) };
    }
    static object JUnit(string path, XElement root, OutputSettings limits)
    {
        var cases = root.DescendantsAndSelf().Where(element => element.Name.LocalName == "testcase").ToArray();
        string Outcome(XElement test) => test.Elements().Any(element => element.Name.LocalName is "failure" or "error") ? "Failed" : test.Elements().Any(element => element.Name.LocalName == "skipped") ? "Skipped" : "Passed";
        var outcomes = cases.GroupBy(Outcome, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var failures = cases.Where(test => Outcome(test) == "Failed").Take(limits.MaxItems).Select(test => new { test = (string?)test.Attribute("name"), className = (string?)test.Attribute("classname"), outcome = "Failed", message = Output.Compact(test.Elements().First(element => element.Name.LocalName is "failure" or "error").Value, limits) }).ToArray();
        var seconds = cases.Sum(test => double.TryParse((string?)test.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0);
        return new { schemaVersion = 1, kind = "test-results", format = "junit", total = cases.Length, outcomes, durationMilliseconds = seconds * 1000, failures, truncated = outcomes.GetValueOrDefault("Failed") > failures.Length, artifact = Path.GetFullPath(path) };
    }
    public static object Coverage(string path, OutputSettings limits)
    {
        var document = XDocument.Load(path, LoadOptions.None); var root = document.Root ?? throw new FormatException("Coverage XML has no root element.");
        if (root.Name.LocalName == "coverage")
        {
            var linesValid = IntAttribute(root, "lines-valid"); var linesCovered = IntAttribute(root, "lines-covered"); var branchesValid = IntAttribute(root, "branches-valid"); var branchesCovered = IntAttribute(root, "branches-covered");
            var files = root.Descendants().Where(element => element.Name.LocalName == "class").Select(element => new { path = (string?)element.Attribute("filename"), lineRate = DoubleAttribute(element, "line-rate"), branchRate = DoubleAttribute(element, "branch-rate") }).OrderBy(item => item.path, StringComparer.Ordinal).Take(limits.MaxItems).ToArray();
            return CoverageResult(path, "cobertura", linesValid, linesCovered, branchesValid, branchesCovered, files);
        }
        if (root.Name.LocalName == "CoverageSession")
        {
            var summary = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "Summary") ?? throw new FormatException("OpenCover summary is missing.");
            var sequence = IntAttribute(summary, "numSequencePoints"); var visitedSequence = IntAttribute(summary, "visitedSequencePoints"); var branches = IntAttribute(summary, "numBranchPoints"); var visitedBranches = IntAttribute(summary, "visitedBranchPoints");
            var files = root.Descendants().Where(element => element.Name.LocalName == "File").Select(element => new { id = (string?)element.Attribute("uid"), path = (string?)element.Attribute("fullPath") }).Take(limits.MaxItems).ToArray();
            return CoverageResult(path, "opencover", sequence, visitedSequence, branches, visitedBranches, files);
        }
        throw new FormatException("Unsupported coverage XML. Expected Cobertura or OpenCover.");
    }
    static object CoverageResult(string path, string format, int linesValid, int linesCovered, int branchesValid, int branchesCovered, object files) => new
    {
        schemaVersion = 1,
        kind = "coverage-summary",
        format,
        lines = new { valid = linesValid, covered = linesCovered, percent = Percent(linesCovered, linesValid) },
        branches = new { valid = branchesValid, covered = branchesCovered, percent = Percent(branchesCovered, branchesValid) },
        files,
        artifact = Path.GetFullPath(path)
    };
    static int IntAttribute(XElement element, string name) => int.TryParse((string?)element.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    static double? DoubleAttribute(XElement element, string name) => double.TryParse((string?)element.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    static double? Percent(int covered, int valid) => valid == 0 ? null : Math.Round(covered * 100d / valid, 2, MidpointRounding.AwayFromZero);
    static string? Relative(string root, string? path)
    {
        if (path is null) return null; var full = Path.GetFullPath(path, root); var relative = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/'); return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? full : relative;
    }
}
public static class Audit
{
    public static int Count(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(kv => kv.Key == "vulnerabilities" && kv.Value is JsonArray a ? a.Count : Count(kv.Value)),
        JsonArray arr => arr.Sum(Count),
        _ => 0
    };
}
public static class Secrets
{
    const string Pattern = @"(?i)(?:Bearer\s+[A-Za-z0-9._~+/=-]+|(?:api[_-]?key|password|secret|token)\s*[=:]\s*[^\s,;]+|-----BEGIN[^\r\n]*PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{16,})";
    public static string Redact(string value)
    {
        value = JevCredentials.Redact(value);
        return Regex.Replace(value, Pattern, "[REDACTED]");
    }
    public static bool LooksSensitive(string value) => !string.Equals(value, Redact(value), StringComparison.Ordinal);
    public static string RedactJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new JsonException("Output JSON is empty.");
        RedactNode(node);
        return node.ToJsonString(AgentTool.Json);
    }
    static void RedactNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj.ToArray())
            {
                var name = Redact(entry.Key);
                if (name != entry.Key) { obj.Remove(entry.Key); obj[name] = entry.Value; }
                if (entry.Value is not null) RedactNode(entry.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array.ToArray())
                if (item is not null) RedactNode(item);
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            value.ReplaceWith(JsonValue.Create(Redact(text)));
    }
}

public static class JevCredentials
{
    public const string EnvironmentVariable = "TYPESAFE_API_KEY";
    public static string Status(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        return string.IsNullOrEmpty(environment(EnvironmentVariable)) ? "JEV credentials: unavailable" : "JEV credentials: configured";
    }
    internal static string? Read() => Environment.GetEnvironmentVariable(EnvironmentVariable);
    internal static bool IsConfigured(Func<string?> source) => !string.IsNullOrEmpty(source());
    internal static string Redact(string value)
    {
        var key = Read();
        return string.IsNullOrEmpty(key) ? value : value.Replace(key, "[REDACTED]", StringComparison.Ordinal);
    }
    internal static bool Contains(string value)
    {
        var key = Read();
        return !string.IsNullOrEmpty(key) && value.Contains(key, StringComparison.Ordinal);
    }
    internal static bool Authorize(HttpRequestMessage request, Func<string?> source)
    {
        var key = source();
        if (string.IsNullOrEmpty(key)) return false;
        try { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key); return true; }
        catch (FormatException) { return false; }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public record ToolkitSettings { public string[] OptionalTools { get; init; } = ["dotnet-trace", "dotnet-dump", "dotnet-counters", "dotnet-gcdump", "dotnet-monitor"]; public string[] EnabledIntegrations { get; init; } = []; public string Version { get; init; } = "0.0.0"; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public record OutputSettings { public int MaxLines { get; init; } = 12; public int MaxLineLength { get; init; } = 240; public int MaxItems { get; init; } = 30; public int MaxOutputChars { get; init; } = 16000; }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record HealthSettings
{
    public bool RequireNullable { get; init; } = true;
    public bool RequireCentralPackages { get; init; } = true;
    public bool RequireDeterministic { get; init; } = true;
    public bool RequireAnalyzers { get; init; } = true;
    public bool RequireLockFiles { get; init; }
    public string[] AllowedFrameworks { get; init; } = ["net10.0"];
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record JevCapabilityPolicy
{
    public bool Allowed { get; init; }
    public string[] Purposes { get; init; } = [];
    public int ExpectedCalls { get; init; }
    public int MaxCalls { get; init; }
    public bool DeterministicFirst { get; init; } = true;
    public int MaxInputBytes { get; init; } = 1;
    public int MaxCandidates { get; init; }
    public string Privacy { get; init; } = "not-applicable";
    public double MinConfidence { get; init; } = 1;
    public string Uncertainty { get; init; } = "review";
    public string GptEscalation { get; init; } = "normal";
    public void Validate(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability) || Purposes.Length == 0 || Purposes.Any(string.IsNullOrWhiteSpace) || ExpectedCalls < 0 || MaxCalls is < 0 or > 100 || ExpectedCalls > MaxCalls || MaxInputBytes is < 1 or > 65536 || MaxCandidates is < 0 or > 100 || MaxCandidates > MaxCalls || string.IsNullOrWhiteSpace(Privacy) || !double.IsFinite(MinConfidence) || MinConfidence is < 0 or > 1 || Uncertainty != "review" || GptEscalation is not ("normal" or "stronger") || Allowed != (MaxCalls > 0))
            throw new ArgumentException($"Invalid JEV capability policy: {capability}.");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record JevSettings
{
    public string Mode { get; init; } = "auto";
    public string ApiUrl { get; init; } = "https://api.typesafe.ai/v1/systemone";
    public string Model { get; init; } = "jev-latest";
    public int TimeoutSeconds { get; init; } = 15;
    public int MaxInputBytes { get; init; } = 16384;
    public int MaxCandidates { get; init; } = 25;
    public double IncludeThreshold { get; init; } = .70;
    public double ExcludeThreshold { get; init; } = .10;
    public double MinConfidence { get; init; } = .80;
    public int CacheHours { get; init; } = 24;
    public Dictionary<string, JevCapabilityPolicy> Capabilities { get; init; } = new(StringComparer.Ordinal);
    public void Validate()
    {
        if (Mode is not ("off" or "auto" or "required") || TimeoutSeconds is < 1 or > 120 || MaxInputBytes is < 1 or > 65536 || MaxCandidates is < 1 or > 100 || CacheHours is < 0 or > 720 || !double.IsFinite(IncludeThreshold) || !double.IsFinite(ExcludeThreshold) || !double.IsFinite(MinConfidence) || ExcludeThreshold < 0 || IncludeThreshold > 1 || ExcludeThreshold >= IncludeThreshold || MinConfidence is < 0 or > 1) throw new ArgumentException("Invalid JEV configuration.");
        if (!Uri.TryCreate(ApiUrl, UriKind.Absolute, out var url) || url.Scheme != "https" || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0) throw new ArgumentException("JEV endpoint must use HTTPS without credentials, query or fragment.");
        foreach (var (capability, policy) in Capabilities) policy.Validate(capability);
    }
}
public record Settings(JevSettings Jev, OutputSettings Output, HealthSettings Health, ToolkitSettings Toolkit)
{
    public static Settings Load(string toolkit, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        T Read<T>(string name) where T : new()
        {
            var node = JsonNode.Parse(File.ReadAllText(Path.Combine(toolkit, "config", name + ".json")))?.AsObject() ?? throw new ArgumentException($"Empty configuration: {name}.json");
            node.Remove("$schema");
            try { return node.Deserialize<T>(AgentTool.Json) ?? throw new ArgumentException($"Empty configuration: {name}.json"); }
            catch (JsonException e) { throw new ArgumentException($"Invalid configuration {name}.json: {e.Message}"); }
        }
        var jev = Read<JevSettings>("jev");
        jev = jev with { Mode = env("JEV_MODE") ?? jev.Mode, ApiUrl = env("TYPESAFE_API_URL") ?? jev.ApiUrl, Model = env("JEV_MODEL") ?? jev.Model, TimeoutSeconds = env("JEV_TIMEOUT_SECONDS") is { } timeout ? int.TryParse(timeout, out var seconds) ? seconds : throw new ArgumentException("Invalid JEV_TIMEOUT_SECONDS.") : jev.TimeoutSeconds };
        jev.Validate();
        var output = Read<OutputSettings>("output-limits");
        if (output.MaxLines is < 1 or > 100 || output.MaxLineLength is < 20 or > 2000 || output.MaxItems is < 1 or > 200 || output.MaxOutputChars is < 1024 or > 131072) throw new ArgumentException("Invalid output limits.");
        return new(jev, output, Read<HealthSettings>("repo-health"), Read<ToolkitSettings>("toolkit"));
    }
    public static Settings LoadFor(string toolkit, string command)
        => command is "install" or "update" or "uninstall" or "validate" or "release" or "results init" or "results new" or "results list" or "results latest" or "results context" or "results clean" or "upstream status" or "upstream update" or "upstream dotnet-skills"
            ? new(new(), new(), new(), new()) : Load(toolkit);
}

public sealed class JevClient
{
    readonly HttpClient http;
    readonly JevSettings settings;
    readonly string cacheDirectory;
    readonly Func<string?> credentialSource;
    public JevClient(HttpClient http, JevSettings settings, string cacheDirectory) : this(http, settings, cacheDirectory, JevCredentials.Read) { }
    internal JevClient(HttpClient http, JevSettings settings, string cacheDirectory, Func<string?> credentialSource)
    {
        this.http = http; this.settings = settings; this.cacheDirectory = cacheDirectory; this.credentialSource = credentialSource;
    }
    static readonly JevCapabilityPolicy DefaultTestPolicy = new() { Allowed = true, Purposes = ["candidate-relevance"], ExpectedCalls = 0, MaxCalls = 1, MaxInputBytes = 16384, MaxCandidates = 1, Privacy = "sanitized-bounded-text", MinConfidence = .8 };
    public static JsonObject Request(string kind, string state, string instructions, JsonNode? criteria, string model)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(instructions)) throw new ArgumentException("state and instructions are required.");
        if (kind is not ("noul" or "choice" or "score")) throw new ArgumentException("Unsupported judgment type.");
        if (kind == "choice" && (criteria is not JsonObject choice || choice.Count is < 2 or > 255)) throw new ArgumentException("Choice requires a criteria object with 2–255 named choices.");
        if (kind == "score" && (criteria is not JsonArray score || score.Count is < 2 or > 10)) throw new ArgumentException("Score requires 2–10 ordered criteria.");
        var question = new JsonObject { ["type"] = kind, ["instructions"] = instructions };
        if (criteria is not null) question["criteria"] = criteria.DeepClone();
        return new JsonObject { ["model"] = model, ["state"] = state, ["questions"] = new JsonObject { ["judgment"] = question } };
    }
    public static string Hash(JsonNode request, string endpoint)
    {
        static JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new KeyValuePair<string, JsonNode?>(x.Key, Canonical(x.Value)))),
            JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("jev-v1\n" + endpoint + "\n" + Canonical(request)!.ToJsonString())));
    }
    public static string Route(double probability, JevSettings settings) => !double.IsFinite(probability) || probability is < 0 or > 1 ? "REVIEW" : probability >= settings.IncludeThreshold ? "INCLUDE" : probability <= settings.ExcludeThreshold ? "EXCLUDE" : "REVIEW";
    public async Task<Result> Judge(JsonObject request, JevCapabilityPolicy? policy = null, string purpose = "candidate-relevance", string capability = "relevance")
    {
        policy ??= DefaultTestPolicy;
        settings.Validate();
        policy.Validate("invocation");
        if (!policy.Allowed || policy.MaxCalls < 1) return Decision("REVIEW", new { reason = "JEV capability is disallowed." }, policy, capability, purpose, null, false, 0, "GPT", "policy-disallowed");
        if (!policy.Purposes.Contains(purpose, StringComparer.Ordinal)) return Decision("REVIEW", new { reason = "JEV purpose is not allowed for this capability." }, policy, capability, "unrecognized", null, false, 0, "GPT", "purpose-disallowed");
        if (settings.Mode == "off") return Fallback("JEV disabled.", policy, capability, purpose, 0);
        if (!JevCredentials.IsConfigured(credentialSource)) return Fallback("JEV credentials unavailable.", policy, capability, purpose, 0);
        var body = request.ToJsonString();
        if (Encoding.UTF8.GetByteCount(body) > Math.Min(settings.MaxInputBytes, policy.MaxInputBytes) || Secrets.LooksSensitive(body)) return Fallback("Input too large or potentially sensitive.", policy, capability, purpose, 0);
        var hash = Hash(request, settings.ApiUrl); var cache = Path.Combine(cacheDirectory, hash + ".json");
        var remoteCalls = 0;
        try
        {
            SafeFiles.NoLinks(cache);
            if (settings.CacheHours > 0 && File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(settings.CacheHours))
            {
                try { return Parse(JsonNode.Parse(await File.ReadAllTextAsync(cache))!, request, true, policy, purpose, capability: capability); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException) { /* Invalid cache is ignored; no guessed decisions. */ }
            }
            using var message = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl);
            if (!JevCredentials.Authorize(message, credentialSource)) return Fallback("JEV credentials unavailable.", policy, capability, purpose, 0);
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            remoteCalls = 1;
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) return Fallback($"HTTP {(int)response.StatusCode}; response body withheld.", policy, capability, purpose, remoteCalls);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var memory = new MemoryStream(); var block = new byte[4096]; int count;
            while ((count = await stream.ReadAsync(block, timeout.Token)) > 0)
            { if (memory.Length + count > 65536) return Fallback("Response too large.", policy, capability, purpose, remoteCalls); await memory.WriteAsync(block.AsMemory(0, count), timeout.Token); }
            var json = JsonNode.Parse(memory.ToArray()) ?? throw new JsonException();
            var parsed = Parse(json, request, false, policy, purpose, remoteCalls, capability);
            if (settings.CacheHours > 0) { try { SafeFiles.Atomic(cache, CacheResponse(json, request).ToJsonString()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Cache is an optimization only. */ } }
            return parsed;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or IOException or UnauthorizedAccessException)
        { return Fallback("JEV unavailable or invalid response; no candidate discarded.", policy, capability, purpose, remoteCalls); }
    }
    Result Fallback(string reason, JevCapabilityPolicy policy, string capability, string purpose, int remoteCalls)
        => Decision("REVIEW", new { reason, requiredFailed = settings.Mode == "required" }, policy, capability, purpose, null, false, remoteCalls, "GPT", "fallback", settings.Mode == "required" ? 3 : 0);

    static Result Decision(string status, object judgment, JevCapabilityPolicy policy, string capability, string purpose, double? confidence, bool cached, int remoteCalls, string? fallbackTarget = null, string? escalationReason = null, int exitCode = 0)
    {
        var uncertain = status == "REVIEW";
        return new(status, new
        {
            judgment,
            instrumentation = new
            {
                schemaVersion = 1,
                capability,
                purpose,
                privacy = policy.Privacy,
                budget = new { expectedCalls = policy.ExpectedCalls, maxCalls = policy.MaxCalls },
                bounds = new { policy.DeterministicFirst, policy.MaxInputBytes, policy.MaxCandidates },
                counts = new { invocations = 1, remoteCalls, cacheHits = cached ? 1 : 0, fallbacks = fallbackTarget is null ? 0 : 1, escalations = uncertain ? 1 : 0 },
                confidence = new { reported = confidence, minimum = policy.MinConfidence, uncertain },
                fallback = new { used = fallbackTarget is not null, target = fallbackTarget },
                escalation = new { required = uncertain, target = uncertain ? policy.GptEscalation + "-gpt" : null, reason = escalationReason ?? (uncertain ? "uncertain-judgment" : null) },
                contextAvoidedBytes = 0,
                payloadCaptured = false
            }
        }, exitCode);
    }
    public static Result PolicyReview(JevCapabilityPolicy? policy, string capability, string purpose, string reason)
    {
        var knownPolicy = policy is not null;
        policy ??= new() { Allowed = false, Purposes = ["unrecognized"], ExpectedCalls = 0, MaxCalls = 0, MaxInputBytes = 1, MaxCandidates = 0, Privacy = "not-transmitted", MinConfidence = 1 };
        var safePurpose = knownPolicy && policy.Purposes.Contains(purpose, StringComparer.Ordinal) ? purpose : "unrecognized";
        return Decision("REVIEW", new { reason }, policy, knownPolicy ? capability : "unrecognized", safePurpose, null, false, 0, "GPT", "policy-gate");
    }
    static JsonObject CacheResponse(JsonNode response, JsonObject request)
    {
        var answer = response["answers"]!["judgment"]!; var kind = request["questions"]!["judgment"]!["type"]!.GetValue<string>();
        var cached = new JsonObject { ["type"] = kind };
        if (kind == "noul") cached["noul"] = answer["noul"]!.DeepClone();
        else
        {
            cached["confidence"] = answer["confidence"]!.DeepClone();
            cached["probabilities"] = answer["probabilities"]!.DeepClone();
            cached[kind] = answer[kind]!.DeepClone();
            if (kind == "score") cached["legend"] = answer["legend"]!.DeepClone();
        }
        return new JsonObject { ["answers"] = new JsonObject { ["judgment"] = cached } };
    }
    public Result Parse(JsonNode response, JsonObject request, bool cached, JevCapabilityPolicy? policy = null, string purpose = "candidate-relevance", int remoteCalls = 0, string capability = "relevance")
    {
        policy ??= DefaultTestPolicy;
        var q = request["questions"]!["judgment"]!; var kind = q["type"]!.GetValue<string>();
        var a = response["answers"]?["judgment"] ?? throw new JsonException("Missing answer.");
        if (a["type"]?.GetValue<string>() != kind) throw new JsonException("Wrong answer type.");
        double Number(string name, double max = 1)
        {
            var n = a[name]?.GetValue<double>() ?? throw new JsonException("Missing numeric answer.");
            if (!double.IsFinite(n) || n < 0 || n > max) throw new JsonException("Invalid numeric range."); return n;
        }
        if (kind == "noul") { var n = Number("noul"); var status = Route(n, settings); return Decision(status, new { probability = n, cached }, policy, capability, purpose, null, cached, remoteCalls, null, status == "REVIEW" ? "uncertain-judgment" : null); }
        var confidence = Number("confidence"); var probabilities = a["probabilities"]?.AsObject() ?? throw new JsonException("Missing probability distribution.");
        var expected = kind == "choice" ? q["criteria"]!.AsObject().Select(x => x.Key).ToArray() : Enumerable.Range(0, q["criteria"]!.AsArray().Count).Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (!expected.Order(StringComparer.Ordinal).SequenceEqual(probabilities.Select(x => x.Key).Order(StringComparer.Ordinal))) throw new JsonException("Wrong probability labels.");
        var values = probabilities.Select(x => x.Value is JsonValue value && value.TryGetValue<double>(out var probability) ? probability : throw new JsonException("Invalid probability value.")).ToArray();
        if (values.Any(x => !double.IsFinite(x) || x is < 0 or > 1) || Math.Abs(values.Sum() - 1) > .01) throw new JsonException("Invalid probability distribution.");
        object value;
        if (kind == "choice")
        {
            var choice = a["choice"]?.GetValue<string>() ?? throw new JsonException("Missing choice.");
            if (!expected.Contains(choice) || probabilities[choice] is not JsonValue choiceProbability || !choiceProbability.TryGetValue<double>(out var selected) || selected + .001 < values.Max()) throw new JsonException("Invalid choice.");
            value = choice;
        }
        else
        {
            var score = Number("score", expected.Length - 1);
            var legend = a["legend"]?.AsObject() ?? throw new JsonException("Missing score legend.");
            if (!expected.Order(StringComparer.Ordinal).SequenceEqual(legend.Select(x => x.Key).Order(StringComparer.Ordinal)) || legend.Any(x => x.Value is not JsonValue value || !value.TryGetValue<string>(out _))) throw new JsonException("Invalid score legend.");
            var weighted = probabilities.Sum(x => int.Parse(x.Key, CultureInfo.InvariantCulture) * (x.Value is JsonValue probability && probability.TryGetValue<double>(out var number) ? number : throw new JsonException("Invalid probability value.")));
            if (Math.Abs(score - weighted) > .02) throw new JsonException("Score and distribution disagree.");
            value = score;
        }
        var result = confidence >= policy.MinConfidence ? "ACCEPT" : "REVIEW";
        return Decision(result, new { value, confidence, cached }, policy, capability, purpose, confidence, cached, remoteCalls, null, result == "REVIEW" ? "low-confidence" : null);
    }
}

public record InstallEntry(string Destination, string Source, bool Directory);
public record InstallManifest(string Toolkit, string Home, string CodexHome, List<InstallEntry> Entries);
public static class Installer
{
    static string ManifestPath(string codex) => Path.Combine(codex, "sdeveng-install.json");
    static string LegacyManifestPath(string codex) => Path.Combine(codex, "codex-toolkit-install.json");
    static string? LinkTarget(InstallEntry entry)
    {
        FileSystemInfo info = entry.Directory ? new DirectoryInfo(entry.Destination) : new FileInfo(entry.Destination);
        try
        {
            if (info.ResolveLinkTarget(false) is { } resolved) return resolved.FullName;
        }
        catch (IOException) { }
        var target = info.LinkTarget;
        return target is null ? null : Path.GetFullPath(target, Path.GetDirectoryName(entry.Destination)!);
    }
    static bool Exists(InstallEntry entry) => File.Exists(entry.Destination) || Directory.Exists(entry.Destination) || LinkTarget(entry) is not null;
    static bool Matches(InstallEntry e)
    {
        var target = LinkTarget(e);
        return target is not null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(e.Source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    static void DeleteLink(InstallEntry entry)
    {
        if (entry.Directory && (OperatingSystem.IsWindows() || Directory.Exists(entry.Destination))) Directory.Delete(entry.Destination);
        else File.Delete(entry.Destination);
    }
    static List<InstallEntry> Plan(string toolkit, string home, string codex, bool bin)
    {
        if (bin && OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("--bin is not supported on Windows; invoke `dotnet <toolkit>/tools/AgentTool.cs` directly.");
        var entries = new List<InstallEntry> { new(Path.Combine(codex, "AGENTS.md"), Path.Combine(toolkit, "global/AGENTS.md"), false) };
        entries.AddRange(Directory.GetFiles(Path.Combine(toolkit, "agents"), "*.toml").Select(s => new InstallEntry(Path.Combine(codex, "agents", Path.GetFileName(s)), s, false)));
        entries.AddRange(Directory.GetDirectories(Path.Combine(toolkit, "plugins/sdeveng/skills")).Select(s => new InstallEntry(Path.Combine(home, ".agents/skills", Path.GetFileName(s)), s, true)));
        if (bin)
        {
            entries.Add(new(Path.Combine(home, ".local/bin/sdeveng"), Path.Combine(toolkit, "tools/AgentTool.cs"), false));
            entries.Add(new(Path.Combine(home, ".local/bin/codex-agent-tool"), Path.Combine(toolkit, "tools/AgentTool.cs"), false));
        }
        return entries;
    }
    static bool IsOwnedShape(InstallEntry entry, string toolkit, string home, string codex)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        static bool Under(string path, string root, StringComparison comparison) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
        var destination = Path.GetFullPath(entry.Destination); var source = Path.GetFullPath(entry.Source);
        if (!Under(source, toolkit, comparison)) return false;
        return destination == Path.Combine(codex, "AGENTS.md") && source == Path.Combine(toolkit, "global", "AGENTS.md") && !entry.Directory
            || Under(destination, Path.Combine(codex, "agents"), comparison) && destination.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) && !entry.Directory
            || Under(destination, Path.Combine(home, ".agents", "skills"), comparison) && entry.Directory
            || (destination == Path.Combine(home, ".local", "bin", "sdeveng") || destination == Path.Combine(home, ".local", "bin", "codex-agent-tool")) && source == Path.Combine(toolkit, "tools", "AgentTool.cs") && !entry.Directory;
    }
    static InstallManifest? Read(string path)
    {
        SafeFiles.NoLinks(path);
        return File.Exists(path) ? JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(path), AgentTool.Json) ?? throw new IOException("Invalid installation manifest.") : null;
    }
    public static object Inspect(string codex)
    {
        var manifest = Read(ManifestPath(codex));
        var legacy = manifest is null ? Read(LegacyManifestPath(codex)) : null;
        return manifest is null && legacy is null ? new { installed = false } : new { installed = true, migrationRequired = legacy is not null, entries = (manifest ?? legacy)!.Entries.Select(e => new { e.Destination, healthy = Matches(e) && (File.Exists(e.Source) || Directory.Exists(e.Source)) }) };
    }
    public static Result Run(string toolkit, string home, string? codexHome, string command, bool dryRun, bool bin)
    {
        toolkit = Path.GetFullPath(toolkit); home = Path.GetFullPath(home); var codex = Path.GetFullPath(codexHome ?? Path.Combine(home, ".codex"));
        SafeFiles.NoLinks(codex);
        var manifestPath = ManifestPath(codex);
        var manifest = Read(manifestPath);
        var legacyManifest = manifest is null ? Read(LegacyManifestPath(codex)) : null;
        var migratingLegacy = legacyManifest is not null;
        manifest ??= legacyManifest;
        if (manifest is not null && (manifest.Toolkit != toolkit || manifest.Home != home || manifest.CodexHome != codex)) throw new IOException("Installation belongs to a different checkout/home; use that checkout to uninstall first.");
        if (manifest is not null && manifest.Entries.Any(e => !IsOwnedShape(e, toolkit, home, codex))) throw new IOException("Ownership manifest contains unexpected paths; no changes made.");
        var plan = command == "uninstall" ? [] : Plan(toolkit, home, codex, bin || manifest?.Entries.Any(x => x.Destination == Path.Combine(home, ".local/bin/codex-agent-tool")) == true);
        var removals = command == "uninstall" ? manifest?.Entries.ToList() ?? [] : command == "update" || migratingLegacy ? manifest?.Entries.Except(plan).ToList() ?? [] : [];
        var conflicts = plan.Where(e => Exists(e) && !(manifest?.Entries.Any(existing => existing.Destination == e.Destination && Matches(existing)) == true)).Select(e => e.Destination).ToArray();
        if (command != "uninstall" && conflicts.Length > 0) return new("conflict", new { conflicts, changed = false }, 1);
        foreach (var entry in plan.Concat(removals)) SafeFiles.NoLinks(Path.GetDirectoryName(entry.Destination)!);
        var preservedRemovals = removals.Where(e => Exists(e) && !Matches(e)).Select(e => e.Destination).ToArray();
        if (dryRun) return Result.Ok(new { dryRun, command, migratingLegacy, plan, removals, preserved = conflicts.Concat(preservedRemovals) });
        if (plan.Count == 0 && removals.Count == 0) return Result.Ok(new { command, changed = 0 });
        Directory.CreateDirectory(codex);
        var lockPath = Path.Combine(codex, "sdeveng-install.lock"); SafeFiles.NoLinks(lockPath);
        using var installLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        // Re-read under the lock so stale concurrent plans cannot overwrite ownership records.
        var current = Read(migratingLegacy ? LegacyManifestPath(codex) : manifestPath);
        if (JsonSerializer.Serialize(current, AgentTool.Json) != JsonSerializer.Serialize(manifest, AgentTool.Json)) throw new IOException("Installation changed concurrently; rerun command.");
        manifest ??= new(toolkit, home, codex, []);
        var changed = new List<string>(); var preserved = new List<string>();
        foreach (var entry in removals)
        {
            if (Matches(entry)) { DeleteLink(entry); changed.Add(entry.Destination); }
            else if (Exists(entry)) preserved.Add(entry.Destination);
            manifest.Entries.Remove(entry);
            SafeFiles.Atomic(manifestPath, JsonSerializer.Serialize(manifest, AgentTool.Json));
        }
        foreach (var entry in plan)
        {
            if (manifest.Entries.Contains(entry) && Matches(entry)) continue;
            if (Exists(entry)) throw new IOException("Destination appeared during installation; rerun to inspect conflicts.");
            Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
            if (entry.Directory) Directory.CreateSymbolicLink(entry.Destination, entry.Source); else File.CreateSymbolicLink(entry.Destination, entry.Source);
            manifest.Entries.Remove(entry);
            manifest.Entries.Add(entry);
            try { SafeFiles.Atomic(manifestPath, JsonSerializer.Serialize(manifest, AgentTool.Json)); }
            catch { if (Matches(entry)) DeleteLink(entry); throw; }
            changed.Add(entry.Destination);
        }
        if (migratingLegacy || command == "uninstall") File.Delete(LegacyManifestPath(codex));
        if (command == "uninstall") File.Delete(manifestPath);
        return Result.Ok(new { command, migratingLegacy, changed, preserved, note = "Only owned links changed; user replacements are preserved. Empty parent directories remain." });
    }
}

public static class RuntimeReferences
{
    static readonly Regex MarkdownLink = new(@"\]\(([^)]+)\)", RegexOptions.Compiled);
    static readonly Regex SkillReference = new(@"(?<![A-Za-z0-9_./-])(references/[A-Za-z0-9_./-]+\.md)\b", RegexOptions.Compiled);

    public static string[] Missing(string root)
    {
        var plugin = Path.Combine(root, "plugins", "sdeveng");
        var skills = Path.Combine(plugin, "skills");
        if (!Directory.Exists(skills)) return ["Missing runtime skills directory: plugins/sdeveng/skills"];
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var markdown = SafeFiles.Enumerate(plugin).Where(file => file.EndsWith(".md", StringComparison.Ordinal)
            && (Path.GetFileName(file) == "SKILL.md" || file.Contains(Path.DirectorySeparatorChar + "references" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        foreach (var file in markdown)
        {
            var text = File.ReadAllText(file);
            if (Path.GetFileName(file) == "SKILL.md")
                foreach (Match match in SkillReference.Matches(text)) Check(file, match.Groups[1].Value, root, errors);
            foreach (Match match in MarkdownLink.Matches(text))
            {
                var target = match.Groups[1].Value.Split('#')[0].Trim('<', '>');
                if (target.Length == 0 || target.Contains("://", StringComparison.Ordinal) || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                Check(file, target, root, errors);
            }
        }
        return errors.Order(StringComparer.Ordinal).ToArray();
    }

    static void Check(string source, string target, string root, HashSet<string> errors)
    {
        var resolved = Path.GetFullPath(target, Path.GetDirectoryName(source)!);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            errors.Add($"Missing runtime reference from {Path.GetRelativePath(root, source)}: {target}");
    }
}

public static class PluginManifests
{
    public const string PortableSchema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json";
    public const string PluginName = "sdeveng-engineering-toolkit";

    public static JsonObject CompatibilityFrom(JsonObject portable) => new()
    {
        ["name"] = portable["name"]?.DeepClone(),
        ["version"] = portable["version"]?.DeepClone(),
        ["description"] = portable["description"]?.DeepClone(),
        ["skills"] = "./skills/",
        ["interface"] = portable["extensions"]?["com.openai"]?["interface"]?.DeepClone()
    };

    public static void Validate(string root, List<string> errors)
    {
        var pluginRoot = Path.Combine(root, "plugins", "sdeveng");
        try
        {
            var portable = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginRoot, "plugin.json"))) as JsonObject;
            var compatibility = JsonNode.Parse(File.ReadAllText(Path.Combine(pluginRoot, ".codex-plugin", "plugin.json"))) as JsonObject;
            if (portable is null || compatibility is null) { errors.Add("Plugin manifests must be JSON objects."); return; }
            if (portable["$schema"]?.GetValue<string>() != PortableSchema || portable["name"]?.GetValue<string>() != PluginName)
                errors.Add("Portable plugin schema or stable identity mismatch.");
            if (portable["author"]?["name"]?.GetValue<string>() != "SimplexiDev Engineering Toolkit"
                || portable["author"]?["url"]?.GetValue<string>() != "https://github.com/simplexidev"
                || portable["homepage"]?.GetValue<string>() != "https://github.com/simplexidev/sdeveng"
                || portable["repository"]?.GetValue<string>() != "https://github.com/simplexidev/sdeveng")
                errors.Add("Portable plugin publisher metadata mismatch.");
            if (portable["extensions"]?["com.openai"]?["interface"] is not JsonObject)
                errors.Add("Portable plugin OpenAI extension metadata missing.");
            if (!JsonNode.DeepEquals(compatibility, CompatibilityFrom(portable)))
                errors.Add("Codex compatibility manifest must be derived from the portable manifest.");
            if (!Directory.Exists(Path.Combine(pluginRoot, "skills")) || !Directory.GetDirectories(Path.Combine(pluginRoot, "skills")).Any())
                errors.Add("Portable plugin skill discovery directory is missing or empty.");
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            errors.Add("Unable to validate plugin manifests: " + e.Message);
        }
    }
}

public static class Validation
{
    public static Result Run(string root)
    {
        var errors = new List<string>(); int parsed = 0;
        foreach (var file in SafeFiles.Enumerate(root))
        {
            if (new FileInfo(file).Length == 0) errors.Add($"Empty file: {Path.GetRelativePath(root, file)}");
            if (Path.GetExtension(file) == ".json")
                try { JsonNode.Parse(File.ReadAllText(file)); parsed++; } catch (JsonException) { errors.Add($"Invalid JSON: {file}"); }
        }
        foreach (var file in Directory.GetFiles(Path.Combine(root, "config"), "*.json").Concat(Directory.GetFiles(Path.Combine(root, "upstream"), "*.json")))
        {
            var instance = JsonNode.Parse(File.ReadAllText(file))!;
            var schemaReference = instance["$schema"]?.GetValue<string>();
            if (schemaReference is null) { errors.Add($"Missing schema: {Path.GetRelativePath(root, file)}"); continue; }
            if (!Uri.TryCreate(schemaReference, UriKind.Absolute, out _))
            {
                var schemaPath = Path.GetFullPath(schemaReference, Path.GetDirectoryName(file)!);
                if (!File.Exists(schemaPath)) errors.Add($"Missing schema file: {Path.GetRelativePath(root, file)}");
                else ValidateSchema(instance, JsonNode.Parse(File.ReadAllText(schemaPath))!, Path.GetRelativePath(root, file), errors);
            }
        }
        foreach (var skill in Directory.GetDirectories(Path.Combine(root, "plugins/sdeveng/skills")))
        {
            var path = Path.Combine(skill, "SKILL.md");
            if (!File.Exists(path)) { errors.Add($"Missing skill: {skill}"); continue; }
            var text = File.ReadAllText(path);
            if (!text.StartsWith("---\n", StringComparison.Ordinal) || !text.Contains("\nname: " + Path.GetFileName(skill) + "\n", StringComparison.Ordinal) || !text.Contains("\ndescription: ", StringComparison.Ordinal)) errors.Add($"Invalid skill frontmatter: {skill}");
            if (!File.Exists(Path.Combine(root, "evals", Path.GetFileName(skill), "eval.yaml"))) errors.Add($"Missing evaluation: {skill}");
            var ui = Path.Combine(skill, "agents", "openai.yaml");
            if (!File.Exists(ui)) errors.Add($"Missing skill UI metadata: {skill}");
            else
            {
                var uiText = File.ReadAllText(ui); var name = Path.GetFileName(skill);
                if (!Regex.IsMatch(uiText, @"(?m)^interface:\s*$") || !Regex.IsMatch(uiText, @"(?m)^\s+display_name:\s+\S") || !Regex.IsMatch(uiText, @"(?m)^\s+short_description:\s+\S") || !uiText.Contains("$" + name, StringComparison.Ordinal)) errors.Add($"Invalid skill UI metadata: {skill}");
            }
        }
        errors.AddRange(RuntimeReferences.Missing(root));
        var nativeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var native in Directory.GetFiles(Path.Combine(root, "agents"), "*.toml"))
        {
            var nativeText = File.ReadAllText(native);
            var name = Regex.Match(nativeText, "(?m)^name\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            var model = Regex.Match(nativeText, "(?m)^model\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            var effort = Regex.Match(nativeText, "(?m)^model_reasoning_effort\\s*=\\s*\\\"([^\\\"]+)\\\"$").Groups[1].Value;
            if (name.Length == 0 || !nativeNames.Add(name) || model is not ("gpt-5.6-luna" or "gpt-5.6-terra" or "gpt-5.6-sol" or "gpt-6-astra" or "gpt-5.5") || effort is not ("low" or "medium" or "high" or "xhigh" or "max" or "ultra")) errors.Add($"Invalid native agent metadata: {native}");
        }
        PluginManifests.Validate(root, errors);
        var version = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/toolkit.json")))?["version"]?.GetValue<string>();
        var portableVersion = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/plugin.json")))?["version"]?.GetValue<string>();
        var compatibilityVersion = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugins/sdeveng/.codex-plugin/plugin.json")))?["version"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(version) || portableVersion != version || compatibilityVersion != version) errors.Add("Toolkit and plugin manifest versions must match.");
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".agents/plugins/marketplace.json")))!;
        if (marketplace["plugins"]?[0]?["source"]?["path"]?.GetValue<string>() != "./plugins/sdeveng"
            || marketplace["plugins"]?[0]?["name"]?.GetValue<string>() != PluginManifests.PluginName) errors.Add("Marketplace source path or plugin identity mismatch.");
        var ecosystem = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config/ecosystem.json")))!;
        if (ecosystem["pluginPath"]?.GetValue<string>() != "plugins/sdeveng"
            || ecosystem["templatePath"]?.GetValue<string>() != "templates/project"
            || ecosystem["siblingRepositoriesAreRuntimeDependencies"]?.GetValue<bool>() != false
            || Directory.GetDirectories(Path.Combine(root, "plugins")).Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Count() != 1
            || Directory.GetDirectories(Path.Combine(root, "templates")).Select(Path.GetFileName).Where(x => !string.IsNullOrEmpty(x)).Count() != 1)
            errors.Add("Ecosystem plugin/template topology mismatch.");
        try { Settings.Load(root, _ => null); } catch (ArgumentException e) { errors.Add(e.Message); }
        return new(errors.Count == 0 ? "ok" : "failed", new { jsonFiles = parsed, errors, note = "Structural validation includes configuration schemas, runtime references and release identity." }, errors.Count == 0 ? 0 : 1);
    }
    static void ValidateSchema(JsonNode instance, JsonNode schema, string file, List<string> errors)
    {
        if (schema["type"]?.GetValue<string>() == "object")
        {
            if (instance is not JsonObject value) { errors.Add($"Schema type mismatch: {file} must be object."); return; }
            var properties = schema["properties"]?.AsObject() ?? [];
            foreach (var required in schema["required"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []) if (!value.ContainsKey(required)) errors.Add($"Schema required key missing: {file}:{required}");
            var additional = schema["additionalProperties"];
            if (additional is JsonValue additionalValue && additionalValue.TryGetValue<bool>(out var allowed) && !allowed)
                foreach (var key in value.Select(x => x.Key)) if (!properties.ContainsKey(key)) errors.Add($"Schema unknown key: {file}:{key}");
            foreach (var property in properties) if (value[property.Key] is { } child) ValidateSchema(child, property.Value!, file + ":" + property.Key, errors);
            if (additional is JsonObject additionalSchema)
                foreach (var property in value.Where(x => !properties.ContainsKey(x.Key) && x.Value is not null)) ValidateSchema(property.Value!, additionalSchema, file + ":" + property.Key, errors);
        }
        else if (schema["type"]?.GetValue<string>() == "array")
        {
            if (instance is not JsonArray values) { errors.Add($"Schema type mismatch: {file} must be array."); return; }
            foreach (var value in values.Where(x => x is not null)) ValidateSchema(value!, schema["items"]!, file + "[]", errors);
        }
        else if (schema["type"]?.GetValue<string>() is { } type && !MatchesType(instance, type)) errors.Add($"Schema type mismatch: {file} must be {type}.");
    }
    static bool MatchesType(JsonNode node, string type) => type switch { "string" => node is JsonValue v && v.TryGetValue<string>(out _), "boolean" => node is JsonValue v && v.TryGetValue<bool>(out _), "integer" => node is JsonValue v && v.TryGetValue<int>(out _), "number" => node is JsonValue v && v.TryGetValue<double>(out _), _ => true };
}

public static class Evaluation
{
    public static Result Run(string toolkit, string? skill, string? resultsPath)
    {
        // Evaluation documents use JSON syntax, a strict YAML 1.2 subset, to stay dependency-free.
        var cases = Directory.GetFiles(Path.Combine(toolkit, "evals"), "eval.yaml", SearchOption.AllDirectories).Where(p => skill is null || Path.GetFileName(Path.GetDirectoryName(p)) == skill).ToArray();
        if (cases.Length == 0) throw new ArgumentException("No matching evaluation.");
        var runs = resultsPath is null ? null : JsonNode.Parse(File.ReadAllText(resultsPath))?.AsArray();
        var outcomes = new List<object>(); bool failed = false;
        foreach (var path in cases)
        {
            var specification = JsonNode.Parse(File.ReadAllText(path))!;
            var name = specification["skill"]!.GetValue<string>();
            if (runs is null)
            {
                var fixturePath = Path.GetFullPath(specification["fixture"]!.GetValue<string>(), Path.GetDirectoryName(path)!);
                var fixture = JsonNode.Parse(File.ReadAllText(fixturePath));
                var ok = fixture is JsonObject && specification["scenario"]?.GetValue<string>().Length > 10 && specification["expected"]?.GetValue<string>().Length > 10 && specification["safety"]?.GetValue<string>().Length > 10;
                failed |= !ok;
                outcomes.Add(new { skill = name, passed = ok, kind = "scenario/fixture integrity", agentBehaviorMeasured = false });
                continue;
            }
            var run = runs.SingleOrDefault(r => r?["skill"]?.GetValue<string>() == name);
            var failures = new List<string>();
            if (run is null) failures.Add("Missing run.");
            else
            {
                if (run["success"]?.GetValue<bool>() != true) failures.Add("Correctness failed.");
                if (run["expectedSatisfied"]?.GetValue<bool>() != true || run["safetySatisfied"]?.GetValue<bool>() != true) failures.Add("Trusted grader must attest expected and safety behavior.");
                if (string.IsNullOrWhiteSpace(run["revision"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(run["model"]?.GetValue<string>()) || string.IsNullOrWhiteSpace(run["promptHash"]?.GetValue<string>())) failures.Add("Measured run requires revision, model and promptHash provenance.");
                foreach (var budget in specification["budgets"]!.AsObject())
                    if (run[budget.Key] is null || run[budget.Key]!.GetValue<double>() < 0 || run[budget.Key]!.GetValue<double>() > budget.Value!.GetValue<double>()) failures.Add($"Missing, invalid or exceeded {budget.Key}.");
                if (run["baseline"] is not JsonObject baseline || baseline["success"]?.GetValue<bool>() != true) failures.Add("Successful baseline required for comparison.");
                else if (baseline["tokens"] is null || baseline["tokens"]!.GetValue<double>() <= 0) failures.Add("Valid baseline token measurement required.");
                else if (run["tokens"] is not null && run["tokens"]!.GetValue<double>() > baseline["tokens"]!.GetValue<double>() * 1.10) failures.Add("Token use regressed more than 10% against baseline.");
            }
            failed |= failures.Count > 0;
            outcomes.Add(new { skill = name, passed = failures.Count == 0, failures });
        }
        return new(failed ? "failed" : "ok", new { outcomes, note = runs is null ? "Offline fixture integrity only; it does not claim agent behavior. Supply trusted, attested --results for measured regression gates." : "Attested measured runs checked against correctness, safety, provenance and regression budgets." }, failed ? 1 : 0);
    }
}
