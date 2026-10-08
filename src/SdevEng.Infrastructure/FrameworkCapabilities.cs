using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SdevEng;

/// <summary>Shared static framework detection seam. Additional detectors extend this projection.</summary>
public static class FrameworkCapabilities
{
    public static FrameworkCapabilityFact[] Detect(string? outputType, Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packageVersions = null,
        IReadOnlyDictionary<string, string?>? projectProperties = null)
        => DetectConsole(outputType, compilation, packageVersions)
            .Concat(DetectChannels(compilation))
            .Concat(DetectLocalization(compilation))
            .Concat(DetectHosting(compilation, packageVersions))
            .Concat(DetectHttpClients(compilation, packageVersions))
            .Concat(DetectHttpResilience(compilation, packageVersions))
            .Concat(DetectCaching(compilation, packageVersions))
            .Concat(DetectTelemetry(compilation, packageVersions))
            .Concat(DetectTelemetryComposition(compilation, packageVersions))
            .Concat(DetectPublishSettings(projectProperties))
            .Concat(DetectReflection(compilation))
            .Concat(DetectMicrosoftExtensionsAI(compilation, packageVersions))
            .Concat(DetectAvalonia(compilation, packageVersions)).ToArray();

    static FrameworkCapabilityFact[] DetectAvalonia(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        const string packageId = "Avalonia";
        var version = packages?.GetValueOrDefault(packageId) ?? compilation?.ReferencedAssemblyNames
            .FirstOrDefault(item => item.Name == packageId)?.Version.ToString();
        var complete = compilation is not null && compilation.Language == LanguageNames.CSharp &&
            !compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error);
        if (version is null)
            return [new("avalonia", complete ? "absent" : "unknown",
                complete ? "resolved-package-not-present" : "package-evidence-unavailable")];

        if (!System.Version.TryParse(version, out _))
            return [new("avalonia", "unknown", "unsupported-package-version", version)];

        // The generalized provenance registry currently marks Avalonia majors unknown;
        // do not treat package presence as verified API compatibility.
        return [new("avalonia", "unknown", "unsupported-package-version", version)];
    }

    static FrameworkCapabilityFact[] DetectMicrosoftExtensionsAI(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        const string ai = "Microsoft.Extensions.AI";
        const string abstractions = "Microsoft.Extensions.AI.Abstractions";
        string? VersionOf(string name) => packages?.GetValueOrDefault(name) ?? compilation?.ReferencedAssemblyNames
            .FirstOrDefault(item => item.Name == name)?.Version.ToString();
        var versions = new[] { VersionOf(ai), VersionOf(abstractions) }.Where(value => value is not null).ToArray();
        var version = packages?.GetValueOrDefault(ai) ?? packages?.GetValueOrDefault(abstractions) ??
            VersionOf(ai) ?? VersionOf(abstractions);
        var complete = compilation is not null && compilation.Language == LanguageNames.CSharp &&
            !compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error);
        var unsupported = versions.Any(value => !System.Version.TryParse(value, out var parsed) || parsed.Major != 10);
        var package = new FrameworkCapabilityFact("microsoft-extensions-ai",
            version is null ? complete ? "absent" : "unknown" : unsupported ? "unknown" : "detected",
            version is null ? complete ? "resolved-package-not-present" : "package-evidence-unavailable" :
                unsupported ? "unsupported-package-version" : "resolved-package", version);
        FrameworkCapabilityFact Registration(string status, string evidence, FrameworkCapabilityLocation[]? locations = null)
            => new("chat-client-registration", status, evidence, version, locations);
        FrameworkCapabilityFact Composition(string status, string evidence, FrameworkCapabilityLocation[]? locations = null)
            => new("chat-client-composition", status, evidence, version, locations);
        FrameworkCapabilityFact Tools(string status, string evidence, FrameworkCapabilityLocation[]? locations = null)
            => new("chat-tool-registration", status, evidence, version, locations);
        if (!complete) return [package, Registration("unknown", "unavailable-or-incomplete-csharp-compilation"),
            Composition("unknown", "unavailable-or-incomplete-csharp-compilation"), Tools("unknown", "unavailable-or-incomplete-csharp-compilation")];
        if (unsupported) return [package, Registration("unknown", "unsupported-package-version"),
            Composition("unknown", "unsupported-package-version"), Tools("unknown", "unsupported-package-version")];
        var chat = compilation!.GetTypeByMetadataName("Microsoft.Extensions.AI.IChatClient");
        var locations = new List<FrameworkCapabilityLocation>();
        var compositionLocations = new List<FrameworkCapabilityLocation>();
        var toolLocations = new List<FrameworkCapabilityLocation>();
        var unknownTools = false;
        bool IsTools(ISymbol? symbol) => symbol is IPropertySymbol property &&
            property.Name == "Tools" && property.ContainingType.ToDisplayString() == "Microsoft.Extensions.AI.ChatOptions" &&
            property.ContainingAssembly.Name == abstractions;
        var delegating = compilation.GetTypeByMetadataName("Microsoft.Extensions.AI.DelegatingChatClient");
        var indirect = false;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) { indirect = true; continue; }
                if (method.ReducedFrom is { } original)
                    method = method.IsGenericMethod ? original.Construct(method.TypeArguments.ToArray()) : original;
                if (call.Expression is MemberAccessExpressionSyntax access &&
                    IsTools(model.GetSymbolInfo(access.Expression).Symbol) && method.Name == "Add" &&
                    method.ContainingType.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.ICollection<T>")
                {
                    var span = call.GetLocation().GetLineSpan();
                    toolLocations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                }
                if (chat?.ContainingAssembly.Name == abstractions && method.ContainingAssembly.Name == ai &&
                    ((method.ContainingType.ToDisplayString() == "Microsoft.Extensions.AI.ChatClientBuilder" && method.Name == "Use") ||
                     (method.IsExtensionMethod && method.Name.StartsWith("Use", StringComparison.Ordinal) &&
                      method.Parameters.FirstOrDefault()?.Type.ToDisplayString() == "Microsoft.Extensions.AI.ChatClientBuilder")))
                {
                    var span = call.GetLocation().GetLineSpan();
                    compositionLocations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                }
                var registration = chat?.ContainingAssembly.Name == abstractions &&
                    ((method.ContainingAssembly.Name == ai &&
                      method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ChatClientBuilderServiceCollectionExtensions" &&
                      method.Name is "AddChatClient" or "AddKeyedChatClient") ||
                     (method.ContainingAssembly.Name == "Microsoft.Extensions.DependencyInjection.Abstractions" &&
                      method.ContainingType.ToDisplayString() is "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions" or
                          "Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions" &&
                      method.Name is "AddSingleton" or "AddScoped" or "AddTransient" or "TryAddSingleton" or "TryAddScoped" or
                          "TryAddTransient" or "AddKeyedSingleton" or "AddKeyedScoped" or "AddKeyedTransient" &&
                      (SymbolEqualityComparer.Default.Equals(method.TypeArguments.FirstOrDefault(), chat) ||
                       model.GetOperation(call) is Microsoft.CodeAnalysis.Operations.IInvocationOperation operation &&
                       operation.Arguments.Any(argument => argument.Parameter?.Name is "serviceType" or "service" &&
                           argument.Value is Microsoft.CodeAnalysis.Operations.ITypeOfOperation typeOf &&
                           SymbolEqualityComparer.Default.Equals(typeOf.TypeOperand, chat)))));
                if (registration)
                {
                    var span = call.GetLocation().GetLineSpan();
                    locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                }
                else if (method.Locations.Any(item => item.IsInSource) || method.ContainingType.TypeKind == TypeKind.Delegate ||
                    method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal) ||
                    method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ServiceDescriptor") indirect = true;
            }
            foreach (var assignment in tree.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!IsTools(model.GetSymbolInfo(assignment.Left).Symbol)) continue;
                // Only explicit entries prove attachment. Variables, factories and spreads need dataflow analysis.
                var entries = assignment.Right switch
                {
                    CollectionExpressionSyntax collection when !collection.Elements.Any(item => item is SpreadElementSyntax)
                        => collection.Elements.Count,
                    InitializerExpressionSyntax initializer => initializer.Expressions.Count,
                    ObjectCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.Count,
                    ImplicitObjectCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.Count,
                    ArrayCreationExpressionSyntax { Initializer: { } initializer } => initializer.Expressions.Count,
                    ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions.Count,
                    LiteralExpressionSyntax literal when literal.RawKind == (int)Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression => 0,
                    _ => -1
                };
                if (entries < 0) unknownTools = true;
                if (entries <= 0) continue;
                var span = assignment.GetLocation().GetLineSpan();
                toolLocations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
            }
            foreach (var creation in tree.GetRoot().DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(creation).Symbol is not IMethodSymbol constructor) { indirect = true; continue; }
                for (var type = constructor.ContainingType; type is not null; type = type.BaseType)
                {
                    if (delegating?.ContainingAssembly.Name != abstractions ||
                        !SymbolEqualityComparer.Default.Equals(type, delegating)) continue;
                    var span = creation.GetLocation().GetLineSpan();
                    compositionLocations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                    break;
                }
            }
        }
        return [package, Registration(locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
            locations.Count > 0 ? "roslyn-static-registration-not-runtime-proof" : indirect ? "indirect-static-composition" : "roslyn-no-static-registration",
            locations.Take(64).ToArray()),
            Composition(compositionLocations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
                compositionLocations.Count > 0 ? "roslyn-static-composition-not-runtime-proof" :
                    indirect ? "indirect-static-composition" : "roslyn-no-static-composition", compositionLocations.Take(64).ToArray()),
            Tools(toolLocations.Count > 0 ? "detected" : indirect || unknownTools ? "unknown" : "absent",
                toolLocations.Count > 0 ? "roslyn-static-tool-registration-not-runtime-proof" :
                    indirect || unknownTools ? "indirect-static-composition" : "roslyn-no-static-tool-registration", toolLocations.Take(64).ToArray())];
    }

    static FrameworkCapabilityFact[] DetectReflection(Compilation? compilation)
    {
        const string id = "reflection-sensitive";
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return [new(id, "unknown", "limited-analysis-unavailable-or-incomplete-csharp-compilation")];
        var locations = new List<FrameworkCapabilityLocation>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol method) continue;
                var type = method.ContainingType.ToDisplayString();
                var framework = method.ContainingAssembly.Name is "System.Private.CoreLib" or "System.Runtime" or
                    "mscorlib" or "System.Reflection" or "System.Reflection.Extensions";
                var sensitive = framework && ((type == "System.Type" && method.Name is
                    "GetType" or "GetMethod" or "GetMethods" or "GetProperty" or "GetProperties" or
                    "GetField" or "GetFields" or "GetConstructor" or "GetConstructors" or "MakeGenericType") ||
                    (type == "System.Activator" && method.Name == "CreateInstance") ||
                    (type == "System.Reflection.Assembly" && method.Name is "Load" or "LoadFrom" or "LoadFile" or "GetType" or "GetTypes") ||
                    (type == "System.Reflection.MethodInfo" && method.Name == "MakeGenericMethod"));
                if (!sensitive) continue;
                var span = call.GetLocation().GetLineSpan();
                locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
            }
        }
        return [new(id, locations.Count > 0 ? "detected" : "absent",
            locations.Count > 0 ? "limited-analysis-resolved-reflection-sensitive-calls-not-aot-safety-proof" :
                "limited-analysis-no-listed-reflection-calls-not-aot-safety-proof", Locations: locations.Take(64).ToArray())];
    }

    static FrameworkCapabilityFact[] DetectPublishSettings(IReadOnlyDictionary<string, string?>? properties)
    {
        FrameworkCapabilityFact Fact(string id, string property)
        {
            if (properties is null || !properties.TryGetValue(property, out var value) || string.IsNullOrWhiteSpace(value))
                return new(id, "unknown", "evaluated-project-setting-unavailable");
            var trimmed = value.Trim();
            if (bool.TryParse(trimmed, out var enabled))
            {
                var frameworks = properties.GetValueOrDefault("TargetFrameworks") ?? properties.GetValueOrDefault("TargetFramework") ?? "";
                var rids = properties.GetValueOrDefault("RuntimeIdentifiers") ?? properties.GetValueOrDefault("RuntimeIdentifier") ?? "";
                var context = string.Join(";", new[] { frameworks, rids }.Where(item => item.Length > 0));
                return new(id, enabled ? "detected" : "absent",
                    $"evaluated-{property.ToLowerInvariant()}-{(enabled ? "enabled" : "disabled")}" + (context.Length == 0 ? "" : $";{context}"));
            }
            return new(id, "unknown", "unsupported-evaluated-project-setting-value");
        }
        return [Fact("publish-trimmed", "PublishTrimmed"), Fact("publish-aot", "PublishAot")];
    }

    static FrameworkCapabilityFact[] DetectChannels(Compilation? compilation)
    {
        const string id = "channels";
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return [new(id, "unknown", "unavailable-or-incomplete-csharp-compilation")];

        var locations = new List<FrameworkCapabilityLocation>();
        var indirect = false;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                if (method is not null && method.ContainingAssembly.Name == "System.Threading.Channels" &&
                    method.ContainingNamespace.ToDisplayString().StartsWith("System.Threading.Channels", StringComparison.Ordinal))
                {
                    var span = call.GetLocation().GetLineSpan();
                    locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                }
                else if (method is null && call.Expression.ToString().Contains("Channel", StringComparison.Ordinal)) indirect = true;
            }
        }
        return [new(id, locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
            locations.Count > 0 ? "roslyn-resolved-api-use" : indirect ? "unresolved-channel-api" : "roslyn-no-channel-api",
            Locations: locations.Take(64).ToArray())];
    }

    static FrameworkCapabilityFact[] DetectLocalization(Compilation? compilation)
    {
        const string id = "localization";
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return [new(id, "unknown", "unavailable-or-incomplete-csharp-compilation")];

        var locations = new List<FrameworkCapabilityLocation>();
        var indirect = false;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                if (method is not null && method.Name == "AddLocalization" &&
                    method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.LocalizationServiceCollectionExtensions" &&
                    method.ContainingAssembly.Name == "Microsoft.Extensions.Localization")
                {
                    var span = call.GetLocation().GetLineSpan();
                    locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                }
                else if (method is null && call.Expression.ToString().Contains("Localization", StringComparison.Ordinal)) indirect = true;
            }
        }
        return [new(id, locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
            locations.Count > 0 ? "roslyn-resolved-api-use" : indirect ? "unresolved-localization-api" : "roslyn-no-localization-api",
            Locations: locations.Take(64).ToArray())];
    }

    static FrameworkCapabilityFact[] DetectTelemetryComposition(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        FrameworkCapabilityFact Signal(string id, string assembly, string type, string methodName)
        {
            var version = packages?.GetValueOrDefault(assembly) ?? compilation?.ReferencedAssemblyNames
                .FirstOrDefault(item => item.Name == assembly)?.Version.ToString();
            if (compilation is null || compilation.Language != LanguageNames.CSharp ||
                compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                return new(id, "unknown", "unavailable-or-incomplete-csharp-compilation", version);
            if (version is not null && (!Version.TryParse(version, out var parsed) || parsed.Major != 1))
                return new(id, "unknown", "unsupported-package-version", version);
            var locations = new List<FrameworkCapabilityLocation>();
            var indirect = false;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                    if (method is null) { indirect = true; continue; }
                    method = method.ReducedFrom ?? method;
                    if ((method.ContainingAssembly.Name == assembly ||
                         (methodName is "WithTracing" or "WithMetrics" && method.ContainingAssembly.Name == "OpenTelemetry")) &&
                        (method.ContainingType.ToDisplayString() == type ||
                         (methodName is "WithTracing" or "WithMetrics" && method.ContainingType.ToDisplayString() == "OpenTelemetry.OpenTelemetryBuilder")) &&
                        method.Name == methodName)
                    {
                        var callVersion = packages?.GetValueOrDefault(method.ContainingAssembly.Name) ?? method.ContainingAssembly.Identity.Version.ToString();
                        if (!Version.TryParse(callVersion, out var supported) || supported.Major != 1)
                            return new(id, "unknown", "unsupported-package-version", callVersion);
                        version = callVersion;
                        var span = call.GetLocation().GetLineSpan();
                        locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                    }
                    else if (method.Locations.Any(item => item.IsInSource) || method.ContainingType.TypeKind == TypeKind.Delegate ||
                        method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal)) indirect = true;
                }
            }
            return new(id, locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
                locations.Count > 0 ? "roslyn-static-composition" : indirect ? "indirect-static-composition" : "roslyn-no-static-composition",
                version, locations.Take(64).ToArray());
        }
        return [Signal("telemetry-logging", "OpenTelemetry", "Microsoft.Extensions.Logging.OpenTelemetryLoggingExtensions", "AddOpenTelemetry"),
            Signal("telemetry-tracing", "OpenTelemetry.Extensions.Hosting", "OpenTelemetry.OpenTelemetryBuilderSdkExtensions", "WithTracing"),
            Signal("telemetry-metrics", "OpenTelemetry.Extensions.Hosting", "OpenTelemetry.OpenTelemetryBuilderSdkExtensions", "WithMetrics")];
    }

    static FrameworkCapabilityFact[] DetectTelemetry(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        FrameworkCapabilityFact Package(string id, Func<string, bool> matches, int major)
        {
            var installed = packages?.Where(item => matches(item.Key)).OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            var version = installed.FirstOrDefault().Value ?? compilation?.ReferencedAssemblyNames
                .Where(item => matches(item.Name)).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Version.ToString()).FirstOrDefault();
            if (compilation is null || compilation.Language != LanguageNames.CSharp ||
                compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
                return new(id, "unknown", "unavailable-or-incomplete-csharp-compilation", version);
            if (version is null) return new(id, "absent", "resolved-package-not-present");
            return !Version.TryParse(version, out var parsed) || parsed.Major != major
                ? new(id, "unknown", "unsupported-package-version", version)
                : new(id, "detected", "resolved-package", version);
        }
        return [Package("microsoft-extensions-telemetry",
                name => name.Equals("Microsoft.Extensions.Telemetry", StringComparison.OrdinalIgnoreCase), 10),
            Package("opentelemetry", name => name.Equals("OpenTelemetry", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("OpenTelemetry.", StringComparison.OrdinalIgnoreCase), 1)];
    }

    static FrameworkCapabilityFact[] DetectCaching(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        const string assembly = "Microsoft.Extensions.Caching.Memory";
        var version = packages?.GetValueOrDefault(assembly) ?? compilation?.ReferencedAssemblyNames
            .FirstOrDefault(item => item.Name == assembly)?.Version.ToString();
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return [new("memory-cache", "unknown", "unavailable-or-incomplete-csharp-compilation", version),
                new("distributed-cache", "unknown", "unavailable-or-incomplete-csharp-compilation", version)];
        if (version is not null && (!Version.TryParse(version, out var parsed) || parsed.Major != 10))
            return [new("memory-cache", "unknown", "unsupported-microsoft-extensions-version", version),
                new("distributed-cache", "unknown", "unsupported-microsoft-extensions-version", version)];

        FrameworkCapabilityFact DetectOne(string id, string methodName)
        {
            var locations = new List<FrameworkCapabilityLocation>();
            var indirect = false;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                    if (method is null) { indirect = true; continue; }
                    if (method.ReducedFrom is { } unreduced)
                        method = method.IsGenericMethod ? unreduced.Construct(method.TypeArguments.ToArray()) : unreduced;
                    if (method.ContainingAssembly.Name == assembly &&
                        method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.MemoryCacheServiceCollectionExtensions" &&
                        method.Name == methodName)
                    {
                        var callVersion = packages?.GetValueOrDefault(assembly) ?? method.ContainingAssembly.Identity.Version.ToString();
                        if (!Version.TryParse(callVersion, out var supported) || supported.Major != 10)
                            return new(id, "unknown", "unsupported-microsoft-extensions-version", callVersion);
                        var span = call.GetLocation().GetLineSpan();
                        locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                    }
                    else if (method.Locations.Any(item => item.IsInSource) || method.ContainingType.TypeKind == TypeKind.Delegate ||
                        method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal)) indirect = true;
                }
            }
            return new(id, locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
                locations.Count > 0 ? "roslyn-static-composition" : indirect ? "indirect-static-composition" : "roslyn-no-static-composition",
                version, locations.Take(64).ToArray());
        }
        return [DetectOne("memory-cache", "AddMemoryCache"), DetectOne("distributed-cache", "AddDistributedMemoryCache")];
    }

    static FrameworkCapabilityFact[] DetectHttpResilience(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        const string id = "http-client-resilience";
        const string assembly = "Microsoft.Extensions.Http.Resilience";
        var version = packages?.GetValueOrDefault(assembly) ?? compilation?.ReferencedAssemblyNames
            .FirstOrDefault(item => item.Name == assembly)?.Version.ToString();
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return [new(id, "unknown", "unavailable-or-incomplete-csharp-compilation", version)];
        if (version is null) return [new(id, "absent", "resolved-package-not-present")];
        return !Version.TryParse(version, out var parsed) || parsed.Major != 10
            ? [new(id, "unknown", "unsupported-microsoft-extensions-version", version)]
            : [new(id, "detected", "resolved-package", version)];
    }

    static FrameworkCapabilityFact[] DetectHttpClients(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        const string id = "http-client-factory";
        const string assembly = "Microsoft.Extensions.Http";
        var version = packages?.GetValueOrDefault(assembly) ?? compilation?.ReferencedAssemblyNames
            .FirstOrDefault(item => item.Name == assembly)?.Version.ToString();
        FrameworkCapabilityFact Fact(string factId, IReadOnlyList<FrameworkCapabilityLocation> locations, bool indirect)
            => new(factId, locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
                locations.Count > 0 ? "roslyn-static-composition" : indirect ? "indirect-static-composition" : "roslyn-no-static-composition",
                version, locations.Take(64).ToArray());
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return [new(id, "unknown", "unavailable-or-incomplete-csharp-compilation", version),
                new("http-client-typed", "unknown", "unavailable-or-incomplete-csharp-compilation", version),
                new("http-client-named", "unknown", "unavailable-or-incomplete-csharp-compilation", version)];
        if (version is not null && (!Version.TryParse(version, out var parsed) || parsed.Major != 10))
            return [new(id, "unknown", "unsupported-microsoft-extensions-version", version),
                new("http-client-typed", "unknown", "unsupported-microsoft-extensions-version", version),
                new("http-client-named", "unknown", "unsupported-microsoft-extensions-version", version)];

        var locations = new List<FrameworkCapabilityLocation>();
        var typedLocations = new List<FrameworkCapabilityLocation>();
        var namedLocations = new List<FrameworkCapabilityLocation>();
        var indirect = false;
        var typedIndirect = false;
        var namedIndirect = false;
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                if (method is null) { indirect = typedIndirect = namedIndirect = true; continue; }
                if (method.ReducedFrom is { } unreduced)
                    method = method.IsGenericMethod ? unreduced.Construct(method.TypeArguments.ToArray()) : unreduced;
                if (method.ContainingAssembly.Name == assembly &&
                    method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions" &&
                    method.Name == "AddHttpClient")
                {
                    var callVersion = packages?.GetValueOrDefault(assembly) ?? method.ContainingAssembly.Identity.Version.ToString();
                    if (!Version.TryParse(callVersion, out var supported) || supported.Major != 10)
                        return [new(id, "unknown", "unsupported-microsoft-extensions-version", callVersion),
                            new("http-client-typed", "unknown", "unsupported-microsoft-extensions-version", callVersion),
                            new("http-client-named", "unknown", "unsupported-microsoft-extensions-version", callVersion)];
                    var span = call.GetLocation().GetLineSpan();
                    var location = new FrameworkCapabilityLocation(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
                    locations.Add(location);
                    if (method.TypeArguments.Length > 0) typedLocations.Add(location);
                    if (method.Parameters.Any(parameter => parameter.Type.SpecialType == SpecialType.System_String)) namedLocations.Add(location);
                }
                else if (method.Locations.Any(item => item.IsInSource) || method.ContainingType.TypeKind == TypeKind.Delegate ||
                    method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal))
                    indirect = typedIndirect = namedIndirect = true;
            }
        }
        return [Fact(id, locations, indirect), Fact("http-client-typed", typedLocations, typedIndirect),
            Fact("http-client-named", namedLocations, namedIndirect)];
    }

    static FrameworkCapabilityFact[] DetectHosting(Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packages)
    {
        var valid = compilation is not null && compilation.Language == LanguageNames.CSharp &&
            !compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error);
        FrameworkCapabilityFact Fact(string id, string assembly, Func<IMethodSymbol, bool> matches)
        {
            var version = packages?.GetValueOrDefault(assembly) ?? compilation?.ReferencedAssemblyNames
                .FirstOrDefault(item => item.Name == assembly)?.Version.ToString();
            if (!valid) return new(id, "unknown", "unavailable-or-incomplete-csharp-compilation", version);
            if (version is not null && (!Version.TryParse(version, out var parsed) || parsed.Major != 10))
                return new(id, "unknown", "unsupported-microsoft-extensions-version", version);
            var locations = new List<FrameworkCapabilityLocation>();
            var indirect = false;
            foreach (var tree in compilation!.SyntaxTrees)
            {
                var model = compilation.GetSemanticModel(tree);
                foreach (var call in tree.GetRoot().DescendantNodes().Where(node => node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax))
                {
                    var method = model.GetSymbolInfo(call).Symbol as IMethodSymbol;
                    if (method is null) { indirect = true; continue; }
                    // Preserve inferred service types when restoring an extension receiver.
                    if (method.ReducedFrom is { } unreduced)
                        method = method.IsGenericMethod ? unreduced.Construct(method.TypeArguments.ToArray()) : unreduced;
                    if (matches(method))
                    {
                        var callVersion = packages?.GetValueOrDefault(method.ContainingAssembly.Name) ?? method.ContainingAssembly.Identity.Version.ToString();
                        if (!Version.TryParse(callVersion, out var supported) || supported.Major != 10)
                            return new(id, "unknown", "unsupported-microsoft-extensions-version", callVersion);
                        var span = call.GetLocation().GetLineSpan();
                        locations.Add(new(tree.FilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
                    }
                    else if (method.Locations.Any(item => item.IsInSource) ||
                        method.ContainingType.TypeKind == TypeKind.Delegate ||
                        method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal))
                        indirect = true;
                }
            }
            return new(id, locations.Count > 0 ? "detected" : indirect ? "unknown" : "absent",
                locations.Count > 0 ? "roslyn-static-composition" : indirect ? "indirect-static-composition" : "roslyn-no-static-composition",
                version, locations.Take(64).ToArray());
        }
        bool HostedRegistration(IMethodSymbol method, bool background)
        {
            var hosted = compilation?.GetTypeByMetadataName("Microsoft.Extensions.Hosting.IHostedService");
            var worker = compilation?.GetTypeByMetadataName("Microsoft.Extensions.Hosting.BackgroundService");
            if (hosted?.ContainingAssembly.Name != "Microsoft.Extensions.Hosting.Abstractions" ||
                worker?.ContainingAssembly.Name != "Microsoft.Extensions.Hosting.Abstractions") return false;
            ITypeSymbol? implementation = null;
            if (method.ContainingAssembly.Name == "Microsoft.Extensions.Hosting.Abstractions" &&
                method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ServiceCollectionHostedServiceExtensions" &&
                method.Name == "AddHostedService") implementation = method.TypeArguments.FirstOrDefault();
            else if (method.ContainingAssembly.Name == "Microsoft.Extensions.DependencyInjection.Abstractions" &&
                method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions" &&
                method.Name == "AddSingleton" && SymbolEqualityComparer.Default.Equals(method.TypeArguments.FirstOrDefault(), hosted))
                implementation = method.TypeArguments.LastOrDefault();
            if (implementation is not INamedTypeSymbol type) return false;
            if (!background) return SymbolEqualityComparer.Default.Equals(type, hosted) ||
                type.AllInterfaces.Any(item => SymbolEqualityComparer.Default.Equals(item, hosted));
            for (var current = type; current is not null; current = current.BaseType)
                if (SymbolEqualityComparer.Default.Equals(current, worker)) return true;
            return false;
        }
        return [Fact("generic-host", "Microsoft.Extensions.Hosting", method =>
            method.ContainingAssembly.Name == "Microsoft.Extensions.Hosting" &&
            ((method.ContainingType.ToDisplayString() == "Microsoft.Extensions.Hosting.Host" &&
              method.Name is "CreateDefaultBuilder" or "CreateApplicationBuilder" or "CreateEmptyApplicationBuilder") ||
             method.ContainingType.ToDisplayString() is "Microsoft.Extensions.Hosting.HostBuilder" or "Microsoft.Extensions.Hosting.HostApplicationBuilder")),
            Fact("dependency-injection", "Microsoft.Extensions.DependencyInjection.Abstractions", method =>
                (method.ContainingAssembly.Name == "Microsoft.Extensions.DependencyInjection.Abstractions" &&
                method.ContainingNamespace.ToDisplayString() is "Microsoft.Extensions.DependencyInjection" or "Microsoft.Extensions.DependencyInjection.Extensions" &&
                method.Name is "AddSingleton" or "AddScoped" or "AddTransient" or "TryAddSingleton" or "TryAddScoped" or "TryAddTransient" or
                    "AddKeyedSingleton" or "AddKeyedScoped" or "AddKeyedTransient" or "TryAdd" or "TryAddEnumerable" &&
                method.Parameters.FirstOrDefault()?.Type.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.IServiceCollection") ||
                (method.Name == "Add" && method.ContainingType.ToDisplayString() ==
                    "System.Collections.Generic.ICollection<Microsoft.Extensions.DependencyInjection.ServiceDescriptor>" &&
                 method.ContainingType.TypeArguments[0].ContainingAssembly.Name == "Microsoft.Extensions.DependencyInjection.Abstractions")),
            Fact("configuration", "Microsoft.Extensions.Configuration.Abstractions", method =>
                (method.ContainingAssembly.Name is "Microsoft.Extensions.Configuration.Abstractions" or "Microsoft.Extensions.Configuration" &&
                 method.ContainingType.ToDisplayString() is "Microsoft.Extensions.Configuration.IConfiguration" or
                    "Microsoft.Extensions.Configuration.ConfigurationManager" or "Microsoft.Extensions.Configuration.ConfigurationRoot" or
                    "Microsoft.Extensions.Configuration.ConfigurationSection" &&
                 method.Name == "GetSection") ||
                (method.ContainingAssembly.Name == "Microsoft.Extensions.Configuration.Binder" &&
                 method.ContainingType.ToDisplayString() == "Microsoft.Extensions.Configuration.ConfigurationBinder" &&
                 method.Name is "Bind" or "Get" or "GetValue")),
            Fact("options", "Microsoft.Extensions.Options", method =>
                (method.ContainingAssembly.Name == "Microsoft.Extensions.Options" &&
                 ((method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions" &&
                   method.Name is "AddOptions" or "Configure" or "PostConfigure") ||
                  (method.ContainingType.OriginalDefinition.ToDisplayString() == "Microsoft.Extensions.Options.OptionsBuilder<TOptions>" &&
                   method.Name is "Configure" or "PostConfigure"))) ||
                (method.ContainingAssembly.Name == "Microsoft.Extensions.Options.ConfigurationExtensions" &&
                 method.ContainingType.Name is "OptionsConfigurationServiceCollectionExtensions" or "OptionsBuilderConfigurationExtensions" &&
                 method.Name is "Configure" or "Bind" or "BindConfiguration")),
            Fact("logging", "Microsoft.Extensions.Logging", method =>
                (method.ContainingAssembly.Name == "Microsoft.Extensions.Logging" &&
                 method.ContainingType.ToDisplayString() == "Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions" &&
                 method.Name == "AddLogging") ||
                (method.ContainingAssembly.Name == "Microsoft.Extensions.Hosting" &&
                 method.ContainingType.ToDisplayString() == "Microsoft.Extensions.Hosting.HostingHostBuilderExtensions" &&
                 method.Name == "ConfigureLogging") ||
                (method.ContainingAssembly.Name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) &&
                 method.ContainingNamespace.ToDisplayString() == "Microsoft.Extensions.Logging" &&
                 method.Parameters.FirstOrDefault()?.Type.ToDisplayString() == "Microsoft.Extensions.Logging.ILoggingBuilder") ||
                (method.ContainingAssembly.Name == "Microsoft.Extensions.DependencyInjection.Abstractions" &&
                 method.ContainingNamespace.ToDisplayString() is "Microsoft.Extensions.DependencyInjection" or "Microsoft.Extensions.DependencyInjection.Extensions" &&
                 method.Name is "AddSingleton" or "AddScoped" or "AddTransient" or "TryAddSingleton" or "TryAddScoped" or "TryAddTransient" &&
                 method.TypeArguments.Any(type => type.ContainingAssembly.Name == "Microsoft.Extensions.Logging.Abstractions" &&
                     type.OriginalDefinition.ToDisplayString() is "Microsoft.Extensions.Logging.ILogger" or
                         "Microsoft.Extensions.Logging.ILogger<TCategoryName>" or "Microsoft.Extensions.Logging.ILoggerProvider" or
                         "Microsoft.Extensions.Logging.ILoggerFactory"))),
            Fact("background-service", "Microsoft.Extensions.Hosting.Abstractions", method => HostedRegistration(method, true)),
            Fact("hosted-service", "Microsoft.Extensions.Hosting.Abstractions", method => HostedRegistration(method, false))];
    }

    static FrameworkCapabilityFact[] DetectConsole(string? outputType, Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packageVersions)
    {
        var commandLineVersion = packageVersions?.GetValueOrDefault("System.CommandLine");
        FrameworkCapabilityFact[] Facts(string plain, string host, string redirected, string evidence) =>
            [new("plain-console", plain, evidence), new("generic-host-console", host, evidence),
             new("console-redirection", redirected, evidence),
             new("system-commandline", commandLineVersion is null ? "absent" : "detected", "evaluated-package-reference", commandLineVersion)];
        if (outputType != "Exe") return Facts("absent", "absent", "absent", "evaluated-output-type");
        if (compilation is null || compilation.Language != LanguageNames.CSharp ||
            compilation.GetDiagnostics().Any(item => item.Severity == DiagnosticSeverity.Error))
            return Facts("unknown", "unknown", "unknown", "unavailable-or-incomplete-csharp-compilation");
        var entry = compilation.GetEntryPoint(default);
        if (entry is null) return Facts("unknown", "unknown", "unknown", "unresolved-entry-point");
        var declarations = entry.DeclaringSyntaxReferences.Select(item => item.GetSyntax()).ToArray();
        // Synthesized top-level Main has no declaration on some Roslyn versions.
        var roots = declarations.Length > 0 ? declarations : compilation.SyntaxTrees.Select(tree => tree.GetRoot()).ToArray();
        var invocations = roots.SelectMany(root => root is CompilationUnitSyntax unit
            ? unit.Members.OfType<GlobalStatementSyntax>().SelectMany(item => item.DescendantNodes().OfType<InvocationExpressionSyntax>())
            : root.DescendantNodes().OfType<InvocationExpressionSyntax>()).ToArray();
        var indirect = false;
        var redirected = false;
        foreach (var invocation in invocations)
        {
            if (invocation.Ancestors().Any(node => node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)) continue;
            var symbol = compilation.GetSemanticModel(invocation.SyntaxTree).GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (symbol is null) { indirect = true; continue; }
            if (symbol.ContainingType.ToDisplayString() == "System.Console" &&
                symbol.ContainingAssembly.Name == "System.Console" &&
                symbol.Name is "get_IsInputRedirected" or "get_IsOutputRedirected") redirected = true;
            if (symbol.ContainingType.ToDisplayString() == "Microsoft.Extensions.Hosting.Host" &&
                symbol.ContainingAssembly.Name == "Microsoft.Extensions.Hosting" &&
                symbol.Name is "CreateDefaultBuilder" or "CreateApplicationBuilder")
                return Facts("absent", "detected", redirected ? "detected" : "unknown", "roslyn-entry-point-host-composition");
            if (symbol.Locations.Any(location => location.IsInSource) || symbol.ContainingType.TypeKind == TypeKind.Delegate)
                indirect = true;
        }
        foreach (var access in roots.SelectMany(root => root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()))
        {
            var property = compilation.GetSemanticModel(access.SyntaxTree).GetSymbolInfo(access).Symbol as IPropertySymbol;
            if (property?.ContainingType.ToDisplayString() == "System.Console" &&
                property.ContainingAssembly.Name == "System.Console" &&
                property.Name is "IsInputRedirected" or "IsOutputRedirected") redirected = true;
        }
        return indirect ? Facts("unknown", "unknown", "unknown", "indirect-entry-point-composition")
            : Facts("detected", "absent", redirected ? "detected" : "unknown", "roslyn-console-entry-point");
    }
}
