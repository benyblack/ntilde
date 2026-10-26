using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Ntilde.Shell.Theming;
using Ntilde.Platform;
using Ntilde.VT;

namespace Ntilde.Shell
{
    public class DirectionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferDirection dir)
            {
                return dir == TransferDirection.Upload ? "⬆" : "⬇";
            }
            return "?";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class RunningConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is TransferState state) return state == TransferState.Running;
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    /// <summary>
    /// A transfer's state as one of the app's theme token brushes: Info while running, Success,
    /// Error, and Fg4 for canceled. The token instances are recoloured in place on a theme switch,
    /// so a rendered row follows it; before v2 these were fixed SkyBlue/LimeGreen/Red/Gray.
    /// </summary>
    public class StateBrushConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            string key = value switch
            {
                TransferState.Running => ThemeKeys.Info,
                TransferState.Completed => ThemeKeys.Success,
                TransferState.Failed => ThemeKeys.Error,
                TransferState.Canceled => ThemeKeys.Fg4,
                _ => ThemeKeys.Fg,
            };
            return Avalonia.Application.Current?.FindResource(key) as IBrush ?? Brushes.Transparent;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
