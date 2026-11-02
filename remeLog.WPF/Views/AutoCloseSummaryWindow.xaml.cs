using remeLog.Models;
using remeLog.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace remeLog.Views
{
    /// <summary>
    /// Логика взаимодействия для AutoCloseSummaryWindow.xaml
    /// </summary>
    public partial class AutoCloseSummaryWindow : Window
    {
        internal AutoCloseSummaryWindow()
        {
            InitializeComponent();
            DataContext = new AutoCloseSummaryViewModel();
            ClosedGrid.MouseDoubleClick += ClosedGrid_MouseDoubleClick;
        }

        // Двойной клик по закрытым ИИ суткам — открыть их (как в календаре проверки).
        private void ClosedGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ClosedGrid.CurrentItem is not AutoCloseControlRow row) return;
            // Двойной клик по кнопкам проверки в строке — это не открытие суток.
            for (var d = e.OriginalSource as DependencyObject; d != null && d != ClosedGrid;
                 d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            {
                if (d is System.Windows.Controls.Primitives.ButtonBase) return;
            }

            var partsInfo = new CombinedParts(row.Machine, row.Date, row.Date);
            var window = new PartsInfoWindow(partsInfo)
            {
                Owner = Application.Current.MainWindow
            };
            window.Show();
        }
    }
}
