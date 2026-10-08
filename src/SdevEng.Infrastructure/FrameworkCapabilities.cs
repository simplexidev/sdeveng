using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SdevEng;

/// <summary>Shared static framework detection seam. Additional detectors extend this projection.</summary>
public static class FrameworkCapabilities
{
    public static FrameworkCapabilityFact[] Detect(string? outputType, Compilation? compilation,
        IReadOnlyDictionary<string, string?>? packageVersions = null)
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
