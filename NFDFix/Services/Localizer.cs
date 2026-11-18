using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace NFDFix.Services
{
    /// <summary>A UI language the app ships strings for.</summary>
    /// <param name="Tag">BCP-47 tag matching the <c>Strings/&lt;Tag&gt;</c> folder.</param>
    /// <param name="NativeName">The language's name in itself, shown the same in every UI language.</param>
    /// <param name="HintKey">Resource key of the secondary name shown next to it in the language menu.</param>
    public sealed record AppLanguage(string Tag, string NativeName, string HintKey);

    /// <summary>
    /// Looks up UI strings and stores the language the user picked.
    /// </summary>
    /// <remarks>
    /// XAML strings come from <c>x:Uid</c>; code reads the keys without a dot through <see cref="GetString"/>.
    /// To add a language, add <c>Strings/&lt;tag&gt;/Resources.resw</c> with the same keys and list it in
    /// <see cref="SupportedLanguages"/>. The choice is applied at startup, so changing it needs a restart.
    /// </remarks>
    public static class Localizer
    {
        public static AppLanguage[] SupportedLanguages { get; } =
        [
            new("ko-KR", "한국어", "LanguageHintKorean"),
            new("en-US", "English", "LanguageHintEnglish"),
        ];

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NFDFix", "language.txt");

        private static ResourceLoader? _loader;

        /// <summary>
        /// Gets the saved language tag, or an empty string to follow the Windows display language.
        /// May differ from the running language until the app restarts.
        /// </summary>
        public static string PreferredLanguage { get; private set; } = string.Empty;

        /// <summary>Reads the saved language and applies it. Call before any UI is created.</summary>
        public static void Initialize()
        {
            PreferredLanguage = Normalize(ReadSetting());
            try
            {
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = PreferredLanguage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to apply the language override: {ex}");
            }

            if (PreferredLanguage.Length > 0)
            {
                // Also format numbers and dates in the chosen language.
                var culture = CultureInfo.GetCultureInfo(PreferredLanguage);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
        }

        /// <summary>Saves the language to use from the next launch; an empty tag follows Windows.</summary>
        public static void SetPreferredLanguage(string tag)
        {
            tag = Normalize(tag);
            PreferredLanguage = tag;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, tag);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save the language setting: {ex}");
            }
        }

        /// <summary>Gets the string for <paramref name="key"/> in the running language, or the key itself if it is missing.</summary>
        public static string GetString(string key)
        {
            try
            {
                _loader ??= new ResourceLoader();
                var value = _loader.GetString(key);
                return string.IsNullOrEmpty(value) ? key : value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Missing string '{key}': {ex.Message}");
                return key;
            }
        }

        public static string Format(string key, params object?[] args) =>
            string.Format(CultureInfo.CurrentCulture, GetString(key), args);

        private static string ReadSetting()
        {
            try
            {
                return File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : string.Empty;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }

        /// <summary>Returns the supported tag matching <paramref name="tag"/>, or an empty string.</summary>
        private static string Normalize(string? tag) =>
            SupportedLanguages.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase))?.Tag ?? string.Empty;
    }
}
