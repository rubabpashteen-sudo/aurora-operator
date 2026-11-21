using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AuroraOperator.Core;

namespace AuroraOperator.App;

/// <summary>
/// Maps a plan step's <see cref="StepStatus"/> to its accent colour.
/// </summary>
public sealed class StepStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StepStatus status)
        {
            return Brushes.Gray;
        }

        return status switch
        {
            StepStatus.Running => new SolidColorBrush(Color.FromRgb(0x5E, 0xEA, 0xD4)),
            StepStatus.Succeeded => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
            StepStatus.Failed => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
            StepStatus.NeedsApproval => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
            StepStatus.Skipped => Brushes.Gray,
            _ => Brushes.Gray,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException("StepStatusToBrushConverter is one-way.");
    }
}
