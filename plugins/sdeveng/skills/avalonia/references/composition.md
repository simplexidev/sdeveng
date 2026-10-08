# Avalonia 11.3.0 XAML and code-only composition

Follow the project's detected UI structure: XAML, code-only, or mixed. Do not
add XAML solely to use Avalonia. Keep view construction consistent with the
existing application and lifetime; load only the composition style the project
uses.

For XAML, use `.axaml` files with Avalonia's XAML build integration and the
project's existing code-behind or view composition pattern. For code-only UI,
compose controls in C# and register or assign them through the detected
lifetime. Avalonia also supports code-created data templates such as
`FuncDataTemplate<T>`; use typed bindings when the project's setup supports
them. Do not assume compiled bindings are enabled: inspect project settings and
detected binding facts before choosing compiled or reflection binding.

Keep view and application resources at the narrowest scope that owns them;
reuse existing styles, templates, and resource dictionaries before adding
duplicates. Keep view composition separate from long-running work and marshal
UI access through the dispatcher guidance in
[`lifetime-ui-thread.md`](lifetime-ui-thread.md).

This guidance is for the detected Avalonia 11.3.0 package only. The supported
major and source revision are recorded in the
[framework provenance registry](../../../references/dotnet-skills-provenance.md).
API sources: [Creating data templates in code](https://v11.docs.avaloniaui.net/docs/concepts/templates/creating-data-templates-in-code/),
[compiled bindings](https://v11.docs.avaloniaui.net/docs/basics/data/data-binding/compiled-bindings/),
[data binding syntax](https://v11.docs.avaloniaui.net/docs/basics/data/data-binding/data-binding-syntax/).
