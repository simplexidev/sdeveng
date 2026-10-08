# Trimming review

Load only for detected evaluated `PublishTrimmed=true`. Use the project's evaluated target framework and runtime identifiers, not defaults inferred from source. This reference targets .NET 10 SDK/framework guidance; unknown or unsupported SDK/framework versions require matching upstream documentation before version-specific advice.

Required capabilities: `sdeveng dotnet inspect --json`, resolved C# compilation, and the matching .NET SDK for analyzer evidence. Inspect `reflection-sensitive` locations for Type member lookup, Activator construction, assembly loading, and generic reflection construction. These are limited static review candidates, including potentially safe annotated calls, not compiler trim warnings or proof of AOT safety. `absent` means no listed calls; `unknown` means analysis was unavailable. Indirect reflection and runtime behavior remain unproved.

Prefer explicit registration, generics, or supported source generation where practical. When reflection is needed, propagate minimal `DynamicallyAccessedMembers` requirements through callers; use `RequiresUnreferencedCode` to expose genuinely incompatible boundaries. Do not suppress warnings merely to pass validation. Review SDK trim warnings and consumer behavior before claiming compatibility; static fixture detection alone proves neither publish nor runtime safety.

Sources: Microsoft [.NET trim warning guidance](https://learn.microsoft.com/dotnet/core/deploying/trimming/fixing-warnings) and [library preparation](https://learn.microsoft.com/dotnet/core/deploying/trimming/prepare-libraries-for-trimming). Reference upstream content; do not copy it. NativeAOT guidance and publish fixtures are separate resources.
