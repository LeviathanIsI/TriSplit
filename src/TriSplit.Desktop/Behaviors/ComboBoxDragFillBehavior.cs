
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TriSplit.Desktop.ViewModels.Tabs;

namespace TriSplit.Desktop.Behaviors;

public static class ComboBoxDragFillBehavior
{
    private sealed class RowInfo
    {
        public RowInfo(FieldMappingViewModel mapping, ComboBox combo, FrameworkElement container, Rect bounds)
        {
            Mapping = mapping;
            Combo = combo;
            Container = container;
            Bounds = bounds;
        }

        public FieldMappingViewModel Mapping { get; }
        public ComboBox Combo { get; }
        public FrameworkElement Container { get; }
        public Rect Bounds { get; }
    }

    private sealed class DragState
    {
        public DragState(ComboBox origin, FrameworkElement startElement, string propertyName, string value, ItemsControl itemsControl)
        {
            Origin = origin;
            StartElement = startElement;
            PropertyName = propertyName;
            Value = value;
            ItemsControl = itemsControl;
            RootWindow = Window.GetWindow(startElement);
        }

        public ComboBox Origin { get; }
        public FrameworkElement StartElement { get; }
        public string PropertyName { get; }
        public string Value { get; }
        public ItemsControl ItemsControl { get; }
        public Window? RootWindow { get; }
        public Point StartPoint { get; set; }
        public bool IsDragging { get; set; }
        public List<RowInfo> Rows { get; } = new();
        public int OriginIndex { get; set; } = -1;
        public HashSet<ComboBox> HighlightedCombos { get; } = new();
        public HashSet<FrameworkElement> HighlightedHandles { get; } = new();
    }

    private const double DragThreshold = 4.0;
    private static DragState? _currentState;

    private static readonly Dictionary<FrameworkElement, ComboBox> HandleLookup = new();
    private static readonly Dictionary<ComboBox, List<FrameworkElement>> ComboHandles = new();

#if DEBUG
    private static readonly object LogSync = new();
    private static readonly string LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TriSplit", "Debug");
    private static readonly string LogPath = Path.Combine(LogDirectory, "combo_drag_fill.log");
    private static void LogDebug(string message)
    {
        try
        {
            lock (LogSync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            Debug.WriteLine(message);
        }
    }
#else
    private static void LogDebug(string message) { }
#endif

    public static readonly DependencyProperty FillPropertyNameProperty = DependencyProperty.RegisterAttached(
        "FillPropertyName",
        typeof(string),
        typeof(ComboBoxDragFillBehavior),
        new PropertyMetadata(null, OnFillPropertyNameChanged));

    public static readonly DependencyProperty HandleForProperty = DependencyProperty.RegisterAttached(
        "HandleFor",
        typeof(ComboBox),
        typeof(ComboBoxDragFillBehavior),
        new PropertyMetadata(null, OnHandleForChanged));

    public static readonly DependencyProperty IsDragHighlightedProperty = DependencyProperty.RegisterAttached(
        "IsDragHighlighted",
        typeof(bool),
        typeof(ComboBoxDragFillBehavior),
        new PropertyMetadata(false));

    public static string? GetFillPropertyName(ComboBox comboBox) => (string?)comboBox.GetValue(FillPropertyNameProperty);
    public static void SetFillPropertyName(ComboBox comboBox, string? value) => comboBox.SetValue(FillPropertyNameProperty, value);

    public static ComboBox? GetHandleFor(FrameworkElement element) => element.GetValue(HandleForProperty) as ComboBox;
    public static void SetHandleFor(FrameworkElement element, ComboBox? value) => element.SetValue(HandleForProperty, value);

    public static bool GetIsDragHighlighted(DependencyObject obj) => (bool)obj.GetValue(IsDragHighlightedProperty);
    public static void SetIsDragHighlighted(DependencyObject obj, bool value) => obj.SetValue(IsDragHighlightedProperty, value);

    private static void OnFillPropertyNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Intentionally left blank. Fill property is processed when drag begins.
    }

    private static void OnHandleForChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        if (e.OldValue is ComboBox oldCombo)
        {
            UnregisterHandle(element, oldCombo);
        }

        if (e.NewValue is ComboBox newCombo)
        {
            RegisterHandle(element, newCombo);
        }
    }

    private static void RegisterHandle(FrameworkElement element, ComboBox combo)
    {
        HandleLookup[element] = combo;
        if (!ComboHandles.TryGetValue(combo, out var handles))
        {
            handles = new List<FrameworkElement>();
            ComboHandles[combo] = handles;
        }

        if (!handles.Contains(element))
        {
            handles.Add(element);
        }

        element.PreviewMouseLeftButtonDown += OnHandlePreviewMouseLeftButtonDown;
        element.PreviewMouseMove += OnHandlePreviewMouseMove;
        element.PreviewMouseLeftButtonUp += OnHandlePreviewMouseLeftButtonUp;
        element.LostMouseCapture += OnHandleLostMouseCapture;
        element.Unloaded += OnHandleUnloaded;
    }

    private static void UnregisterHandle(FrameworkElement element, ComboBox? combo)
    {
        element.PreviewMouseLeftButtonDown -= OnHandlePreviewMouseLeftButtonDown;
        element.PreviewMouseMove -= OnHandlePreviewMouseMove;
        element.PreviewMouseLeftButtonUp -= OnHandlePreviewMouseLeftButtonUp;
        element.LostMouseCapture -= OnHandleLostMouseCapture;
        element.Unloaded -= OnHandleUnloaded;

        HandleLookup.Remove(element);

        if (combo != null && ComboHandles.TryGetValue(combo, out var handles))
        {
            handles.Remove(element);
            if (handles.Count == 0)
            {
                ComboHandles.Remove(combo);
            }
        }
    }

    private static void OnHandleUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var combo = HandleLookup.TryGetValue(element, out var mapped) ? mapped : null;
            UnregisterHandle(element, combo);
        }
    }

    private static void OnComboPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ComboBox combo)
        {
            TryBeginTracking(combo, combo, e.GetPosition(combo));
        }
    }

    private static void OnComboPreviewMouseMove(object sender, MouseEventArgs e) => HandleMouseMove(e);
    private static void OnComboPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();
    private static void OnComboLostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private static void OnHandlePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element && HandleLookup.TryGetValue(element, out var combo))
        {
            TryBeginTracking(combo, element, e.GetPosition(element));
            e.Handled = true;
        }
    }

    private static void OnHandlePreviewMouseMove(object sender, MouseEventArgs e) => HandleMouseMove(e);

    private static void OnHandlePreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private static void OnHandleLostMouseCapture(object sender, MouseEventArgs e) => EndDrag();

    private static void TryBeginTracking(ComboBox combo, FrameworkElement startElement, Point startPoint)
    {
        var propertyName = GetFillPropertyName(combo);
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            LogDebug("Drag aborted: no FillPropertyName configured");
            return;
        }

        if (combo.SelectedItem is not string value)
        {
            LogDebug("Drag aborted: combo has no selected value");
            return;
        }

        var itemsControl = FindItemsOwner(startElement) ?? FindItemsOwner(combo);

        if (itemsControl == null)
        {
            LogDebug("Drag aborted: failed to locate mapping ItemsControl");
            return;
        }

        var state = new DragState(combo, startElement, propertyName, value, itemsControl)
        {
            StartPoint = startPoint
        };

        if (state.RootWindow == null)
        {
            LogDebug("Drag aborted: unable to locate root window");
            return;
        }

        if (!startElement.CaptureMouse())
        {
            LogDebug("Drag aborted: failed to capture mouse");
            return;
        }

        _currentState = state;
        LogDebug($"Tracking started for property '{propertyName}' with value '{value}'");
    }

    private static void HandleMouseMove(MouseEventArgs e)
    {
        if (_currentState == null)
            return;

        if (Mouse.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        var state = _currentState;
        var currentPosition = Mouse.GetPosition(state.StartElement);
        var delta = currentPosition - state.StartPoint;

        if (!state.IsDragging)
        {
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            {
                return;
            }

            if (!BeginDrag(state))
            {
                EndDrag();
                return;
            }
        }

        if (state.ItemsControl == null || state.Rows.Count == 0)
            return;

        var position = Mouse.GetPosition(state.ItemsControl);
        var targetIndex = GetRowIndexAtPosition(state, position);
        if (targetIndex == -1)
            return;

        UpdateRange(state, targetIndex);
    }

    private static bool BeginDrag(DragState state)
    {
        if (state.IsDragging)
            return true;

        if (!state.StartElement.IsMouseCaptured && !state.StartElement.CaptureMouse())
            return false;

        state.IsDragging = true;
        state.Origin.IsDropDownOpen = false;

        LogDebug($"BeginDrag: itemsControl={state.ItemsControl?.GetType().Name ?? "null"}");
        BuildRowMetadata(state);
        UpdateRange(state, state.OriginIndex);

        LogDebug($"Drag begun: rows={state.Rows.Count}, originIndex={state.OriginIndex}, value='{state.Value}'");
        return true;
    }

    private static void EndDrag()
    {
        if (_currentState == null)
            return;

        var state = _currentState;

        if (state.StartElement.IsMouseCaptured)
        {
            state.StartElement.ReleaseMouseCapture();
        }

        ClearHighlights(state);
        LogDebug("Drag ended");
        _currentState = null;
    }

    private static void BuildRowMetadata(DragState state)
    {
        state.Rows.Clear();
        state.OriginIndex = -1;

        if (state.ItemsControl == null)
        {
            LogDebug("BuildRowMetadata: ItemsControl not found");
            return;
        }

        state.ItemsControl.UpdateLayout();

        LogDebug($"BuildRowMetadata: items={state.ItemsControl.Items.Count}, type={state.ItemsControl.GetType().Name}");

        foreach (var item in state.ItemsControl.Items)
        {
            if (item is null)
            {
                LogDebug("BuildRowMetadata: encountered null item");
                continue;
            }

            if (item is not FieldMappingViewModel mapping)
            {
                LogDebug($"BuildRowMetadata: item of type {item?.GetType().Name} skipped");
                continue;
            }

            var container = state.ItemsControl.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
            if (container == null)
            {
                LogDebug($"BuildRowMetadata: container not realized for mapping {mapping.SourceField}");
                continue;
            }

            var combo = FindDescendantCombo(container, state.PropertyName);
            if (combo == null)
            {
                LogDebug($"BuildRowMetadata: combo not found in container for property {state.PropertyName}");
                continue;
            }

            var topLeft = container.TranslatePoint(new Point(0, 0), state.ItemsControl);
            var bounds = new Rect(topLeft, new Size(container.ActualWidth, container.ActualHeight));
            state.Rows.Add(new RowInfo(mapping, combo, container, bounds));

            if (ReferenceEquals(combo, state.Origin))
            {
                state.OriginIndex = state.Rows.Count - 1;
            }
        }

        if (state.OriginIndex == -1 && state.Rows.Count > 0)
        {
            var fallbackIndex = state.Rows.FindIndex(r => ReferenceEquals(r.Mapping, state.Origin.DataContext));
            if (fallbackIndex >= 0)
            {
                state.OriginIndex = fallbackIndex;
            }
        }

        if (state.OriginIndex == -1 && state.Rows.Count > 0)
        {
            state.OriginIndex = 0;
        }

        LogDebug($"BuildRowMetadata complete: rows={state.Rows.Count}, originIndex={state.OriginIndex}");
    }

    private static int GetRowIndexAtPosition(DragState state, Point position)
    {
        if (state.Rows.Count == 0)
            return -1;

        for (int i = 0; i < state.Rows.Count; i++)
        {
            if (state.Rows[i].Bounds.Contains(position))
                return i;
        }

        if (position.Y < state.Rows[0].Bounds.Top)
            return 0;

        if (position.Y > state.Rows[^1].Bounds.Bottom)
            return state.Rows.Count - 1;

        return -1;
    }

    private static void UpdateRange(DragState state, int targetIndex)
    {
        if (state.Rows.Count == 0 || state.OriginIndex < 0)
            return;

        targetIndex = Math.Clamp(targetIndex, 0, state.Rows.Count - 1);
        var start = Math.Min(state.OriginIndex, targetIndex);
        var end = Math.Max(state.OriginIndex, targetIndex);

        ClearHighlights(state);

        for (int i = start; i <= end; i++)
        {
            var row = state.Rows[i];
            HighlightRow(state, row);
            ApplyValue(row, state);
        }

        LogDebug($"Range updated: start={start}, end={end}, value='{state.Value}'");
    }

    private static void HighlightRow(DragState state, RowInfo row)
    {
        if (state.HighlightedCombos.Add(row.Combo))
        {
            SetIsDragHighlighted(row.Combo, true);
        }

        if (ComboHandles.TryGetValue(row.Combo, out var handles))
        {
            foreach (var handle in handles)
            {
                if (handle == null)
                    continue;

                if (state.HighlightedHandles.Add(handle))
                {
                    SetIsDragHighlighted(handle, true);
                }
            }
        }
    }

    private static void ClearHighlights(DragState state)
    {
        foreach (var combo in state.HighlightedCombos)
        {
            SetIsDragHighlighted(combo, false);
        }
        state.HighlightedCombos.Clear();

        foreach (var handle in state.HighlightedHandles)
        {
            SetIsDragHighlighted(handle, false);
        }
        state.HighlightedHandles.Clear();
    }

    private static void ApplyValue(RowInfo row, DragState state)
    {
        var mapping = row.Mapping;
        var value = state.Value;

        switch (state.PropertyName)
        {
            case nameof(FieldMappingViewModel.AssociationLabel):
                if (!string.Equals(mapping.AssociationLabel, value, StringComparison.Ordinal))
                {
                    mapping.AssociationLabel = value;
                }
                break;
            case nameof(FieldMappingViewModel.PropertyGroup):
                if (!string.Equals(mapping.PropertyGroup, value, StringComparison.Ordinal))
                {
                    mapping.PropertyGroup = value;
                }
                break;
            case nameof(FieldMappingViewModel.ObjectType):
                if (!string.Equals(mapping.ObjectType, value, StringComparison.Ordinal))
                {
                    mapping.ObjectType = value;
                }
                break;
            default:
                return;
        }

        if (!Equals(row.Combo.SelectedItem, value))
        {
            row.Combo.SelectedItem = value;
        }
    }

    private static ComboBox? FindDescendantCombo(DependencyObject root, string propertyName)
    {
        if (root is ComboBox combo && string.Equals(GetFillPropertyName(combo), propertyName, StringComparison.Ordinal))
        {
            return combo;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindDescendantCombo(child, propertyName);
            if (result != null)
                return result;
        }

        return null;
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

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T typed)
                return typed;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }
}
