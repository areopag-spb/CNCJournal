using DocumentFormat.OpenXml.Spreadsheet;
using remeLog.Infrastructure;
using remeLog.Infrastructure.Extensions;
using remeLog.Infrastructure.Spell;
using remeLog.Infrastructure.Types;
using remeLog.Models;
using remeLog.ViewModels;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using libeLog.Views;
using Part = remeLog.Models.Part;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace remeLog.Views
{
    /// <summary>
    /// Логика взаимодействия для PartsInfoWindow.xaml
    /// </summary>
    public partial class PartsInfoWindow : Window
    {
        enum DataType
        {
            None, Numeric, TimeSpan
        }

        private readonly List<MenuItem> _columnProfileMenuItems = new();
        private bool _suppressColumnWidthCapture;

        public PartsInfoWindow(CombinedParts parts)
        {
            InitializeComponent();
            foreach (var id in PartColumnMeta.ColumnOrder)
            {
                var column = (DataGridColumn)FindResource($"{id}Column");
                partsGrid.Columns.Add(column);
                DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))
                    ?.AddValueChanged(column, (_, _) => OnColumnWidthChanged(id, column));
            }
            ColumnWidthDefaults.CaptureOnce(PartColumnMeta.ColumnOrder, partsGrid.Columns);
            DataContext = new PartsInfoWindowViewModel(parts);
            Closing += (_, _) => (DataContext as PartsInfoWindowViewModel)?.CancelAllAiChecks();
            var vm = (PartsInfoWindowViewModel)DataContext;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(PartsInfoWindowViewModel.AvailableColumnProfiles)
                                    or nameof(PartsInfoWindowViewModel.ActiveColumnProfileName))
                    RebuildColumnProfileMenu();
                if (e.PropertyName == nameof(PartsInfoWindowViewModel.ActiveColumnProfileName))
                    ApplyColumnWidthsForActiveProfile();
            };
            RebuildColumnProfileMenu();
            ApplyColumnWidthsForActiveProfile();
            var groupNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase)
            {
                "Ожидание"
            };
            var engineerMenu = (ContextMenu)FindResource("EngeneerCommentCellContextMenu");
            var groups = new Dictionary<string, MenuItem>(StringComparer.CurrentCultureIgnoreCase);

            foreach (var comment in AppSettings.EngineerComments)
            {
                var firstWord = comment
                    .Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries)[0]
                    .Trim();
                var item = new MenuItem { Header = comment }
                    .Tap(c => c.Click += OnVariantClick);

                if (groupNames.Contains(firstWord))
                {
                    if (!groups.TryGetValue(firstWord, out var groupMenu))
                    {
                        groupMenu = new MenuItem
                        {
                            Header = firstWord
                        };

                        groups[firstWord] = groupMenu;
                        engineerMenu.Items.Add(groupMenu);
                    }

                    groupMenu.Items.Add(item);
                }
                else
                {
                    engineerMenu.Items.Add(item);
                }
            }
                
            engineerMenu.Items.Add(new Separator());
            engineerMenu.Items.Add(new MenuItem { Header = "Очистить", Icon = TryFindResource("CleanData") as UIElement}.Tap(i => i.Click += OnClearVariantClick));
        }

        /// <summary>
        /// Пункты пользовательских профилей столбцов в меню "Вид" — динамические, поэтому
        /// строятся здесь, а не через ItemsControl в XAML (см. комментарий у columnProfilesSeparator).
        /// </summary>
        private void RebuildColumnProfileMenu()
        {
            if (DataContext is not PartsInfoWindowViewModel vm) return;

            foreach (var item in _columnProfileMenuItems)
                viewMenu.Items.Remove(item);
            _columnProfileMenuItems.Clear();

            var insertAt = viewMenu.Items.IndexOf(columnProfilesSeparator) + 1;
            foreach (var profile in vm.AvailableColumnProfiles)
            {
                var item = new MenuItem
                {
                    Header = profile.Name,
                    Padding = new Thickness(0),
                    Command = vm.SelectColumnProfileCommand,
                    CommandParameter = profile.Name,
                    Icon = profile.Name == vm.ActiveColumnProfileName
                        ? new TextBlock { Text = "●", FontSize = 16, Margin = new Thickness(4, 0, 0, 3), TextAlignment = TextAlignment.Center }
                        : null,
                };
                viewMenu.Items.Insert(insertAt++, item);
                _columnProfileMenuItems.Add(item);
            }
        }

        /// <summary> Ширины столбцов активного профиля (или дефолт из разметки для встроенных ролей) </summary>
        private void ApplyColumnWidthsForActiveProfile()
        {
            if (DataContext is not PartsInfoWindowViewModel vm) return;
            var profile = vm.ActiveColumnProfileName is string name
                ? AppSettings.Instance.ColumnProfiles.FirstOrDefault(p => p.Name == name)
                : null;

            _suppressColumnWidthCapture = true;
            try
            {
                foreach (var id in PartColumnMeta.ColumnOrder)
                {
                    var column = (DataGridColumn)FindResource($"{id}Column");
                    var width = profile != null && profile.ColumnWidths.TryGetValue(id, out var overrideWidth)
                        ? overrideWidth
                        : ColumnWidthDefaults.GetDefault(id);
                    column.Width = new DataGridLength(width);
                }
            }
            finally
            {
                _suppressColumnWidthCapture = false;
            }
        }

        /// <summary>
        /// Ручной resize столбца мышью — сохраняется в активный пользовательский
        /// профиль (для встроенных ролей ширина не персистится, см. согласованный подход).
        /// </summary>
        private void OnColumnWidthChanged(string id, DataGridColumn column)
        {
            if (_suppressColumnWidthCapture) return;
            if (DataContext is not PartsInfoWindowViewModel vm || vm.ActiveColumnProfileName is not string name) return;
            var profile = AppSettings.Instance.ColumnProfiles.FirstOrDefault(p => p.Name == name);
            if (profile == null) return;

            var width = column.Width.Value;
            if (double.IsNaN(width) || width <= 0) return;
            profile.ColumnWidths[id] = width;
            AppSettings.Save();
        }

        private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if(sender is ComboBox cb)
            {
                cb.Text = "Фильтр по станку";
            }
        }

        private void ValidationTextBlock_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBlock { Parent: Grid grid }) return;
            foreach (UIElement gridChild in grid.Children)
            {
                if (gridChild is AdornedElementPlaceholder { AdornedElement: TextBlock textBlock } 
                && System.Windows.Controls.Validation.GetErrors(textBlock) is ICollection<ValidationError> { Count: > 0 } errors)
                {
                    MessageBoxWindow.Show(errors.First().ErrorContent.ToString(), "Некорректный ввод", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        /// <summary>
        /// Варианты исправления слова-опечатки под кареткой — в начало общего
        /// меню редактора (сам подбор — в SpellCheckBehavior).
        /// </summary>
        private static void AppendSpellingSuggestions(ContextMenu menu, TextBox textBox) =>
            SpellCheckBehavior.AppendSuggestionsToMenu(menu, textBox);

        private static bool IsEditingCell(DataGrid dataGrid)
        {
            var cellContent = dataGrid.CurrentCell.Column?
                .GetCellContent(dataGrid.CurrentCell.Item);

            if (cellContent == null)
                return false;

            var cell = FindVisualParent<DataGridCell>(cellContent);

            return cell?.IsEditing == true;
        }

        private void DataGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.MiddleButton == MouseButtonState.Pressed)
            {
                DependencyObject depObj = (DependencyObject)e.OriginalSource;
                DataGridCell cell = FindVisualParent<DataGridCell>(depObj);
                if (cell != null)
                {
                    DataGridColumn column = cell.Column;
                    object value = cell.DataContext;
                    if (DataContext is PartsInfoWindowViewModel d && value is Part p)
                    {
                        switch (ColumnId.GetId(column))
                        {
                            case "Shift":
                                d.ShiftFilter = d.ShiftFilter.FilterText == p.Shift ? new Shift(Infrastructure.Types.ShiftType.All) : new Shift(p.Shift);
                                break;
                            case "Operator":
                                d.OperatorFilter = d.OperatorFilter == p.Operator ? "" : p.Operator;
                                break;
                            case "PartName":
                                d.PartNameFilter = d.PartNameFilter == p.PartName ? "" : p.PartName;
                                break;
                            case "Order":
                                d.OrderFilter = d.OrderFilter == p.Order ? "" : p.Order;
                                break;
                            case "Setup":
                                d.SetupFilter = d.SetupFilter == p.Setup ? null : p.Setup;
                                break;
                            case "EngineerConclusion":
                                var comments = AppSettings.EngineerComments;
                                int i = Array.IndexOf(comments, p.EngineerConclusion);
                                p.EngineerConclusion = i == comments.Length - 1
                                    ? ""
                                    : comments[Math.Max(0, i + 1)];
                                break;
                        }
                    }
                }
            }
            else if (e.RightButton == MouseButtonState.Pressed)
            {
                var depObj = (DependencyObject)e.OriginalSource;

                // Редактируемый TextBox — показываем меню редактирования
                if (FindVisualParent<TextBox>(depObj) is { } editingTextBox)
                {
                    editingTextBox.Focus();
                    // каретку — в точку клика, чтобы варианты предлагались для кликнутого слова;
                    // клик внутри выделения выделение не трогает (иначе Копировать/Вырезать не работают)
                    int clickIndex = editingTextBox.GetCharacterIndexFromPoint(e.GetPosition(editingTextBox), true);
                    SpellCheckBehavior.MoveCaretToClick(editingTextBox, clickIndex);
                    e.Handled = true;
                    var textBoxMenu = (ContextMenu)FindResource("EditingTextBoxContextMenu");
                    AppendSpellingSuggestions(textBoxMenu, editingTextBox);
                    textBoxMenu.PlacementTarget = editingTextBox;
                    textBoxMenu.IsOpen = true;
                    return;
                }

                if (FindVisualParent<DataGridCell>(depObj) is not { } cell) return;

                var column = cell.Column;
                var value = cell.DataContext;

                if (DataContext is PartsInfoWindowViewModel d && value is Part p)
                {
                    switch (ColumnId.GetId(column))
                    {
                        // Оператор и М/Л — добавление в MultiValueEditor
                        case "Operator" or "Order":
                            e.Handled = true;
                            cell.Focus();
                            var multiMenu = (ContextMenu)FindResource("MultiFilterValueContextMenu");
                            multiMenu.PlacementTarget = cell;
                            multiMenu.IsOpen = true;
                            break;

                        // Времена — вставка готовых значений
                        case "StartSetupTime" or "StartMachiningTime" or "EndMachiningTime":
                            e.Handled = true;
                            cell.Focus();
                            var timeMenu = (ContextMenu)FindResource("TimeContextMenu");
                            timeMenu.PlacementTarget = cell;
                            timeMenu.IsOpen = true;
                            break;

                        // Комментарий мастера к наладке/изготовлению (варианты в меню — станок/
                        // операция, пригодны для обеих категорий отклонений). Пункты только
                        // для роли Мастер — остальным ролям здесь предлагать нечего, они этот
                        // комментарий не заполняют (см. MasterOwnedColumns/DataGrid_BeginningEdit).
                        case "MasterSetupDetail" or "MasterMachiningDetail" when d.ViewMode == User.Master:
                            e.Handled = true;
                            cell.Focus();
                            var masterMenu = (ContextMenu)FindResource("MasterCommentCellContextMenu");
                            masterMenu.PlacementTarget = cell;
                            masterMenu.IsOpen = true;
                            break;

                        // Нормативы серийной детали
                        case "FixedSetupTimePlan" or "FixedProductionTimePlan" when p.IsSerial:
                            e.Handled = true;
                            cell.Focus();
                            var normMenu = (ContextMenu)FindResource("SerialPartFixedNormativesContextMenu");
                            normMenu.PlacementTarget = cell;
                            normMenu.IsOpen = true;
                            break;

                        // Причины отклонений — переопределение аналитиком (СГТ 1). Меню
                        // переопределения дополняется пунктами фильтра той же ячейки: иначе оно
                        // перехватывало бы событие целиком и закрывало аналитику доступ
                        // к фильтру по этой колонке.
                        case "MasterSetupComment" or "MasterMachiningComment":
                            var isSetup = ColumnId.GetId(column) == "MasterSetupComment";
                            var overrideMenu = BuildReasonOverrideContextMenu(d, p, isSetup) ?? new ContextMenu();
                            AppendFilterItems(overrideMenu, cell);
                            if (overrideMenu.Items.Count == 0) break;
                            e.Handled = true;
                            cell.Focus();
                            overrideMenu.PlacementTarget = cell;
                            overrideMenu.IsOpen = true;
                            break;

                        // Заключение техотдела
                        case "EngineerConclusion":
                            e.Handled = true;
                            cell.Focus();
                            var engMenu = (ContextMenu)FindResource("EngeneerCommentCellContextMenu");
                            engMenu.PlacementTarget = cell;
                            engMenu.IsOpen = true;
                            break;
                    }
                }
                if (!e.Handled)
                    TryShowFilterContextMenu(cell, e);
            }

        }

        /// <summary>
        /// Поля мастера, недоступные аналитику (Engineer) для прямого редактирования: правка
        /// затёрла бы позицию мастера, ради сохранения которой и сделан слой переопределений.
        /// Причину аналитик меняет через «Переопределить причину…» и там же пишет обоснование,
        /// а для собственных заметок у него есть «Заключение техотдела» и «Комментарий
        /// техотдела». На мастера и разработчика ограничение не распространяется.
        /// «Комментарий к простоям» в список не входит: эта колонка от Engineer и так скрыта.
        /// </summary>
        private static readonly HashSet<string> MasterOwnedColumns = new()
        {
            "MasterSetupComment",
            "MasterMachiningComment",
            "MasterSetupDetail",
            "MasterMachiningDetail",
            "MasterComment",
        };

        private void DataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (DataContext is not PartsInfoWindowViewModel vm) return;
            if (vm.ViewMode != User.Engineer) return;

            if (ColumnId.GetId(e.Column) is { } id && MasterOwnedColumns.Contains(id))
                e.Cancel = true;
        }

        /// <summary>
        /// Меню переопределения причины. Пункты появляются только под фичей ReasonOverride —
        /// смотреть чужие переопределения (маркер и тултип в ячейке) можно и без неё.
        /// </summary>
        private System.Windows.Controls.ContextMenu? BuildReasonOverrideContextMenu(
            PartsInfoWindowViewModel vm, Part part, bool isSetup)
        {
            if (!vm.HasFeatureReasonOverride) return null;

            var menu = new System.Windows.Controls.ContextMenu();
            var hasOverride = isSetup ? part.HasSetupReasonOverride : part.HasMachiningReasonOverride;

            var overrideItem = new MenuItem
            {
                Header = hasOverride ? "Изменить переопределение…" : "Переопределить причину…",
            };
            overrideItem.Click += (_, _) => ShowReasonOverrideDialog(vm, part, isSetup);
            menu.Items.Add(overrideItem);

            if (hasOverride)
            {
                var clearItem = new MenuItem { Header = "Снять переопределение" };
                clearItem.Click += (_, _) => ClearReasonOverride(part, isSetup);
                menu.Items.Add(clearItem);
            }

            return menu;
        }

        private void ShowReasonOverrideDialog(PartsInfoWindowViewModel vm, Part part, bool isSetup)
        {
            var dialog = new ReasonOverrideDialogWindow(
                categoryTitle: isSetup ? PartColumnMeta.H_MasterSetupComment : PartColumnMeta.H_MasterMachiningComment,
                masterReason: isSetup ? part.MasterSetupComment : part.MasterMachiningComment,
                masterDetail: isSetup ? part.MasterSetupDetail : part.MasterMachiningDetail,
                reasons: isSetup ? vm.SetupReasons : vm.MachiningReasons,
                requireComment: isSetup ? vm.SetupReasonsRequireComment : vm.MachiningReasonsRequireComment,
                currentOverride: isSetup ? part.SetupReasonOverride : part.MachiningReasonOverride,
                currentComment: isSetup ? part.SetupReasonOverrideComment : part.MachiningReasonOverrideComment,
                currentIsMasterFault: isSetup ? part.SetupReasonOverrideIsMasterFault : part.MachiningReasonOverrideIsMasterFault,
                currentMasterFaultComment: isSetup ? part.SetupReasonOverrideMasterFaultComment : part.MachiningReasonOverrideMasterFaultComment)
            {
                Owner = this,
            };

            if (dialog.ShowDialog() != true) return;

            // Комментарий об ошибке мастера имеет смысл только вместе с флагом — если аналитик
            // снял «Ошибка мастера», не оставляем текст висеть невидимым до следующей отметки.
            var masterFaultComment = dialog.IsMasterFault ? dialog.MasterFaultComment : string.Empty;

            if (isSetup)
            {
                part.SetupReasonOverride = dialog.SelectedReason ?? string.Empty;
                part.SetupReasonOverrideComment = dialog.OverrideComment;
                part.SetupReasonOverrideIsMasterFault = dialog.IsMasterFault;
                part.SetupReasonOverrideMasterFaultComment = masterFaultComment;
            }
            else
            {
                part.MachiningReasonOverride = dialog.SelectedReason ?? string.Empty;
                part.MachiningReasonOverrideComment = dialog.OverrideComment;
                part.MachiningReasonOverrideIsMasterFault = dialog.IsMasterFault;
                part.MachiningReasonOverrideMasterFaultComment = masterFaultComment;
            }

            StampOverrideAuthor(part);
        }

        private static void ClearReasonOverride(Part part, bool isSetup)
        {
            if (isSetup)
            {
                part.SetupReasonOverride = string.Empty;
                part.SetupReasonOverrideComment = string.Empty;
                part.SetupReasonOverrideIsMasterFault = false;
                part.SetupReasonOverrideMasterFaultComment = string.Empty;
            }
            else
            {
                part.MachiningReasonOverride = string.Empty;
                part.MachiningReasonOverrideComment = string.Empty;
                part.MachiningReasonOverrideIsMasterFault = false;
                part.MachiningReasonOverrideMasterFaultComment = string.Empty;
            }

            // Автор/время общие на запись — чистим только когда снято последнее переопределение.
            if (!part.HasSetupReasonOverride && !part.HasMachiningReasonOverride)
            {
                part.ReasonOverrideBy = string.Empty;
                part.ReasonOverrideAt = null;
            }
        }

        private static void StampOverrideAuthor(Part part)
        {
            part.ReasonOverrideBy = Environment.UserName;
            part.ReasonOverrideAt = DateTime.Now;
        }

        private System.Windows.Controls.ContextMenu BuildPartFlagContextMenu(PartsInfoWindowViewModel vm, Part part)
        {
            var menu = new System.Windows.Controls.ContextMenu();

            if (vm.HasFeatureAi)
            {
                if (!part.IsFlagged)
                {
                    var flagItem = new MenuItem
                    {
                        Header = "⚠ Проблемная запись — отметить",
                    };
                    flagItem.Click += (_, _) => vm.TogglePartFlagCommand.Execute(part);
                    menu.Items.Add(flagItem);
                }
                else
                {
                    var unflagItem = new MenuItem
                    {
                        Header = "✔ Снять отметку проблемной",
                        FontWeight = System.Windows.FontWeights.SemiBold,
                    };
                    unflagItem.Click += (_, _) => vm.TogglePartFlagCommand.Execute(part);
                    menu.Items.Add(unflagItem);
                }
            }

            return menu;
        }


        private void DataGrid_SelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
        {
            if (sender is DataGrid dataGrid && DataContext is PartsInfoWindowViewModel d)
            {
                var selectedCells = dataGrid.SelectedCells;
                if (selectedCells.Count <= 1)
                {
                    d.Status = string.Empty;
                    return;
                }
                string percent = "";
                double sum = 0;
                TimeSpan timeSpan = TimeSpan.Zero;
                int cnt = 0;
                int cntWithioutZeroes = 0;
                foreach (DataGridCellInfo cell in selectedCells.Where(c => c.IsValid))
                {
                    if (ColumnId.GetId(cell.Column) is "DefectiveCount" or "Setup" or "StartSetupTime")
                    {
                        d.Status = string.Empty;
                        return;
                    }
                    var content = cell.Column.GetCellContent(cell.Item);
                    if (content is TextBlock textBlock)
                    {
                        var value = textBlock.Text;
                        if (value.EndsWith("%"))
                        {
                            percent = "%";
                            value = value.Replace("%", "");
                        }
                        if (double.TryParse(value, out double num))
                        {
                            sum += num;
                            if (num > 0) cntWithioutZeroes++;
                            cnt++;
                        }
                        else if (TimeSpan.TryParse(textBlock.Text, out TimeSpan span))
                        {
                            timeSpan += span;
                            if (timeSpan.Ticks > 0) cntWithioutZeroes++;
                            cnt++;
                        }
                    }
                }
                if (sum > 0 && cnt > 0 && timeSpan == TimeSpan.Zero)
                {
                    d.Status = $"Среднее: {sum / cnt:0.#}{percent} ({sum / cntWithioutZeroes:0.#}{percent})     Количество: {cnt:0.#}     Сумма: {sum}{percent}";
                }
                else if (timeSpan.Ticks > 0 && cnt > 0 && sum == 0)
                {
                    d.Status = $"Среднее: {TimeSpan.FromTicks(timeSpan.Ticks / cnt):hh\\:mm\\:ss} ({TimeSpan.FromTicks(timeSpan.Ticks / cntWithioutZeroes):hh\\:mm\\:ss})     Количество: {cnt}     Сумма: {timeSpan:hh\\:mm\\:ss}";
                }
                else
                {
                    d.Status = string.Empty;
                }
            }
        }

        private async void DataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not DataGrid dataGrid)
                return;
            if (IsEditingCell(dataGrid))
                return;
            if (DataContext is PartsInfoWindowViewModel d)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    switch (e.Key)
                    {
                        case Key.I:
                            var infoCell = dataGrid.SelectedCells.FirstOrDefault();
                            var colIndex = infoCell.Column.DisplayIndex;
                            var colId = ColumnId.GetId(infoCell.Column) ?? "(нет)";
                            var infoCellContent = infoCell.Column.GetCellContent(infoCell.Item);
                            var info = infoCellContent is TextBlock tb ? tb.Text : "null";
                            info = $"Выбрано ячеек: {dataGrid.SelectedCells.Count}\n\n" +
                                $"Информация о первой выделенной:\n" +
                                $"Индекс столбца: {colIndex} (ID: {colId})\n" +
                                $"Тип: {infoCellContent}\n" +
                                $"Содержимое: {info}\n\n" +
                                $"Деталь: {d.SelectedPart?.PartName}";
                            MessageBoxWindow.Show(info);
                            e.Handled = true;
                            break;

                        case Key.F:
                            // не работает - надо разобраться
                            break; // временно закрыл
                                   //var baseCell = dataGrid.SelectedCells.FirstOrDefault();
                                   //if (!baseCell.IsValid) return;
                                   //var content = baseCell.Column.GetCellContent(baseCell.Item);
                                   //if (content is TextBlock textBlock)
                                   //{
                                   //    var value = textBlock.Text;
                                   //    foreach (var cell in dataGrid.SelectedCells.Skip(1))
                                   //    {
                                   //        var cellContent = cell.Column.GetCellContent(cell.Item);
                                   //        if (cellContent is TextBlock textBlockToUpdate)
                                   //        {
                                   //            textBlockToUpdate.Text = value;
                                   //        }
                                   //    }
                                   //}
                                   //e.Handled = true;
                                   //break;

                        case Key.V:
                            {
                                var cells = dataGrid.SelectedCells
                                    .Where(CanPasteToCell)
                                    .ToList();

                                if (cells.Count == 0)
                                    break;

                                foreach (var cell in cells)
                                    PasteToCell(dataGrid, cell);

                                e.Handled = true;
                                break;
                            }
                        case Key.W:
                            if (d.SelectedPart is not null)
                            {
                                d.SearchInWinnumCommand.Execute(d.SelectedPart);
                                e.Handled = true;
                            }                        
                            break;
                        case Key.Delete:
                            if (d.SelectedPart is Part dp)
                            {
                                d.DeletePartCommand.Execute(dp);
                                e.Handled = true;
                            }
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Показывает контекстное меню фильтрации для любой колонки таблицы.
        /// Вызывается из DataGrid_PreviewMouseDown как fallback после специфичных меню.
        /// </summary>
        private void TryShowFilterContextMenu(DataGridCell cell, MouseButtonEventArgs e)
        {
            var menu = (ContextMenu)FindResource("GenericCellFilterContextMenu");
            menu.Items.Clear();

            if (!AppendFilterItems(menu, cell)) return;

            e.Handled = true;
            cell.Focus();
            menu.PlacementTarget = cell;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Добавляет в переданное меню пункты фильтрации по значению ячейки. Вынесено из
        /// TryShowFilterContextMenu, чтобы те же пункты можно было пристыковать к другому
        /// меню (см. вызов у "MasterSetupComment"/"MasterMachiningComment" в
        /// DataGrid_PreviewMouseDown) — возвращает false, если для колонки фильтр не нужен.
        /// </summary>
        private bool AppendFilterItems(ContextMenu menu, DataGridCell cell)
        {
            if (DataContext is not PartsInfoWindowViewModel d) return false;

            string colId = ColumnId.GetId(cell.Column) ?? "";

            if (!PartColumnMeta.Map.TryGetValue(colId, out var meta)) return false;
            if (meta.Kind == FilterKind.None) return false;

            string? cellValue = GetCellTextValue(cell);
            if (string.IsNullOrWhiteSpace(cellValue)) return false;

            if (menu.Items.Count > 0)
                menu.Items.Add(new Separator());

            menu.Items.Add(new MenuItem
            {
                Header = meta.DisplayName,
                IsEnabled = false,
                FontWeight = FontWeights.SemiBold,
            });
            menu.Items.Add(new Separator());

            // Для колонок с существующими UI-контролами (Смена, Оператор, Деталь,
            // М/Л, Установка) обновляем их напрямую; для остальных — чип.
            var filterItem = new MenuItem { Header = $"Фильтровать: «{cellValue}»" };
            filterItem.Click += (_, _) =>
            {
                switch (colId)
                {
                    case "Shift":
                        d.ShiftFilter = new Shift(cellValue);
                        break;
                    case "Operator":
                        d.OperatorFilter = cellValue;
                        break;
                    case "PartName":
                        d.PartNameFilter = cellValue;
                        break;
                    case "Order":
                        d.OrderFilter = cellValue;
                        break;
                    case "Setup" when int.TryParse(cellValue, out int setup):
                        d.SetupFilter = setup;
                        break;
                    default:
                        d.SetChipFilter(meta, cellValue);
                        break;
                }
            };
            menu.Items.Add(filterItem);
            var addItem = new MenuItem { Header = $"Добавить к фильтру: «{cellValue}»" };
            addItem.Click += (_, _) =>
            {
                string editorKey = colId switch
                {
                    "Operator" => "Operator",
                    "Order" => "Order",
                    _ when string.IsNullOrWhiteSpace(meta.SqlColumn) => $"col:{colId}",
                    _ => meta.SqlColumn,
                };
                d.PushValueToEditor(editorKey, cellValue);
            };

            menu.Items.Add(addItem);

            var existingChip = d.ChipFilters.FirstOrDefault(c => c.DisplayName == meta.DisplayName);

            bool hasActiveFilter = colId switch
            {
                "Shift" => d.ShiftFilter.Type != ShiftType.All,
                "Operator" => !string.IsNullOrEmpty(d.OperatorFilter),
                "PartName" => !string.IsNullOrEmpty(d.PartNameFilter),
                "Order" => !string.IsNullOrEmpty(d.OrderFilter),
                "Setup" => d.SetupFilter.HasValue,
                _ => existingChip is not null,
            };

            if (hasActiveFilter)
            {
                menu.Items.Add(new Separator());

                var clearItem = new MenuItem { Header = $"Убрать фильтр по «{meta.DisplayName}»" };
                clearItem.Click += (_, _) =>
                {
                    switch (colId)
                    {
                        case "Shift": d.ShiftFilter = new Shift(ShiftType.All); break;
                        case "Operator": d.OperatorFilter = string.Empty; break;
                        case "PartName": d.PartNameFilter = string.Empty; break;
                        case "Order": d.OrderFilter = string.Empty; break;
                        case "Setup": d.SetupFilter = null; break;
                        default:
                            if (existingChip is not null)
                                d.RemoveChipFilter(existingChip);
                            break;
                    }
                };
                menu.Items.Add(clearItem);
            }

            if (DataContext is PartsInfoWindowViewModel dv
                && dv.IsSingleMachineSingleDay
                && dv.HasFeatureAi
                && cell.DataContext is Part flaggedPart)
            {
                menu.Items.Add(new Separator());
                var flagItem = new MenuItem
                {
                    Header = flaggedPart.IsFlagged
                        ? "Снять отметку проблемной"
                        : "Отметить как проблемную",
                    FontWeight = flaggedPart.IsFlagged
                        ? FontWeights.SemiBold
                        : FontWeights.Normal,
                };
                flagItem.Click += (_, _) => dv.TogglePartFlagCommand.Execute(flaggedPart);
                menu.Items.Add(flagItem);
            }

            return true;
        }



        private bool CanPasteToCell(DataGridCellInfo cellInfo)
        {
            if (!cellInfo.IsValid)
                return false;

            var column = cellInfo.Column;

            if (column == null || column.IsReadOnly)
                return false;

            string? propertyName = GetPropertyName(cellInfo);

            if (string.IsNullOrWhiteSpace(propertyName))
                return false;

            var prop = cellInfo.Item.GetType().GetProperty(propertyName);

            if (prop == null || !prop.CanWrite)
                return false;

            return true;
        }

        private string? GetPropertyName(DataGridCellInfo cellInfo)
        {
            var column = cellInfo.Column;

            // обычный DataGridTextColumn
            if (column is DataGridBoundColumn boundColumn &&
                boundColumn.Binding is Binding binding)
            {
                if (binding.Mode == BindingMode.OneWay)
                    return null;

                return binding.Path?.Path;
            }

            // TemplateColumn
            var content = column.GetCellContent(cellInfo.Item);

            if (content is FrameworkElement fe)
            {
                var tb = FindVisualChild<TextBox>(fe);
                if (tb != null)
                {
                    var b = BindingOperations.GetBinding(tb, TextBox.TextProperty);
                    if (b?.Mode == BindingMode.OneWay)
                        return null;

                    return b?.Path?.Path;
                }

                var txt = FindVisualChild<TextBlock>(fe);
                if (txt != null)
                {
                    var b = BindingOperations.GetBinding(txt, TextBlock.TextProperty);
                    if (b?.Mode == BindingMode.OneWay)
                        return null;

                    return b?.Path?.Path;
                }
            }

            return null;
        }

        private void PartsInfoWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {            // если обрабатывать, то окно кнопки в окне нажимаются не с первого раза
            return;
            if (DataContext is PartsInfoWindowViewModel d)
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H || e.Key is Key.Help or Key.F1)
                {
                    var helpWindow = new PartsInfoHelpWindow();
                    helpWindow.ShowDialog();
                }
                else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E)
                {
                    d.ExportToExcelCommand.Execute(null);

                }
            }
        }

        private void CopyToClipboard_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            var contextMenu = FindVisualParent<ContextMenu>(item);
            if (contextMenu?.PlacementTarget is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
                Clipboard.SetText(textBlock.Text);
        }

        private void CopyAiResult_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            var contextMenu = FindVisualParent<ContextMenu>(item);
            if (contextMenu?.PlacementTarget is FrameworkElement target
                && target.DataContext is PartsInfoWindowViewModel vm)
            {
                var text = vm.AiResultFormatted;
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
            }
        }

        private void CopyAiResultFromPopup_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PartsInfoWindowViewModel vm)
            {
                var text = vm.AiResultFormatted;
                if (!string.IsNullOrEmpty(text))
                    Clipboard.SetText(text);
            }
        }

        /// <summary> Принять предложение ИИ-проверки: сменить причину строки на предложенную. </summary>
        private void OnApplyAiCheckSuggestionClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: Part part } } })
                part.ApplyAiCheckSuggestion();
        }

        private void OnVariantClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;

            string newText = item.Header?.ToString() ?? "";

            if (Keyboard.FocusedElement is not DataGridCell cell) return;

            var row = DataGridRow.GetRowContainingElement(cell);
            var itemData = row?.Item;
            if (itemData == null) return;

            string? propertyName = null;

            if (cell.Column is DataGridTextColumn textColumn && textColumn.Binding is Binding binding)
            {
                propertyName = binding.Path?.Path;
            }
            else
            {
                FrameworkElement? boundElement = FindBoundFrameworkElement<TextBox>(cell) as FrameworkElement;
                boundElement ??= FindBoundFrameworkElement<TextBlock>(cell) as FrameworkElement;

                if (boundElement != null)
                {
                    if (BindingOperations.GetBinding(boundElement, GetDependencyProperty(boundElement)) is Binding innerBinding)
                    {
                        propertyName = innerBinding.Path?.Path;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(propertyName))
            {
                propertyName = MapToWritableProperty(propertyName);
                var prop = itemData.GetType().GetProperty(propertyName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(itemData, newText);
                }
            }
        }

        private void OnInsertValueClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            string value = item.Tag?.ToString() ?? item.Header?.ToString() ?? "";

            ContextMenu? contextMenu = FindVisualParent<ContextMenu>(item);
            if (contextMenu?.PlacementTarget is FrameworkElement target)
            {
                switch (target)
                {
                    case TextBox textBox:
                        int caretIndex = textBox.CaretIndex;
                        textBox.Text = textBox.Text.Insert(caretIndex, value);
                        textBox.CaretIndex = caretIndex + value.Length;
                        textBox.Focus();
                        return;

                    case DataGridCell cell:
                        var dataGrid = FindVisualParent<DataGrid>(cell);
                        if (dataGrid == null) return;

                        if (cell.IsEditing)
                        {
                            if (cell.Content is TextBox editor)
                            {
                                int caret = editor.CaretIndex;
                                editor.Text = editor.Text.Insert(caret, value);
                                editor.CaretIndex = caret + value.Length;
                                editor.Focus();
                            }
                        }
                        else
                        {
                            SetSingleCellValue(cell, dataGrid, value);
                        }
                        return;
                }
            }
        }

        private void OnSetValueClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem item) return;
            if (!Util.HasFeature(RemeLogFeature.AdvancedEdit)) { MessageBoxWindow.Show("Нет прав на выполнение операции", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error); return; }
            string value = item.Tag?.ToString() ?? string.Empty;

            if (Keyboard.FocusedElement is DataGridCell cell)
            {
                var dataGrid = FindVisualParent<DataGrid>(cell);
                if (dataGrid == null) return;

                var selectedCells = dataGrid.SelectedCells;

                if (selectedCells.Count > 0)
                {
                    foreach (var selectedCell in selectedCells)
                    {
                        SetCellValue(dataGrid, selectedCell, value);
                    }
                }
                else
                {
                    SetSingleCellValue(cell, dataGrid, value);
                }
            }
        }

        private void OnAddMultiFilterValueClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem) return;
            if (Keyboard.FocusedElement is DataGridCell cell)
            {
                DataGridColumn column = cell.Column;
                object value = cell.DataContext;
                if (DataContext is PartsInfoWindowViewModel d && value is Part p)
                {
                    switch (ColumnId.GetId(column))
                    {
                        case "Operator":
                            d.PushValueToEditor("Operator", p.Operator);
                            break;
                        case "Order":
                            d.PushValueToEditor("Order", p.Order);
                            break;
                    }
                }
            }
        }

        private void SetCellValue(DataGrid dataGrid, DataGridCellInfo cellInfo, string value)
        {
            var cellContainer = GetDataGridCell(dataGrid, cellInfo);
            if (cellContainer != null)
            {
                SetSingleCellValue(cellContainer, dataGrid, value);
            }
        }

        //private void SetSingleCellValue(DataGridCell cell, DataGrid dataGrid, string value)
        //{
        //    TextBox? textBox = FindVisualChild<TextBox>(cell);
        //    if (textBox != null)
        //    {
        //        textBox.Text = value;
        //        textBox.Focus();
        //        return;
        //    }

        //    dataGrid.CurrentCell = new DataGridCellInfo(cell);
        //    dataGrid.BeginEdit();

        //    textBox = FindVisualChild<TextBox>(cell);
        //    if (textBox != null)
        //    {
        //        textBox.Text = value;
        //        textBox.Focus();
        //    }
        //}

        private void SetSingleCellValue(DataGridCell cell, DataGrid dataGrid, string value)
        {
            dataGrid.CurrentCell = new DataGridCellInfo(cell);
            dataGrid.SelectedCells.Clear();
            dataGrid.SelectedCells.Add(dataGrid.CurrentCell);

            if (!cell.IsEditing)
                dataGrid.BeginEdit();

            var textBox = FindVisualChild<TextBox>(cell);
            if (textBox == null)
                return;

            textBox.Text = value;

            var be = textBox.GetBindingExpression(TextBox.TextProperty);
            be?.UpdateSource();

            dataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        }

        private DataGridCell? GetDataGridCell(DataGrid dataGrid, DataGridCellInfo cellInfo)
        {
            var rowContainer = (DataGridRow)dataGrid.ItemContainerGenerator.ContainerFromItem(cellInfo.Item);
            if (rowContainer != null)
            {
                var presenter = FindVisualChild<DataGridCellsPresenter>(rowContainer);
                if (presenter != null)
                {
                    var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(cellInfo.Column.DisplayIndex);
                    return cell;
                }
            }
            return null;
        }

        private void OnClearVariantClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem) return;

            string newText = "";

            if (Keyboard.FocusedElement is not DataGridCell cell) return;

            var row = DataGridRow.GetRowContainingElement(cell);
            var itemData = row?.Item;
            if (itemData == null) return;

            string? propertyName = null;

            if (cell.Column is DataGridTextColumn textColumn && textColumn.Binding is Binding binding)
            {
                propertyName = binding.Path?.Path;
            }
            else
            {
                FrameworkElement? boundElement = FindBoundFrameworkElement<TextBox>(cell) as FrameworkElement;
                boundElement ??= FindBoundFrameworkElement<TextBlock>(cell) as FrameworkElement;

                if (boundElement != null)
                {
                    if (BindingOperations.GetBinding(boundElement, GetDependencyProperty(boundElement)) is Binding innerBinding)
                    {
                        propertyName = innerBinding.Path?.Path;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(propertyName))
            {
                propertyName = MapToWritableProperty(propertyName);
                var prop = itemData.GetType().GetProperty(propertyName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(itemData, newText);
                }
            }
        }

        //private static void OnPaste()
        //{
        //    if (!Clipboard.ContainsText()) return;

        //    string clipboardText = Clipboard.GetText();

        //    if (Keyboard.FocusedElement is not DataGridCell cell) return;

        //    var row = DataGridRow.GetRowContainingElement(cell);
        //    var itemData = row?.Item;
        //    if (itemData == null) return;

        //    string? propertyName = null;

        //    if (cell.Column is DataGridTextColumn textColumn && textColumn.Binding is Binding binding)
        //    {
        //        propertyName = binding.Path?.Path;
        //    }
        //    else
        //    {
        //        FrameworkElement? boundElement = FindBoundFrameworkElement<TextBox>(cell) as FrameworkElement;
        //        boundElement ??= FindBoundFrameworkElement<TextBlock>(cell) as FrameworkElement;

        //        if (boundElement != null)
        //        {
        //            if (BindingOperations.GetBinding(boundElement, GetDependencyProperty(boundElement)) is Binding innerBinding)
        //            {
        //                propertyName = innerBinding.Path?.Path;
        //            }
        //        }
        //    }

        //    if (!string.IsNullOrWhiteSpace(propertyName))
        //    {
        //        var prop = itemData.GetType().GetProperty(propertyName);
        //        if (prop != null && prop.CanWrite)
        //        {
        //            prop.SetValue(itemData, clipboardText);
        //        }
        //    }
        //}

        private void OnPaste()
        {
            if (!Clipboard.ContainsText())
                return;

            string text = Clipboard.GetText();

            if (Keyboard.FocusedElement is not DataGridCell cell)
                return;

            var grid = FindVisualParent<DataGrid>(cell);
            if (grid == null)
                return;

            grid.CurrentCell = new DataGridCellInfo(cell);
            grid.BeginEdit();

            var tb = FindVisualChild<TextBox>(cell);
            if (tb == null)
                return;

            tb.Text = text;

            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            grid.CommitEdit(DataGridEditingUnit.Cell, true);
        }

        /// <summary>
        /// Получает строковое значение из ячейки DataGrid в режиме отображения.
        /// </summary>
        private string? GetCellTextValue(DataGridCell cell)
        {
            if (FindVisualChild<TextBlock>(cell) is { } tb
                && !string.IsNullOrWhiteSpace(tb.Text))
                return tb.Text;

            if (FindVisualChild<CheckBox>(cell) is { } cb)
                return cb.IsChecked == true ? "True" : "False";

            return null;
        }



        private void PasteToCell(DataGrid grid, DataGridCellInfo cellInfo)
        {
            string text = Clipboard.GetText()
                .TrimEnd('\r', '\n');

            grid.CurrentCell = cellInfo;
            grid.BeginEdit();

            var cell = GetDataGridCell(grid, cellInfo);
            if (cell == null)
                return;

            var tb = FindVisualChild<TextBox>(cell);
            if (tb == null)
                return;

            tb.Text = text;

            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

            grid.CommitEdit(DataGridEditingUnit.Cell, true);
        }

        private static T? FindBoundFrameworkElement<T>(DependencyObject parent) where T : FrameworkElement
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && BindingOperations.IsDataBound(element, GetDependencyProperty(element)))
                {
                    return element;
                }

                var result = FindBoundFrameworkElement<T>(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private static DependencyProperty GetDependencyProperty(FrameworkElement element)
        {
            if (element is TextBox)
                return TextBox.TextProperty;
            if (element is TextBlock)
                return TextBlock.TextProperty;
            throw new NotSupportedException($"Неподдерживаемый тип: {element.GetType()}");
        }

        /// <summary>
        /// В режиме просмотра (не редактирования) ячейки MasterSetupDetail/MasterMachiningDetail
        /// показывают Effective*Detail (может отражать обоснование СГТ при переопределении причины —
        /// см. Part.EffectiveSetupDetail), а не сам комментарий мастера. Это read-only свойство, поэтому
        /// запись из контекстного меню нужно перенаправлять на исходное Master*Detail — иначе prop.CanWrite
        /// молчаливо отклоняет запись и пункт меню выглядит нерабочим.
        /// </summary>
        private static string MapToWritableProperty(string propertyName) => propertyName switch
        {
            nameof(Part.EffectiveSetupDetail) => nameof(Part.MasterSetupDetail),
            nameof(Part.EffectiveMachiningDetail) => nameof(Part.MasterMachiningDetail),
            _ => propertyName,
        };

        private static T FindVisualParent<T>(DependencyObject obj) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(obj);
            while (parent != null && parent is not T)
            {
                parent = VisualTreeHelper.GetParent(parent);
            }
            return (T)parent!;
        }

        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }

        private void ThoughtTextBlock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ThoughtScrollViewer != null)
                ThoughtScrollViewer.ScrollToBottom();
        }

        /// <summary>
        /// Кнопка-пикер даты в группе смены дат: выбранная дата уходит сразу в
        /// оба календаря (От и До), попап закрывается.
        /// </summary>
        private void SpecificDateCalendar_SelectedDatesChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is Calendar { SelectedDate: DateTime date }
                && DataContext is PartsInfoWindowViewModel vm)
            {
                vm.SetSpecificDateCommand.Execute(date);
            }
            PickDateToggle.IsChecked = false;
        }

        private void Chip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount >= 2 && sender is FrameworkElement { DataContext: FilterChip chip })
            {
                var vm = DataContext as PartsInfoWindowViewModel;
                vm?.EditChipFilter(chip);
            }
        }
    }
}
