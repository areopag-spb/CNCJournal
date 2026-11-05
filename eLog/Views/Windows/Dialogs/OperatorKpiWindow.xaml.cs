using eLog.ViewModels;
using System.Windows;

namespace eLog.Views.Windows.Dialogs
{
    public partial class OperatorKpiWindow : Window
    {
        public OperatorKpiWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (DataContext is OperatorKpiViewModel vm)
                    await vm.RefreshAsync();
            };
        }
    }
}
