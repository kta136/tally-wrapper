using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// The culture the screens format amounts and dates in. The showroom writes lakhs and crores
/// (2,89,706), and WPF would otherwise group in thousands (289,706) because a bound
/// <c>StringFormat</c> follows the element's language, which defaults to en-US.
/// </summary>
public static class UiCulture
{
    public const string Name = "en-IN";

    private static bool _applied;

    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo(Name);

    /// <summary>Makes every element format with <see cref="Culture"/>. Safe to call more than once.</summary>
    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Name)));
    }

    /// <summary>Formats an amount with two decimals, lakh grouping and no currency sign: 1,00,457.56.</summary>
    public static string Amount(decimal value) => value.ToString("N2", Culture);

    /// <summary>Formats a whole-rupee amount: 2,89,706.</summary>
    public static string Rupees(decimal value) => value.ToString("N0", Culture);

    /// <summary>Formats a weight with the three decimals the showroom reads: 12.450.</summary>
    public static string Weight(decimal value) => value.ToString("N3", Culture);
}
