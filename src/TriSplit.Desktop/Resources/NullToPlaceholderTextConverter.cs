using System.Globalization;
using System.Windows.Data;

namespace TriSplit.Desktop.Resources;

public class NullToPlaceholderTextConverter : IValueConverter
{
    public string PlaceholderText { get; set; } = "Select an option";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || (value is string str && string.IsNullOrWhiteSpace(str)))
        {
            return parameter?.ToString() ?? PlaceholderText;
        }
        return value;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value?.ToString() == parameter?.ToString() || value?.ToString() == PlaceholderText)
        {
            return null;
        }
        return value;
    }
}