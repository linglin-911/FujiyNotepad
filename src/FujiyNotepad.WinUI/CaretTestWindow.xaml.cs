using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using WinRT.Interop;

namespace FujiyNotepad.WinUI;

/// <summary>
/// Isolated experiment for a WPF-inspired caret: a XAML-drawn visual caret plus
/// a transparent Win32 caret whose position is kept in sync for accessibility.
/// This window is deliberately not used by the production annotation editors.
/// </summary>
public sealed partial class CaretTestWindow : Window
{
    private readonly DispatcherQueueTimer blinkTimer;
    private readonly IntPtr windowHandle;
    private ScrollViewer? editorScrollViewer;
    private IntPtr transparentCaretBitmap;
    private bool transparentCaretCreated;
    private int transparentCaretHeightPixels;
    private bool useCustomCaret;
    private bool customCaretVisible;
    private bool updateQueued;
    private bool pendingResetBlink;
    private bool pendingForceRecreate;
    private bool initialized;
    private bool closing;
    private uint blinkIntervalMilliseconds = 500;
    private string lastSystemCaretStatus = "尚未启用自绘光标。";

    public CaretTestWindow()
    {
        InitializeComponent();

        windowHandle = WindowNative.GetWindowHandle(this);

        blinkTimer = DispatcherQueue.CreateTimer();
        blinkTimer.IsRepeating = true;
        blinkTimer.Interval = TimeSpan.FromMilliseconds(500);
        blinkTimer.Tick += BlinkTimer_Tick;

        EditorTextBox.Text =
            "FujiyNotepad 光标技术测试。\r\n" +
            "请在这里输入中文、English、数字 123，以及标点符号。\r\n" +
            "尝试使用方向键移动光标，使用 Shift + 方向键选择文字。\r\n" +
            "也可以点击文本中间，观察自绘光标是否准确跟随。";

        EditorTextBox.Loaded += EditorTextBox_Loaded;
        RootGrid.SizeChanged += RootGrid_SizeChanged;
        Closed += CaretTestWindow_Closed;

        initialized = true;
        UpdateModeState();
    }

    private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized)
            return;

        UpdateModeState();
    }

    private void UpdateModeState()
    {
        useCustomCaret = ModeSelector.SelectedIndex == 1;

        if (!useCustomCaret)
        {
            blinkTimer.Stop();
            CustomCaret.Visibility = Visibility.Collapsed;
            ReleaseTransparentSystemCaret();
            ModeStatusText.Text = "当前模式：原生光标（对照）。系统光标行为保持不变。";
            SystemCaretStatusText.Text = "自绘光标已关闭；未创建透明 Win32 光标。";
            return;
        }

        ModeStatusText.Text = "当前模式：WPF 式双层光标。请点击下方输入框开始测试。";
        SystemCaretStatusText.Text = "等待输入框获得焦点后创建透明 Win32 光标。";

        if (EditorTextBox.FocusState != FocusState.Unfocused)
            QueueCaretRefresh(resetBlink: true, forceRecreate: true);
    }

    private void EditorTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        editorScrollViewer = FindDescendant<ScrollViewer>(EditorTextBox);
        if (editorScrollViewer is not null)
            editorScrollViewer.ViewChanged += EditorScrollViewer_ViewChanged;

        QueueCaretRefresh(resetBlink: false);
    }

    private void EditorTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!initialized || !useCustomCaret)
            return;

        // Run after the TextBox has completed its own focus/caret initialization.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closing || !useCustomCaret || EditorTextBox.FocusState == FocusState.Unfocused)
                return;

            RefreshCaretGeometry(forceRecreate: true);
            ResetBlinking();
        });
    }

    private void EditorTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        blinkTimer.Stop();
        CustomCaret.Visibility = Visibility.Collapsed;
        ReleaseTransparentSystemCaret();

        if (useCustomCaret)
            SystemCaretStatusText.Text = "输入框已失去焦点；透明 Win32 光标已释放。";
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: true);
    }

    private void EditorTextBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: true);
    }

    private void EditorTextBox_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: true);
    }

    private void EditorTextBox_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: true);
    }

    private void EditorTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: false);
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: false);
    }

    private void EditorScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        QueueCaretRefresh(resetBlink: false);
    }

    private void QueueCaretRefresh(bool resetBlink, bool forceRecreate = false)
    {
        if (!initialized || closing || !useCustomCaret)
            return;

        // Coalesce events, but never discard a later request's stronger requirements.
        // TextChanged/SelectionChanged can arrive before TextBox has completed layout;
        // a low-priority dispatch lets the current input/layout pass settle first.
        pendingResetBlink |= resetBlink;
        pendingForceRecreate |= forceRecreate;
        if (updateQueued)
            return;

        updateQueued = true;
        bool queued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            updateQueued = false;
            bool shouldResetBlink = pendingResetBlink;
            bool shouldForceRecreate = pendingForceRecreate;
            pendingResetBlink = false;
            pendingForceRecreate = false;

            if (closing || !useCustomCaret || EditorTextBox.FocusState == FocusState.Unfocused)
                return;

            RefreshCaretGeometry(shouldForceRecreate);
            if (shouldResetBlink)
                ResetBlinking();

            // An event may have arrived while this update was being processed.
            if (pendingResetBlink || pendingForceRecreate)
                QueueCaretRefresh(resetBlink: false);
        });

        if (!queued)
            updateQueued = false;
    }

    private Rect GetCaretRectFromTextBox(int caretIndex, string text)
    {
        if (text.Length == 0)
            return new Rect(0, 0, 0, 0);

        caretIndex = Math.Clamp(caretIndex, 0, text.Length);

        if (caretIndex == text.Length)
        {
            // TextBox reports the trailing edge of the final character more reliably
            // than asking for a character at Text.Length (which is outside the text).
            char last = text[text.Length - 1];
            if (last == '\\r' || last == '\\n')
            {
                int breakStart = text.Length - 1;
                if (last == '\\n' && breakStart > 0 && text[breakStart - 1] == '\\r')
                    breakStart--;

                // The caret is at the start of the visual line after the final newline.
                // Use the first text edge for X and advance one rendered line for Y.
                Rect previousLine = EditorTextBox.GetRectFromCharacterIndex(Math.Max(0, breakStart - 1), true);
                Rect firstLine = EditorTextBox.GetRectFromCharacterIndex(0, false);
                if (!previousLine.IsEmpty && previousLine.Height > 0 && !firstLine.IsEmpty)
                    return new Rect(firstLine.X, previousLine.Y + previousLine.Height, 0, previousLine.Height);
            }

            return EditorTextBox.GetRectFromCharacterIndex(text.Length - 1, true);
        }

        return EditorTextBox.GetRectFromCharacterIndex(caretIndex, false);
    }

    private void RefreshCaretGeometry(bool forceRecreate)
    {
        string text = EditorTextBox.Text ?? string.Empty;
        // With no selection, SelectionStart is the insertion point. Do not add
        // SelectionLength: selection orientation and IME composition can make that
        // arithmetic an incorrect representation of the active caret.
        int caretIndex = Math.Clamp(EditorTextBox.SelectionStart, 0, text.Length);

        Rect caretRect;
        try
        {
            caretRect = GetCaretRectFromTextBox(caretIndex, text);
        }
        catch (Exception ex)
        {
            SystemCaretStatusText.Text = $"读取 TextBox 光标矩形失败：{ex.Message}";
            CustomCaret.Visibility = Visibility.Collapsed;
            return;
        }

        if (caretRect.IsEmpty || caretRect.Height <= 0)
        {
            // Empty content has no character edge to query. Keep this fallback local
            // to the TextBox content origin; do not invent a glyph-width adjustment.
            Thickness border = EditorTextBox.BorderThickness;
            Thickness padding = EditorTextBox.Padding;
            caretRect = new Rect(border.Left + padding.Left, border.Top + padding.Top, 0, Math.Max(14, EditorTextBox.FontSize * 1.25));
        }

        Point overlayPoint;
        Point windowPoint;
        try
        {
            // GetRectFromCharacterIndex returns a rectangle in TextBox coordinates.
            // Transform its origin into the overlay's coordinate space for drawing.
            GeneralTransform toOverlay = EditorTextBox.TransformToVisual(CaretOverlay);
            overlayPoint = toOverlay.TransformPoint(new Point(caretRect.X, caretRect.Y));

            // Derive the Win32 caret position from the exact same visual point.
            // RootGrid is the window content coordinate space; apply raster scale once.
            GeneralTransform toWindowContent = EditorTextBox.TransformToVisual(RootGrid);
            windowPoint = toWindowContent.TransformPoint(new Point(caretRect.X, caretRect.Y));
        }
        catch (Exception ex)
        {
            SystemCaretStatusText.Text = $"转换光标坐标失败：{ex.Message}";
            CustomCaret.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(CustomCaret, overlayPoint.X);
        Canvas.SetTop(CustomCaret, overlayPoint.Y);
        CustomCaret.Height = Math.Max(14, caretRect.Height);

        bool hasSelection = EditorTextBox.SelectionLength > 0;
        CustomCaret.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        if (hasSelection)
        {
            blinkTimer.Stop();
            customCaretVisible = false;
        }

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int xPixels = (int)Math.Round(windowPoint.X * scale);
        int yPixels = (int)Math.Round(windowPoint.Y * scale);
        int heightPixels = Math.Max(1, (int)Math.Ceiling(caretRect.Height * scale));

        if (forceRecreate || !transparentCaretCreated || transparentCaretHeightPixels != heightPixels)
        {
            if (!CreateTransparentSystemCaret(heightPixels))
            {
                UpdateCaretDiagnostics(text, caretIndex, caretRect, overlayPoint, windowPoint, scale, xPixels, yPixels, "透明 Win32 光标创建失败");
                return;
            }
        }

        if (!SetCaretPos(xPixels, yPixels))
        {
            int firstError = Marshal.GetLastWin32Error();
            if (CreateTransparentSystemCaret(heightPixels) && SetCaretPos(xPixels, yPixels))
                lastSystemCaretStatus = "透明 Win32 光标已重新创建，位置同步成功。";
            else
            {
                int secondError = Marshal.GetLastWin32Error();
                lastSystemCaretStatus = $"SetCaretPos 失败（首次错误 {firstError}，重试错误 {secondError}）。";
            }
        }
        else
        {
            lastSystemCaretStatus = "透明 Win32 光标已创建，系统辅助光标位置同步成功。";
        }

        SystemCaretStatusText.Text = lastSystemCaretStatus;
        UpdateCaretDiagnostics(text, caretIndex, caretRect, overlayPoint, windowPoint, scale, xPixels, yPixels, null);
    }

    private void UpdateCaretDiagnostics(string text, int caretIndex, Rect caretRect, Point overlayPoint, Point windowPoint, double scale, int xPixels, int yPixels, string? extraStatus)
    {
        string adjacentEdges = "";
        if (text.Length > 0 && caretIndex < text.Length)
        {
            try
            {
                Rect currentLeading = EditorTextBox.GetRectFromCharacterIndex(caretIndex, false);
                Rect previousTrailing = caretIndex > 0
                    ? EditorTextBox.GetRectFromCharacterIndex(caretIndex - 1, true)
                    : currentLeading;
                adjacentEdges = $"前一字符尾边缘=({previousTrailing.X:0.0},{previousTrailing.Y:0.0})；当前字符前边缘=({currentLeading.X:0.0},{currentLeading.Y:0.0})；";
            }
            catch
            {
                adjacentEdges = "相邻字符边缘读取失败；";
            }
        }

        double horizontalOffset = editorScrollViewer?.HorizontalOffset ?? 0;
        double verticalOffset = editorScrollViewer?.VerticalOffset ?? 0;
        DiagnosticsText.Text =
            $"SelectionStart={EditorTextBox.SelectionStart}, SelectionLength={EditorTextBox.SelectionLength}, Text.Length={text.Length}, caretIndex={caretIndex}; " +
            $"TextBox 矩形=({caretRect.X:0.0},{caretRect.Y:0.0},h={caretRect.Height:0.0}) DIP；" +
            $"Overlay 点=({overlayPoint.X:0.0},{overlayPoint.Y:0.0}) DIP；窗口内容点=({windowPoint.X:0.0},{windowPoint.Y:0.0}) DIP；" +
            $"Win32=({xPixels},{yPixels}) px，DPI={scale:0.00}；滚动偏移=({horizontalOffset:0.0},{verticalOffset:0.0}) DIP；{adjacentEdges}{extraStatus}";
    }
    private bool CreateTransparentSystemCaret(int heightPixels)
    {
        ReleaseTransparentSystemCaret();

        transparentCaretBitmap = CreateBitmap(
            1,
            heightPixels,
            1,
            1,
            IntPtr.Zero);

        if (transparentCaretBitmap == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            lastSystemCaretStatus = $"CreateBitmap 失败，Win32 错误码：{error}。";
            SystemCaretStatusText.Text = lastSystemCaretStatus;
            return false;
        }

        // WPF uses a 1-bit empty bitmap as its Win32 caret. The bitmap keeps the
        // system caret available to accessibility clients while the visible caret
        // is rendered by the XAML overlay.
        if (!CreateCaret(windowHandle, transparentCaretBitmap, 0, 0))
        {
            int error = Marshal.GetLastWin32Error();
            DeleteObject(transparentCaretBitmap);
            transparentCaretBitmap = IntPtr.Zero;
            lastSystemCaretStatus = $"CreateCaret 失败，Win32 错误码：{error}。";
            SystemCaretStatusText.Text = lastSystemCaretStatus;
            return false;
        }

        transparentCaretCreated = true;
        transparentCaretHeightPixels = heightPixels;

        if (!ShowCaret(windowHandle))
        {
            int error = Marshal.GetLastWin32Error();
            lastSystemCaretStatus = $"透明 Win32 光标已创建，但 ShowCaret 失败，错误码：{error}。";
        }
        else
        {
            lastSystemCaretStatus = "透明 Win32 光标已创建，正在同步位置。";
        }

        return true;
    }

    private void ReleaseTransparentSystemCaret()
    {
        if (transparentCaretCreated)
            DestroyCaret();

        transparentCaretCreated = false;
        transparentCaretHeightPixels = 0;

        if (transparentCaretBitmap != IntPtr.Zero)
        {
            DeleteObject(transparentCaretBitmap);
            transparentCaretBitmap = IntPtr.Zero;
        }
    }

    private void ResetBlinking()
    {
        if (!useCustomCaret || EditorTextBox.FocusState == FocusState.Unfocused)
            return;

        if (EditorTextBox.SelectionLength > 0)
        {
            blinkTimer.Stop();
            CustomCaret.Visibility = Visibility.Collapsed;
            return;
        }

        uint systemBlinkTime = GetCaretBlinkTime();

        if (systemBlinkTime == 0)
        {
            // Windows uses zero to mean that the user has disabled caret blinking.
            blinkTimer.Stop();
            blinkIntervalMilliseconds = 0;
            customCaretVisible = true;
            CustomCaret.Opacity = 1;
            CustomCaret.Visibility = Visibility.Visible;
            SystemCaretStatusText.Text = lastSystemCaretStatus + " 系统设置为不闪烁，因此自绘光标保持常亮。";
            return;
        }

        if (systemBlinkTime == uint.MaxValue)
        {
            // GetCaretBlinkTime failed; use a conservative 500 ms fallback for this experiment.
            systemBlinkTime = 500;
            lastSystemCaretStatus += " GetCaretBlinkTime 读取失败，暂用 500 ms。";
        }

        blinkIntervalMilliseconds = systemBlinkTime;
        blinkTimer.Interval = TimeSpan.FromMilliseconds(blinkIntervalMilliseconds);
        blinkTimer.Stop();

        customCaretVisible = true;
        CustomCaret.Opacity = 1;
        CustomCaret.Visibility = Visibility.Visible;
        blinkTimer.Start();
    }

    private void BlinkTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!useCustomCaret || EditorTextBox.FocusState == FocusState.Unfocused)
        {
            blinkTimer.Stop();
            CustomCaret.Visibility = Visibility.Collapsed;
            return;
        }

        if (EditorTextBox.SelectionLength > 0)
        {
            blinkTimer.Stop();
            CustomCaret.Visibility = Visibility.Collapsed;
            return;
        }

        customCaretVisible = !customCaretVisible;
        CustomCaret.Opacity = customCaretVisible ? 1 : 0;
    }

    private void FocusEditor_Click(object sender, RoutedEventArgs e)
    {
        EditorTextBox.Focus(FocusState.Programmatic);
    }

    private void ClearText_Click(object sender, RoutedEventArgs e)
    {
        EditorTextBox.Text = string.Empty;
        EditorTextBox.SelectionStart = 0;
        EditorTextBox.SelectionLength = 0;
        EditorTextBox.Focus(FocusState.Programmatic);
        QueueCaretRefresh(resetBlink: true);
    }

    private void FillLongText_Click(object sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();
        for (int i = 1; i <= 100; i++)
        {
            builder.Append("第 ");
            builder.Append(i);
            builder.Append(" 行：这是用于测试光标位置、中文换行和滚动同步的示例文本。");
            builder.Append(" The quick brown fox jumps over the lazy dog. 0123456789");
            builder.AppendLine();
        }

        EditorTextBox.Text = builder.ToString();
        EditorTextBox.SelectionStart = EditorTextBox.Text.Length;
        EditorTextBox.SelectionLength = 0;
        EditorTextBox.Focus(FocusState.Programmatic);
        QueueCaretRefresh(resetBlink: true);
    }

    private void CaretTestWindow_Closed(object sender, WindowEventArgs args)
    {
        closing = true;
        blinkTimer.Stop();

        if (editorScrollViewer is not null)
            editorScrollViewer.ViewChanged -= EditorScrollViewer_ViewChanged;

        ReleaseTransparentSystemCaret();
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            T? nested = FindDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    // Use classic DllImport here instead of LibraryImport. The WinUI app does not
    // enable unsafe blocks, and these seven small Win32 declarations do not need
    // source-generated interop to support this isolated experiment.
    [DllImport("gdi32.dll", EntryPoint = "CreateBitmap", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "CreateCaret", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateCaret(IntPtr hwnd, IntPtr bitmap, int width, int height);

    [DllImport("user32.dll", EntryPoint = "ShowCaret", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowCaret(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "DestroyCaret", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCaret();

    [DllImport("user32.dll", EntryPoint = "SetCaretPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCaretPos(int x, int y);

    [DllImport("user32.dll", EntryPoint = "GetCaretBlinkTime", SetLastError = true)]
    private static extern uint GetCaretBlinkTime();
}
