# Culture-aware resources

Register localization with `AddLocalization` and keep user-facing text in resource files. Select culture through the normal .NET culture flow, and provide a neutral fallback resource so unsupported cultures still render usable text. Keep formatting culture-aware for dates, numbers, and plural-sensitive messages; do not construct translated strings by concatenating fragments.

Static detection identifies a resolved registration only. It does not prove resource coverage, culture selection, or runtime fallback.

References: [ASP.NET Core globalization and localization](https://learn.microsoft.com/aspnet/core/fundamentals/localization).
