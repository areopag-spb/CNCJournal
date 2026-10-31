using remeLog.ViewModels;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace remeLog.Infrastructure.Converters
{
    /// <summary>
    /// Подсказка ячейки станка в календаре проверок: для суток, закрытых ИИ без человека,
    /// — «Закрыто ИИ»; иначе подсказки нет. Параметры те же, что у
    /// <see cref="MachineInspectionCalendarCellIconMultiConverter"/>.
    /// </summary>
    internal class MachineInspectionCalendarCellToolTipMultiConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is MachineInspectionCalendarDayRow row && values[1] is string machine)
            {
                foreach (var cell in row.Cells)
                {
                    if (cell.Machine == machine && cell.IsChecked && cell.IsClosedByAi)
                        return "Закрыто ИИ без участия человека. Двойной клик — открыть сутки.";
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
