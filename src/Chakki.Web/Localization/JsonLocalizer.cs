using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Localization;

namespace Chakki.Web.Localization;

/// <summary>
/// English text is the key; Resources/i18n/{culture}.json maps it to the translation. A missing translation falls back
/// to English, so adding a screen never breaks Hindi — it only shows English until the phrase is translated.
/// </summary>
public sealed class JsonStringLocalizer(IReadOnlyDictionary<string, Dictionary<string, string>> tables) : IStringLocalizer
{
    public LocalizedString this[string name]
    {
        get
        {
            var found = TryGet(name, out var value);
            return new LocalizedString(name, value, resourceNotFound: !found);
        }
    }

    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            var found = TryGet(name, out var value);
            return new LocalizedString(name, string.Format(CultureInfo.CurrentCulture, value, arguments), !found);
        }
    }

    private bool TryGet(string name, out string value)
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (tables.TryGetValue(culture, out var table) && table.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
        {
            value = v;
            return true;
        }
        value = name;
        return culture == "en";
    }

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return tables.TryGetValue(culture, out var table)
            ? table.Select(kv => new LocalizedString(kv.Key, kv.Value))
            : [];
    }
}

public sealed class JsonStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly JsonStringLocalizer _localizer;

    public JsonStringLocalizerFactory(IWebHostEnvironment env)
    {
        var tables = new ConcurrentDictionary<string, Dictionary<string, string>>();
        var dir = Path.Combine(env.ContentRootPath, "Resources", "i18n");
        if (Directory.Exists(dir))
            foreach (var file in Directory.GetFiles(dir, "*.json"))
                tables[Path.GetFileNameWithoutExtension(file)] =
                    JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
        _localizer = new JsonStringLocalizer(tables);
    }

    public IStringLocalizer Create(Type resourceSource) => _localizer;
    public IStringLocalizer Create(string baseName, string location) => _localizer;
}

/// <summary>Marker type for <c>IStringLocalizer&lt;Shared&gt;</c>.</summary>
public sealed class Shared;

public static class LocalizerExtensions
{
    public static bool Hi(this IStringLocalizer _) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "hi";
}
