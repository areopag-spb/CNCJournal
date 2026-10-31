using remeLog.ViewModels;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace remeLog.Infrastructure.Converters
{
    /// <summary>
    /// Иконка статуса ячейки станка в календаре проверок.
    /// values[0] — строка дня (<see cref="MachineInspectionCalendarDayRow"/>),
    /// values[1] — имя станка (заголовок колонки DataGrid). Так один XAML-шаблон
    /// обслуживает все динамические колонки станков (см. MachineInspectionCalendarWindow.xaml).
    /// </summary>
    internal class MachineInspectionCalendarCellIconMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is MachineInspectionCalendarDayRow row && values[1] is string machine)
            {
                foreach (var cell in row.Cells)
                {
                    if (cell.Machine == machine)
                    {
                        var key = !cell.IsChecked ? "StatusErrorIcon" : cell.IsClosedByAi ? "DozorIcon.Ok" : "StatusOkIcon";
                        return Application.Current.TryFindResource(key) ?? DependencyProperty.UnsetValue;
                    }
                }
            }
            return DependencyProperty.UnsetValue;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
