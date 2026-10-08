# Avalonia 11.3.0 lifetime and UI thread

Use the lifetime detected from resolved project types; do not assume a desktop
application. For classic desktop apps, configure the main window and shutdown
policy through `IClassicDesktopStyleApplicationLifetime`. For single-view apps,
use `ISingleViewApplicationLifetime` and its root view. Keep lifetime-specific
startup and shutdown code behind the matching interface.

Create and access controls on Avalonia's UI thread. From background work,
marshal UI updates through `Dispatcher.UIThread.Post` or `InvokeAsync`; keep
blocking I/O and computation off that thread. Do not use dispatcher APIs before
the framework has initialized. Stop background work and release app-owned
resources as part of the detected lifetime's shutdown path; do not rely on
process-exit callbacks for UI cleanup.

These APIs are scoped to detected Avalonia 11.3.0. The supported major and
source revision are recorded in the [framework provenance registry](../../../references/dotnet-skills-provenance.md).
API sources: [IClassicDesktopStyleApplicationLifetime](https://github.com/AvaloniaUI/Avalonia/blob/d6edb46ce04f983892a61d3abf906014d3f5ec8d/src/Avalonia.Controls/ApplicationLifetimes/IClassicDesktopStyleApplicationLifetime.cs),
[ISingleViewApplicationLifetime](https://github.com/AvaloniaUI/Avalonia/blob/d6edb46ce04f983892a61d3abf906014d3f5ec8d/src/Avalonia.Controls/ApplicationLifetimes/ISingleViewApplicationLifetime.cs),
[Dispatcher](https://github.com/AvaloniaUI/Avalonia/blob/d6edb46ce04f983892a61d3abf906014d3f5ec8d/src/Avalonia.Base/Threading/Dispatcher.cs).
