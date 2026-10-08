# NativeAOT review

Load only for detected evaluated `PublishAot=true`. Use the evaluated target framework and runtime identifier; confirm the matching .NET SDK and native toolchain before publishing. Do not infer a supported target from source alone.

Required capabilities: `sdeveng dotnet inspect --json`, resolved C# compilation, and matching SDK/analyzer evidence. Review `reflection-sensitive` locations as limited candidates, then inspect trim and AOT analyzer warnings and publish output. The detector does not prove AOT safety; indirect reflection and runtime behavior remain unproved. Prefer explicit registration, generics, or supported source generation where practical. Preserve required metadata with the smallest applicable annotations and do not suppress warnings just to pass a fixture.

The synthetic projects under `evals/trim-aot/fixtures/` exercise trimming and NativeAOT publish settings. Build only for an explicit fixture check with the matching SDK and platform toolchain; a missing native toolchain is unavailable evidence, not a passing publish.

Sources: Microsoft [.NET trimming guidance](https://learn.microsoft.com/dotnet/core/deploying/trimming/) and [Native AOT deployment](https://learn.microsoft.com/dotnet/core/deploying/native-aot/). Reference upstream content; do not copy it.
