Console.WriteLine(Create(typeof(FixtureMessage)));

static object? Create([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type type) => Activator.CreateInstance(type);

public sealed class FixtureMessage;
