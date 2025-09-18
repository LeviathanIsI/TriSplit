using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace TriSplit.Desktop.Views.Tabs;

public partial class TestView : UserControl
{
    private const int WM_MOUSEHWHEEL = 0x020E;

    public TestView()
    {
        InitializeComponent();
        Loaded += TestView_Loaded;
    }

    private void TestView_Loaded(object sender, RoutedEventArgs e)
    {
        // Hook into window messages to capture horizontal mouse wheel
        var window = Window.GetWindow(this);
        if (window != null)
        {
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            source?.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEHWHEEL)
        {
            // Handle horizontal wheel event
            // Use ToInt64 to avoid overflow on 64-bit systems, then safely extract the high word
            long wparam = wParam.ToInt64();
            int delta = (short)((wparam >> 16) & 0xFFFF);

            // Find the DataGrid's ScrollViewer
            var scrollViewer = GetScrollViewer(PreviewDataGrid);
            if (scrollViewer != null && scrollViewer.IsMouseOver)
            {
                double offset = scrollViewer.HorizontalOffset;
                // Delta is positive for left tilt, negative for right tilt
                // Reversed: left tilt should scroll left, right tilt should scroll right
                scrollViewer.ScrollToHorizontalOffset(offset + (delta > 0 ? 30 : -30));
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void DataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is DataGrid dataGrid)
        {
            var scrollViewer = GetScrollViewer(dataGrid);
            if (scrollViewer != null)
            {
                // Support Shift+Wheel for horizontal scrolling
                if (e.Delta != 0 && (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)))
                {
                    // Scroll horizontally with Shift+Wheel
                    double offset = scrollViewer.HorizontalOffset;
                    scrollViewer.ScrollToHorizontalOffset(offset + (e.Delta < 0 ? 30 : -30));
                    e.Handled = true;
                }
            }
        }
    }

    private ScrollViewer? GetScrollViewer(DependencyObject obj)
    {
        if (obj is ScrollViewer viewer)
            return viewer;

        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(obj); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(obj, i);
            var result = GetScrollViewer(child);
            if (result != null)
                return result;
        }

        return null;
    }
}