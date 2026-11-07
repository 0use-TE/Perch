using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Perch.Strings;

namespace Perch.Services;

public sealed record LanguageOption(string Code, string Name);

// UI strings live in Strings/<code>.axaml. XAML reads them with {DynamicResource Key}, so switching language
// updates the windows in place; code reads them with T / F and refreshes its own text on Changed.
// English is always loaded underneath, so a key missing from another language falls back to English.
// To add a language: copy Strings/en.axaml to Strings/<code>.axaml, translate it, and add it to Available.
public static class Loc
{
    public static readonly LanguageOption[] Available = [new("en", "English"), new("zh-CN", "简体中文")];
    private static ResourceDictionary? english, selected;

    public static string Code { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");
    public static event Action? Changed;

    // Setting "" follows the Windows display language. PERCH_LANG overrides both (used by tools/Verify-Window.ps1).
    public static string Resolve(string? setting)
    {
        var env = Environment.GetEnvironmentVariable("PERCH_LANG");
        var wanted = !string.IsNullOrWhiteSpace(env) ? env : !string.IsNullOrWhiteSpace(setting) ? setting : CultureInfo.CurrentUICulture.Name;
        foreach (var option in Available)
            if (string.Equals(option.Code, wanted, StringComparison.OrdinalIgnoreCase)) return option.Code;
        var language = wanted.Split('-')[0];
        foreach (var option in Available)
            if (string.Equals(option.Code.Split('-')[0], language, StringComparison.OrdinalIgnoreCase)) return option.Code;
        return "en";
    }

    public static void Apply(string? setting)
    {
        var code = Resolve(setting);
        var merged = Application.Current!.Resources.MergedDictionaries;
        english ??= Load("en");
        if (!merged.Contains(english)) merged.Add(english);
        if (selected != null) merged.Remove(selected);
        selected = code == "en" ? null : Load(code);
        if (selected != null) merged.Add(selected);
        var changed = code != Code;
        Code = code;
        Culture = CultureInfo.GetCultureInfo(code == "en" ? "en-US" : code);
        if (changed) Changed?.Invoke();
    }

    // Add a case here when adding a language file (see the note above).
    private static ResourceDictionary Load(string code) => code switch
    {
        "zh-CN" => new ChineseStrings(),
        _ => new EnglishStrings(),
    };

    public static string T(string key)
    {
        var app = Application.Current;
        return app != null && app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is string text ? text : key;
    }

    // Fills %0, %1 … in the translated string, formatting numbers and dates for the current language.
    public static string F(string key, params object?[] args)
    {
        var text = T(key);
        for (var i = args.Length - 1; i >= 0; i--)
            text = text.Replace("%" + i, args[i] is IFormattable f ? f.ToString(null, Culture) : args[i]?.ToString() ?? "");
        return text;
    }

    public static string Date(DateTime value, string formatKey) => value.ToString(T(formatKey), Culture);
}
