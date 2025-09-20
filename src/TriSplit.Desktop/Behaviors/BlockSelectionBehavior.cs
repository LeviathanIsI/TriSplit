
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TriSplit.Desktop.ViewModels.Tabs;

namespace TriSplit.Desktop.Behaviors;

public static class BlockSelectionBehavior
{
    private sealed class SelectionState
    {
        public SelectionState(ProfilesViewModel viewModel, ItemsControl itemsControl, int anchorIndex, FrameworkElement handle)
        {
            ViewModel = viewModel;
            ItemsControl = itemsControl;
            AnchorIndex = anchorIndex;
            Handle = handle;
        }

        public ProfilesViewModel ViewModel { get; }
        public ItemsControl ItemsControl { get; }
        public int AnchorIndex { get; }
        public FrameworkElement Handle { get; }
        public bool HasMoved { get; set; }
    }

    private static SelectionState? _currentState;

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable",
        typeof(bool),
        typeof(BlockSelectionBehavior),
        new PropertyMetadata(false, OnEnableChanged));

    public static bool GetEnable(FrameworkElement element) => (bool)element.GetValue(EnableProperty);
    public static void SetEnable(FrameworkElement element, bool value) => element.SetValue(EnableProperty, value);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if ((bool)e.OldValue)
        {
            element.PreviewMouseLeftButtonDown -= OnHandleMouseDown;
            element.PreviewMouseMove -= OnHandleMouseMove;
            element.PreviewMouseLeftButtonUp -= OnHandleMouseUp;
            element.LostMouseCapture -= OnHandleLostCapture;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseLeftButtonDown += OnHandleMouseDown;
            element.PreviewMouseMove += OnHandleMouseMove;
            element.PreviewMouseLeftButtonUp += OnHandleMouseUp;
            element.LostMouseCapture += OnHandleLostCapture;
        }
    }

    private static void OnHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle)
            return;

        var itemsControl = FindItemsOwner(handle);
        if (itemsControl == null)
            return;

        if (itemsControl.DataContext is not ProfilesViewModel viewModel)
            return;

        if (handle.DataContext is not FieldMappingViewModel mapping)
            return;

        var anchorIndex = itemsControl.Items.IndexOf(mapping);
        if (anchorIndex < 0)
            return;

        if (viewModel.HasBlockClipboard)
        {
            viewModel.BeginBlockSelectionForPaste(anchorIndex);
        }
        else
        {
            viewModel.BeginBlockSelection(anchorIndex);
        }

        _currentState = new SelectionState(viewModel, itemsControl, anchorIndex, handle);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private static void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_currentState == null || _currentState.ItemsControl == null)
            return;

        if (_currentState.Handle.IsMouseCaptured)
        {
            var itemsControl = _currentState.ItemsControl;
            itemsControl.UpdateLayout();

            var position = Mouse.GetPosition(itemsControl);
            var index = GetIndexFromPosition(itemsControl, position);
            if (index >= 0)
            {
                _currentState.HasMoved = true;
                _currentState.ViewModel.UpdateBlockSelection(_currentState.AnchorIndex, index);
            }
        }
    }

    private static void OnHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_currentState == null)
            return;

        FinishSelection();
        e.Handled = true;
    }

    private static void OnHandleLostCapture(object sender, MouseEventArgs e)
    {
        FinishSelection();
    }

    private static void FinishSelection()
    {
        if (_currentState == null)
            return;

        var state = _currentState;

        if (state.Handle.IsMouseCaptured)
        {
            state.Handle.ReleaseMouseCapture();
        }

        _currentState = null;
        state.ViewModel?.CompleteBlockSelection();
    }

    private static ItemsControl? FindItemsOwner(DependencyObject? start)
    {
        var current = start;
        while (current != null)
        {
            if (current is ItemsControl itemsControl && itemsControl is not ComboBox)
            {
                return itemsControl;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static int GetIndexFromPosition(ItemsControl itemsControl, Point position)
    {
        for (int i = 0; i < itemsControl.Items.Count; i++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container)
            {
                var topLeft = container.TranslatePoint(new Point(0, 0), itemsControl);
                var bounds = new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight));
                if (bounds.Contains(position))
                    return i;
            }
        }

        if (itemsControl.Items.Count > 0)
        {
            if (position.Y < 0)
                return 0;

            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(itemsControl.Items.Count - 1) is FrameworkElement last)
            {
                var bottom = last.TranslatePoint(new Point(0, last.ActualHeight), itemsControl).Y;
                if (position.Y > bottom)
                    return itemsControl.Items.Count - 1;
            }
        }

        return -1;
    }
}
