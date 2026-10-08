# Microsoft.Extensions.AI 10 application detection

Required evidence: evaluated packages and a complete C# Roslyn compilation from
`sdeveng dotnet inspect --json`. No model, weights, endpoint or UI is required.

`microsoft-extensions-ai` reports resolved Microsoft.Extensions.AI or
Microsoft.Extensions.AI.Abstractions package/version evidence. Supported major is
10; missing analysis or unsupported/mixed versions remain unknown.
`chat-client-registration` requires the real Abstractions `IChatClient` identity
and resolved `AddChatClient`, `AddKeyedChatClient`, or generic/typeof DI lifetime/TryAdd
registration of that interface. Package presence, constructing a client, and
matching local names do not prove registration. Indirect/dynamic wiring remains
unknown. Static registration does not prove resolution or runtime activity.

This guides application code only; factory workers retain their own provider-neutral
runtime contracts.
Use fake chat clients and synthetic outputs for tests; never paid endpoints or weights.

`chat-client-composition` identifies resolved `ChatClientBuilder.Use` and framework
`Use*` extensions receiving that builder, or construction of a real
`DelegatingChatClient` subclass. Locations identify static middleware/decorator
composition, not execution, DI registration, pipeline order or runtime activity.
Builder construction alone and local lookalikes are absent; indirect/dynamic wiring,
incomplete compilation and unsupported versions remain unknown.

`chat-tool-registration` identifies explicit nonempty collections assigned to the
real `ChatOptions.Tools` property (including object/collection initializers), or
`Tools.Add` calls. It reports static attachment of tools/functions, not execution,
request submission or runtime activity. `AIFunctionFactory.Create` alone,
`UseFunctionInvocation` alone, empty/null collections and local lookalikes do not
prove attachment. Collection variables, spreads and indirect/dynamic wiring remain
unknown; no dataflow or final collection state is inferred.

Provenance: Microsoft.Extensions.AI 10.0.0, MIT, dotnet/extensions commit
`fbd393616ef5ce0f2f1521a7250e4311728ed93a` from the published NuGet repository metadata.
Verify installed-version overloads against the
[pinned registration source](https://github.com/dotnet/extensions/blob/fbd393616ef5ce0f2f1521a7250e4311728ed93a/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/ChatClientBuilderServiceCollectionExtensions.cs)
and [API reference](https://learn.microsoft.com/dotnet/api/microsoft.extensions.dependencyinjection.chatclientbuilderservicecollectionextensions?view=net-10.0-pp).
References only; no upstream implementation is vendored.

Composition provenance: the same pinned
[builder source](https://github.com/dotnet/extensions/blob/fbd393616ef5ce0f2f1521a7250e4311728ed93a/src/Libraries/Microsoft.Extensions.AI/ChatCompletion/ChatClientBuilder.cs)
and [decorator source](https://github.com/dotnet/extensions/blob/fbd393616ef5ce0f2f1521a7250e4311728ed93a/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/DelegatingChatClient.cs).

Tool provenance: the same pinned
[options source](https://github.com/dotnet/extensions/blob/fbd393616ef5ce0f2f1521a7250e4311728ed93a/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatOptions.cs)
and [function factory source](https://github.com/dotnet/extensions/blob/fbd393616ef5ce0f2f1521a7250e4311728ed93a/src/Libraries/Microsoft.Extensions.AI.Abstractions/Functions/AIFunctionFactory.cs).
