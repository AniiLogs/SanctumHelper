using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Markup;
using Microsoft.Win32;

namespace SanctumHelper;

/// <summary>The app's text in the game's languages. Strings are looked up by their English text
/// (which stays the fallback), from Assets/lang/&lt;code&gt;.json; game terms (difficulties, Egg
/// Boat, ...) come in the game's own wording from Assets/lang/game_terms.json (tools/extract_text.py).</summary>
public static class Lang
{
    /// <summary>The game's languages, in the order of its language setting (0 = zh_CN ...), with
    /// their names as shown in the language picker.</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("zh_CN", "简体中文"), ("zh_TW", "繁體中文"), ("en", "English"), ("ko_KR", "한국어"), ("ja_JP", "日本語"),
        ("vi_VN", "Tiếng Việt"), ("ru_RU", "Русский"), ("de_DE", "Deutsch"), ("fr_FR", "Français"), ("es_ES", "Español"),
        ("pt_PT", "Português"), ("id_ID", "Bahasa Indonesia"), ("th_TH", "ไทย"),
    ];

    public static string Code { get; private set; } = "en";
    /// <summary>The language the game shows its text in (for reading the party screen).</summary>
    public static string GameCode { get; private set; } = "en";

    static Dictionary<string, string> map = new();
    static Dictionary<string, Dictionary<string, string[]>> terms = new();
    static string root = "";

    /// <summary>The language the game shows according to its own settings (null: not found).</summary>
    public static string? DetectedGameCode { get; private set; }

    /// <summary>Picks the languages. Game: the setting, or with "Auto" the game's own language
    /// setting, then Windows' display language, then English. App: the setting, or with "Auto"
    /// the game's language.</summary>
    public static void Init(string assetsDir, string appSetting, string gameSetting)
    {
        root = Path.Combine(assetsDir, "lang");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "game_terms.json")));
            terms = doc.RootElement.GetProperty("terms").EnumerateObject().ToDictionary(
                l => l.Name, l => l.Value.EnumerateObject().ToDictionary(t => t.Name, t => t.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()));
        }
        catch (Exception e) { Log.Write("game terms: " + e.Message); }
        Apply(appSetting, gameSetting);
    }

    public static void Apply(string appSetting, string gameSetting)
    {
        DetectedGameCode = DetectGameLanguage();
        GameCode = gameSetting != "Auto" && Languages.Any(l => l.Code == gameSetting) ? gameSetting
            : DetectedGameCode ?? FromCulture(CultureInfo.CurrentUICulture) ?? "en";
        Code = appSetting != "Auto" && Languages.Any(l => l.Code == appSetting) ? appSetting : GameCode;
        map = new();
        var file = Path.Combine(root, Code + ".json");
        if (Code != "en" && File.Exists(file))
        {
            try { map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new(); }
            catch (Exception e) { Log.Write($"language {Code}: {e.Message}"); }
        }
    }

    /// <summary>A language's own name ("Deutsch", "日本語").</summary>
    public static string NameOf(string code) => Languages.FirstOrDefault(l => l.Code == code).Name ?? code;

    /// <summary>The text in the current language (the English text when there is no translation).</summary>
    public static string T(string english) => map.TryGetValue(english, out var t) && !string.IsNullOrEmpty(t) ? t : english;

    /// <summary>A translated format string filled in ({0}, {1} ...).</summary>
    public static string F(string english, params object[] args) => string.Format(T(english), args);

    /// <summary>A game term (Normal, Hard, Nightmare, Chaos, EggBoat, EggNest, Challenge, ...) in the
    /// game's own wording for the current language.</summary>
    public static string Term(string key, string? fallback = null) =>
        terms.TryGetValue(Code, out var t) && t.TryGetValue(key, out var v) && v.Length > 0 ? v[0] : fallback ?? key;

    /// <summary>A difficulty's name as the game shows it ("Normal" is the app's internal name).</summary>
    public static string Difficulty(string? d) => d == null ? Lang.T("Unknown") : Term(d, d);

    /// <summary>Every wording of a term in a language, for reading it off the screen.</summary>
    public static string[] Variants(string lang, string key) =>
        terms.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v) ? v : [];

    /// <summary>The game's language setting, from its saved preferences (Unity keeps them in the
    /// registry under HKCU\Software\Aniimo\Aniimo; reading them doesn't touch the game).</summary>
    /// <summary>The game's language setting as it is right now (it can be changed while playing).</summary>
    public static string? ReadGameLanguage() => DetectGameLanguage();

    static string? DetectGameLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Aniimo\Aniimo");
            if (key == null) return null;
            var name = key.GetValueNames().FirstOrDefault(n => n.Contains("_language_h", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("Audio", StringComparison.OrdinalIgnoreCase));
            if (name == null) return null;
            // Unity keeps ints as DWORDs, but this one is saved as text ("2" plus a NUL).
            int i = key.GetValue(name) switch
            {
                int v => v,
                long v => (int)v,
                byte[] b when int.TryParse(System.Text.Encoding.ASCII.GetString(b).TrimEnd('\0', ' '), out int p) => p,
                string str when int.TryParse(str.Trim(), out int p) => p,
                _ => -1,
            };
            return i >= 0 && i < Languages.Length ? Languages[i].Code : null;
        }
        catch { return null; }
    }

    static string? FromCulture(CultureInfo c)
    {
        string n = c.Name;
        if (n.StartsWith("zh")) return n.Contains("Hant") || n.EndsWith("TW") || n.EndsWith("HK") || n.EndsWith("MO") ? "zh_TW" : "zh_CN";
        string two = c.TwoLetterISOLanguageName;
        return Languages.Select(l => l.Code).FirstOrDefault(code => code.StartsWith(two + "_") || code == two);
    }

    /// <summary>Windows OCR language tag for the game's language (null: Windows has no OCR for it,
    /// e.g. Thai; Latin-script languages also read fine with English OCR).</summary>
    public static string? OcrTag(string code) => code switch
    {
        "zh_CN" => "zh-Hans-CN", "zh_TW" => "zh-Hant-TW", "ko_KR" => "ko", "ja_JP" => "ja", "ru_RU" => "ru",
        "de_DE" => "de-DE", "fr_FR" => "fr-FR", "es_ES" => "es-ES", "pt_PT" => "pt-PT", "vi_VN" => "vi", "id_ID" => "id",
        "en" => "en-US", _ => null,
    };
}

/// <summary>XAML: Text="{l:T 'English text'}" shows the text in the current language.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Lang.T(text);
}
