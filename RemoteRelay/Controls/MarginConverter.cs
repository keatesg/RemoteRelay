using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace RemoteRelay.Controls;

public class MarginConverter : IValueConverter
{
    public static readonly MarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double windowHeight)
        {
            // Default factor is 1% of the window height
            double factor = 0.01;
            if (parameter is string paramStr && double.TryParse(paramStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double paramFactor))
            {
                factor = paramFactor;
            }
            
            // Generate a comfortable proportional margin
            double marginValue = Math.Floor(windowHeight * factor);
            
            // Enforce a minimum margin of 2 pixels so elements never completely touch on microscopically thin rows
            if (marginValue < 2) marginValue = 2;
            
            return new Thickness(marginValue);
        }
        
        return new Thickness(4);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
