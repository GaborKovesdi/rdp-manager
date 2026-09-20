using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RdpManager.Models;

namespace RdpManager.Views;

/// <summary>
/// Lets the rows of a DataGrid be reordered by dragging them with the mouse. A thin line shows
/// where the row will land; the bound collection is reordered in place on drop.
/// </summary>
public sealed class RowDragReorder
{
    private const string DataFormat = "RdpManager.ConnectionRow";
    private const double AutoScrollEdge = 28;

    private readonly DataGrid _grid;
    private readonly ObservableCollection<RdpConnection> _items;
    private readonly FrameworkElement _indicator;
    private readonly Action<RdpConnection> _moved;

    private RdpConnection? _pressedItem;
    private Point _pressPosition;
    private bool _dragging;

    /// <param name="indicator">A thin element sharing a Grid with <paramref name="grid"/>, used as the drop line.</param>
    /// <param name="moved">Called after a drop actually changed the order.</param>
    public RowDragReorder(DataGrid grid, ObservableCollection<RdpConnection> items, FrameworkElement indicator, Action<RdpConnection> moved)
    {
        _grid = grid;
        _items = items;
        _indicator = indicator;
        _moved = moved;

        _grid.AllowDrop = true;
        _grid.PreviewMouseLeftButtonDown += OnMouseDown;
        _grid.PreviewMouseLeftButtonUp += (_, _) => _pressedItem = null;
        _grid.PreviewMouseMove += OnMouseMove;
        _grid.DragOver += OnDragOver;
        _grid.DragLeave += (_, _) => HideIndicator();
        _grid.Drop += OnDrop;
    }

    /// <summary>
    /// Where a row ends up when it is dropped in front of the row at <paramref name="insertionIndex"/>
    /// (which may equal the item count, meaning "at the end"). Removing the row first shifts
    /// everything after it up by one, so a downward move lands one slot earlier.
    /// </summary>
    public static int ComputeTargetIndex(int oldIndex, int insertionIndex) =>
        oldIndex < insertionIndex ? insertionIndex - 1 : insertionIndex;

    /// <summary>The connection whose row contains this element, or null if it isn't on a row.</summary>
    public static RdpConnection? FindRowItem(DependencyObject? source) =>
        FindAncestor<DataGridRow>(source)?.Item as RdpConnection;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressedItem = FindRowItem(e.OriginalSource as DependencyObject);

        if (_pressedItem is not null)
            _pressPosition = e.GetPosition(_grid);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedItem is null || _dragging || e.LeftButton != MouseButtonState.Pressed)
            return;

        var moved = e.GetPosition(_grid) - _pressPosition;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var item = _pressedItem;
        _pressedItem = null;
        _dragging = true;

        try
        {
            DragDrop.DoDragDrop(_grid, new DataObject(DataFormat, item), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            HideIndicator();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DataFormat) is not RdpConnection item)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        AutoScroll(e);

        var insertion = GetInsertionIndex(e);
        var oldIndex = _items.IndexOf(item);

        if (oldIndex < 0 || ComputeTargetIndex(oldIndex, insertion) == oldIndex)
            HideIndicator();
        else
            ShowIndicator(insertion);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        HideIndicator();

        if (e.Data.GetData(DataFormat) is not RdpConnection item)
            return;

        e.Handled = true;

        var oldIndex = _items.IndexOf(item);
        if (oldIndex < 0)
            return;

        var target = ComputeTargetIndex(oldIndex, GetInsertionIndex(e));
        if (target == oldIndex)
            return;

        _items.Move(oldIndex, target);
        _moved(item);
    }

    /// <summary>The index of the row the dragged item would be dropped in front of.</summary>
    private int GetInsertionIndex(DragEventArgs e)
    {
        var insertion = 0;

        for (var i = 0; i < _items.Count; i++)
        {
            // Rows scrolled out of view have no container; they can't be under the pointer.
            if (_grid.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow row)
                continue;

            if (e.GetPosition(row).Y < row.ActualHeight / 2)
                return i;

            insertion = i + 1;
        }

        return insertion;
    }

    private void ShowIndicator(int insertion)
    {
        if (VisualTreeHelper.GetParent(_indicator) is not Visual host)
            return;

        double y;
        if (_grid.ItemContainerGenerator.ContainerFromIndex(insertion) is DataGridRow before)
            y = before.TransformToAncestor(host).Transform(new Point(0, 0)).Y;
        else if (insertion > 0 && _grid.ItemContainerGenerator.ContainerFromIndex(insertion - 1) is DataGridRow after)
            y = after.TransformToAncestor(host).Transform(new Point(0, after.ActualHeight)).Y;
        else
        {
            HideIndicator();
            return;
        }

        _indicator.Margin = new Thickness(0, y - _indicator.Height / 2, 0, 0);
        _indicator.Visibility = Visibility.Visible;
    }

    private void HideIndicator() => _indicator.Visibility = Visibility.Collapsed;

    /// <summary>Scrolls the list a row at a time while the pointer hovers near its top or bottom edge.</summary>
    private void AutoScroll(DragEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(_grid) is not { } scroller)
            return;

        var y = e.GetPosition(scroller).Y;
        if (y < AutoScrollEdge)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - 1);
        else if (y > scroller.ActualHeight - AutoScrollEdge)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + 1);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
                return match;

            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            if (FindDescendant<T>(child) is { } nested)
                return nested;
        }

        return null;
    }
}
