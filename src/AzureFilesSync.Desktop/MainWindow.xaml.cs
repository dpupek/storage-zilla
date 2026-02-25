using AzureFilesSync.Core.Models;
using AzureFilesSync.Desktop.Dialogs;
using AzureFilesSync.Desktop.ViewModels;
using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace AzureFilesSync.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            ApplyLayout(LocalGrid, GetLocalColumnMap(), vm.LocalGridLayout);
            ApplyLayout(RemoteGrid, GetRemoteColumnMap(), vm.RemoteGridLayout);
        }

        await PersistLayoutsAsync();
    }

    private async void LocalUploadStartNow_Click(object sender, RoutedEventArgs e) => await QueueLocalAsync(startImmediately: true);
    private async void LocalUploadAddToQueue_Click(object sender, RoutedEventArgs e) => await QueueLocalAsync(startImmediately: false);
    private async void RemoteDownloadStartNow_Click(object sender, RoutedEventArgs e) => await QueueRemoteAsync(startImmediately: true);
    private async void RemoteDownloadAddToQueue_Click(object sender, RoutedEventArgs e) => await QueueRemoteAsync(startImmediately: false);
    private async void LocalShowInExplorer_Click(object sender, RoutedEventArgs e) => await RunLocalEntryActionAsync(vm => vm.ShowInExplorerAsync(LocalGrid.SelectedItem as LocalEntry), "Show in Explorer failed.");
    private async void LocalOpen_Click(object sender, RoutedEventArgs e) => await RunLocalEntryActionAsync(vm => vm.OpenLocalAsync(LocalGrid.SelectedItem as LocalEntry), "Open failed.");
    private async void LocalOpenWith_Click(object sender, RoutedEventArgs e) => await RunLocalEntryActionAsync(vm => vm.OpenLocalWithAsync(LocalGrid.SelectedItem as LocalEntry), "Open with failed.");
    private async void LocalRename_Click(object sender, RoutedEventArgs e) => await RenameLocalAsync();
    private async void LocalDelete_Click(object sender, RoutedEventArgs e) => await DeleteLocalAsync();
    private async void RemoteRename_Click(object sender, RoutedEventArgs e) => await RenameRemoteAsync();
    private async void RemoteDelete_Click(object sender, RoutedEventArgs e) => await DeleteRemoteAsync();

    private async Task QueueLocalAsync(bool startImmediately)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var selected = LocalGrid.SelectedItems.Count > 0
            ? LocalGrid.SelectedItems
            : BuildFallbackSelection(LocalGrid.SelectedItem);
        await vm.QueueLocalSelectionAsync(selected, startImmediately);
    }

    private async Task QueueRemoteAsync(bool startImmediately)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var selected = RemoteGrid.SelectedItems.Count > 0
            ? RemoteGrid.SelectedItems
            : BuildFallbackSelection(RemoteGrid.SelectedItem);
        await vm.QueueRemoteSelectionAsync(selected, startImmediately);
    }

    private static IList BuildFallbackSelection(object? selectedItem)
    {
        var list = new ArrayList();
        if (selectedItem is not null)
        {
            list.Add(selectedItem);
        }

        return list;
    }

    private async Task RunLocalEntryActionAsync(Func<MainViewModel, Task> action, string errorMessage)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        try
        {
            await action(vm);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show(errorMessage, ex);
        }
    }

    private async Task RenameLocalAsync()
    {
        if (DataContext is not MainViewModel vm || LocalGrid.SelectedItem is not LocalEntry entry || entry.Name == "..")
        {
            return;
        }

        if (!RenamePromptWindow.TryShow(this, "Rename Local Entry", entry.Name, out var newName))
        {
            return;
        }

        try
        {
            await vm.RenameLocalAsync(entry, newName);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show("Failed to rename local entry.", ex);
        }
    }

    private async Task DeleteLocalAsync()
    {
        if (DataContext is not MainViewModel vm || LocalGrid.SelectedItem is not LocalEntry entry || entry.Name == "..")
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete '{entry.Name}'{(entry.IsDirectory ? " recursively" : string.Empty)}?",
            "Delete Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await vm.DeleteLocalAsync(entry, recursive: true);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show("Failed to delete local entry.", ex);
        }
    }

    private async Task RenameRemoteAsync()
    {
        if (DataContext is not MainViewModel vm || RemoteGrid.SelectedItem is not RemoteEntry entry || entry.Name == "..")
        {
            return;
        }

        if (!RenamePromptWindow.TryShow(this, "Rename Remote Entry", entry.Name, out var newName))
        {
            return;
        }

        try
        {
            await vm.RenameRemoteAsync(entry, newName);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show("Failed to rename remote entry.", ex);
        }
    }

    private async Task DeleteRemoteAsync()
    {
        if (DataContext is not MainViewModel vm || RemoteGrid.SelectedItem is not RemoteEntry entry || entry.Name == "..")
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Delete remote '{entry.Name}'{(entry.IsDirectory ? " recursively" : string.Empty)}?",
            "Delete Confirmation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await vm.DeleteRemoteAsync(entry, recursive: true);
        }
        catch (Exception ex)
        {
            ErrorDialog.Show("Failed to delete remote entry.", ex);
        }
    }

    private async void LocalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.OpenLocalEntryAsync(LocalGrid.SelectedItem as LocalEntry);
    }

    private async void RemoteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        await vm.OpenRemoteEntryAsync(RemoteGrid.SelectedItem as RemoteEntry);
    }

    private async void LocalColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleColumnVisibility(sender, GetLocalColumnMap());
        await PersistLayoutsAsync();
    }

    private async void RemoteColumnToggle_Click(object sender, RoutedEventArgs e)
    {
        ToggleColumnVisibility(sender, GetRemoteColumnMap());
        await PersistLayoutsAsync();
    }

    private static void ToggleColumnVisibility(object sender, Dictionary<string, DataGridColumn> columns)
    {
        if (sender is not MenuItem menu || menu.Tag is not string key || !columns.TryGetValue(key, out var column))
        {
            return;
        }

        column.Visibility = menu.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LocalGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        ApplyGridSort(LocalGrid, e.Column);
        await PersistLayoutsAsync();
    }

    private async void RemoteGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        ApplyGridSort(RemoteGrid, e.Column);
        await PersistLayoutsAsync();
    }

    private static void ApplyGridSort(DataGrid grid, DataGridColumn column)
    {
        var direction = column.SortDirection != ListSortDirection.Ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;

        foreach (var other in grid.Columns)
        {
            if (!ReferenceEquals(other, column))
            {
                other.SortDirection = null;
            }
        }

        column.SortDirection = direction;
        var sortMember = column.SortMemberPath;
        if (string.IsNullOrWhiteSpace(sortMember))
        {
            sortMember = (column.Header?.ToString() ?? "Name") switch
            {
                "Size" => "Length",
                "Modified" => "LastWriteTime",
                "Type" => "IsDirectory",
                _ => "Name"
            };
        }

        if (CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
        {
            view.CustomSort = new EntrySortComparer(sortMember, direction);
        }
    }

    private async Task PersistLayoutsAsync()
    {
        if (DataContext is not MainViewModel vm)
        {
            return;
        }

        var local = BuildLayout(LocalGrid, GetLocalColumnMap());
        var remote = BuildLayout(RemoteGrid, GetRemoteColumnMap());
        await vm.UpdateGridLayoutsAsync(local, remote);
    }

    private static GridLayoutProfile BuildLayout(DataGrid grid, Dictionary<string, DataGridColumn> map)
    {
        var columns = map
            .Select(x => new GridColumnProfile(
                x.Key,
                x.Value.Visibility == Visibility.Visible,
                x.Value.ActualWidth,
                x.Value.DisplayIndex))
            .ToList();

        var sortedColumn = grid.Columns.FirstOrDefault(x => x.SortDirection is not null);
        var sortColumn = sortedColumn is null ? null : map.FirstOrDefault(x => ReferenceEquals(x.Value, sortedColumn)).Key;
        var sortDirection = sortedColumn?.SortDirection;

        return new GridLayoutProfile(columns, sortColumn, sortDirection);
    }

    private static void ApplyLayout(DataGrid grid, Dictionary<string, DataGridColumn> map, GridLayoutProfile? layout)
    {
        if (layout is null)
        {
            return;
        }

        foreach (var profile in layout.Columns)
        {
            if (!map.TryGetValue(profile.Key, out var column))
            {
                continue;
            }

            column.Visibility = profile.Visible ? Visibility.Visible : Visibility.Collapsed;
            if (profile.Width > 0)
            {
                column.Width = new DataGridLength(profile.Width);
            }

            if (profile.DisplayIndex >= 0 && profile.DisplayIndex < grid.Columns.Count)
            {
                column.DisplayIndex = profile.DisplayIndex;
            }
        }

        foreach (var c in grid.Columns)
        {
            c.SortDirection = null;
        }

        if (!string.IsNullOrWhiteSpace(layout.SortColumn) && map.TryGetValue(layout.SortColumn, out var sortColumn) && layout.SortDirection is not null)
        {
            sortColumn.SortDirection = layout.SortDirection;
            var member = sortColumn.SortMemberPath;
            if (string.IsNullOrWhiteSpace(member))
            {
                member = "Name";
            }

            if (CollectionViewSource.GetDefaultView(grid.ItemsSource) is ListCollectionView view)
            {
                view.CustomSort = new EntrySortComparer(member, layout.SortDirection.Value);
            }
        }
    }

    private Dictionary<string, DataGridColumn> GetLocalColumnMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = LocalNameColumn,
        ["Type"] = LocalTypeColumn,
        ["Size"] = LocalSizeColumn,
        ["Modified"] = LocalModifiedColumn,
        ["Created"] = LocalCreatedColumn,
        ["Author"] = LocalAuthorColumn
    };

    private Dictionary<string, DataGridColumn> GetRemoteColumnMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Name"] = RemoteNameColumn,
        ["Type"] = RemoteTypeColumn,
        ["Size"] = RemoteSizeColumn,
        ["Modified"] = RemoteModifiedColumn,
        ["Created"] = RemoteCreatedColumn,
        ["Author"] = RemoteAuthorColumn
    };

    private sealed class EntrySortComparer : IComparer
    {
        private readonly string _member;
        private readonly ListSortDirection _direction;

        public EntrySortComparer(string member, ListSortDirection direction)
        {
            _member = member;
            _direction = direction;
        }

        public int Compare(object? x, object? y)
        {
            var rankCompare = CompareRank(x, y);
            if (rankCompare != 0)
            {
                return rankCompare;
            }

            var core = _member switch
            {
                "Length" => Nullable.Compare(GetLength(x), GetLength(y)),
                "LastWriteTime" => Nullable.Compare(GetLastWrite(x), GetLastWrite(y)),
                "CreatedTime" => Nullable.Compare(GetCreated(x), GetCreated(y)),
                "Author" => string.Compare(GetAuthor(x), GetAuthor(y), true, CultureInfo.CurrentCulture),
                "IsDirectory" => GetIsDirectory(y).CompareTo(GetIsDirectory(x)),
                _ => string.Compare(GetName(x), GetName(y), true, CultureInfo.CurrentCulture)
            };

            return _direction == ListSortDirection.Ascending ? core : -core;
        }

        private static int CompareRank(object? x, object? y)
        {
            var xRank = GetRank(x);
            var yRank = GetRank(y);
            return xRank.CompareTo(yRank);
        }

        private static int GetRank(object? item)
        {
            var name = GetName(item);
            if (name == "..")
            {
                return 0;
            }

            return GetIsDirectory(item) ? 1 : 2;
        }

        private static string GetName(object? item) => item switch
        {
            LocalEntry local => local.Name,
            RemoteEntry remote => remote.Name,
            _ => string.Empty
        };

        private static bool GetIsDirectory(object? item) => item switch
        {
            LocalEntry local => local.IsDirectory,
            RemoteEntry remote => remote.IsDirectory,
            _ => false
        };

        private static long? GetLength(object? item) => item switch
        {
            LocalEntry local => local.Length,
            RemoteEntry remote => remote.Length,
            _ => null
        };

        private static DateTimeOffset? GetLastWrite(object? item) => item switch
        {
            LocalEntry local => local.LastWriteTime,
            RemoteEntry remote => remote.LastWriteTime,
            _ => null
        };

        private static DateTimeOffset? GetCreated(object? item) => item switch
        {
            LocalEntry local => local.CreatedTime,
            RemoteEntry remote => remote.CreatedTime,
            _ => null
        };

        private static string? GetAuthor(object? item) => item switch
        {
            LocalEntry local => local.Author,
            RemoteEntry remote => remote.Author,
            _ => null
        };
    }
}
