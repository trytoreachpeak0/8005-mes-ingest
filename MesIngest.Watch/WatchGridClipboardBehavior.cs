using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MesIngest.Watch;

/// <summary>
/// Wires Ctrl+C and context-menu copy actions onto a Watch DataGrid.
/// </summary>
internal static class WatchGridClipboardBehavior
{
    public static void Attach(DataGrid grid)
    {
        grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        grid.ClipboardCopyMode = DataGridClipboardCopyMode.None;
        if (grid.HeadersVisibility == DataGridHeadersVisibility.Column)
        {
            grid.HeadersVisibility = DataGridHeadersVisibility.All;
        }

        grid.CommandBindings.Add(new CommandBinding(
            ApplicationCommands.Copy,
            (_, e) =>
            {
                CopySelection(grid);
                e.Handled = true;
            },
            (_, e) =>
            {
                e.CanExecute = HasCopyableSelection(grid);
                e.Handled = true;
            }));

        grid.PreviewMouseRightButtonDown += (_, e) => SelectUnderMouse(grid, e);

        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("复制单元格", () => CopyCell(grid)));
        menu.Items.Add(CreateMenuItem("复制整行", () => CopyRow(grid, includeHeaders: false)));
        menu.Items.Add(CreateMenuItem("复制整行（含列名）", () => CopyRow(grid, includeHeaders: true)));
        grid.ContextMenu = menu;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static bool HasCopyableSelection(DataGrid grid) =>
        grid.CurrentCell.Item is not null || grid.SelectedItems.Count > 0;

    private static void CopySelection(DataGrid grid)
    {
        if (IsRowHeaderSelection(grid))
        {
            CopyRow(grid, includeHeaders: false);
            return;
        }

        CopyCell(grid);
    }

    private static bool IsRowHeaderSelection(DataGrid grid)
    {
        if (grid.SelectedItems.Count == 0)
        {
            return false;
        }

        // Row-header click selects the item and fills SelectedCells with every visible column.
        var visibleColumns = VisibleColumns(grid).Count();
        return visibleColumns > 0 && grid.SelectedCells.Count >= visibleColumns;
    }

    private static void CopyCell(DataGrid grid)
    {
        var cell = ResolveCell(grid);
        if (cell is null)
        {
            return;
        }

        var value = ReadCellValue(cell.Value.Item, cell.Value.Column);
        SetClipboardText(WatchGridClipboard.FormatValue(value));
    }

    private static void CopyRow(DataGrid grid, bool includeHeaders)
    {
        var item = ResolveRowItem(grid);
        if (item is null)
        {
            return;
        }

        var headers = new List<string>();
        var values = new List<object?>();
        foreach (var column in VisibleColumns(grid))
        {
            headers.Add(column.Header?.ToString() ?? string.Empty);
            values.Add(ReadCellValue(item, column));
        }

        var text = includeHeaders
            ? WatchGridClipboard.FormatRowWithHeaders(headers, values)
            : WatchGridClipboard.FormatRow(values);
        SetClipboardText(text);
    }

    private static object? ResolveRowItem(DataGrid grid)
    {
        if (grid.CurrentCell.Item is not null)
        {
            return grid.CurrentCell.Item;
        }

        return grid.SelectedItems.Count > 0 ? grid.SelectedItems[0] : null;
    }

    private static DataGridCellInfo? ResolveCell(DataGrid grid)
    {
        if (grid.CurrentCell.Item is not null && grid.CurrentCell.Column is not null)
        {
            return grid.CurrentCell;
        }

        if (grid.SelectedCells.Count > 0)
        {
            return grid.SelectedCells[0];
        }

        return null;
    }

    private static IEnumerable<DataGridColumn> VisibleColumns(DataGrid grid) =>
        grid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex);

    private static object? ReadCellValue(object rowItem, DataGridColumn column)
    {
        if (column is DataGridBoundColumn bound
            && bound.Binding is Binding binding
            && !string.IsNullOrWhiteSpace(binding.Path?.Path))
        {
            return WatchGridClipboard.ReadProperty(rowItem, binding.Path.Path);
        }

        return null;
    }

    private static void SelectUnderMouse(DataGrid grid, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null
               && source is not DataGridCell
               && source is not DataGridRowHeader
               && source is not DataGrid)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridCell cell)
        {
            grid.Focus();
            grid.SelectedCells.Clear();
            var info = new DataGridCellInfo(cell);
            grid.CurrentCell = info;
            if (!grid.SelectedCells.Contains(info))
            {
                grid.SelectedCells.Add(info);
            }

            cell.Focus();
            e.Handled = true;
            return;
        }

        if (source is DataGridRowHeader)
        {
            var row = FindAncestor<DataGridRow>(source);
            if (row?.Item is null)
            {
                return;
            }

            grid.Focus();
            grid.SelectedCells.Clear();
            grid.SelectedItem = row.Item;
            var columns = VisibleColumns(grid).ToList();
            foreach (var column in columns)
            {
                grid.SelectedCells.Add(new DataGridCellInfo(row.Item, column));
            }

            grid.CurrentCell = new DataGridCellInfo(row.Item, columns.FirstOrDefault());
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can be locked by other apps; never break the watch loop.
        }
    }
}
