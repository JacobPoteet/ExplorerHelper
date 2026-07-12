using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ExplorerHelper.Converters;

/// <summary>
/// Visible when the bound bool is false, Collapsed when true — used to show an empty-state hint
/// only while a collection has no items (bound to ItemsControl.HasItems).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible ? false : true;
}
