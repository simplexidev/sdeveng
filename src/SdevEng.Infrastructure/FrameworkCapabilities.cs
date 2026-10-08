using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SdevEng;

/// <summary>Shared static framework detection seam. Additional detectors extend this projection.</summary>
public static class FrameworkCapabilities
{
    public static FrameworkCapabilityFact[] Detect(string? outputType, Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packageVersions = null)
        => DetectConsole(outputType, compilation, packageVersions)
            .Concat(DetectHosting(compilation, packageVersions)).ToArray();

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
                         "Microsoft.Extensions.Logging.ILoggerFactory")))];
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
