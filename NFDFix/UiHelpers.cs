using Microsoft.UI.Xaml;
using NFDFix.Services;

namespace NFDFix
{
    /// <summary>
    /// Functions used by <c>x:Bind</c> in XAML.
    /// </summary>
    public static class UiHelpers
    {
        public static string OnOff(bool value) => Localizer.GetString(value ? "ToggleOn" : "ToggleOff");

        public static Visibility VisibleIfFalse(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    }
}
