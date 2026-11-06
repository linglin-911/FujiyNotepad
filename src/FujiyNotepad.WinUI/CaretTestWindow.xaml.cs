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
        if (!initialized || closing || !useCustomCaret || updateQueued)
            return;

        updateQueued = true;
        bool queued = DispatcherQueue.TryEnqueue(() =>
        {
            updateQueued = false;

            if (closing || !useCustomCaret || EditorTextBox.FocusState == FocusState.Unfocused)
                return;

            RefreshCaretGeometry(forceRecreate);

            if (resetBlink)
                ResetBlinking();
        });

        if (!queued)
            updateQueued = false;
    }

    private void RefreshCaretGeometry(bool forceRecreate)
    {
        string text = EditorTextBox.Text ?? string.Empty;
        int caretIndex = Math.Clamp(
            EditorTextBox.SelectionStart + EditorTextBox.SelectionLength,
            0,
            text.Length);

        Rect caretRect;
        try
        {
            if (text.Length == 0)
            {
                caretRect = EditorTextBox.GetRectFromCharacterIndex(0, false);
            }
            else if (caretIndex >= text.Length)
            {
                caretRect = EditorTextBox.GetRectFromCharacterIndex(text.Length - 1, true);
            }
            else
            {
                caretRect = EditorTextBox.GetRectFromCharacterIndex(caretIndex, false);
            }
        }
        catch (Exception ex)
        {
            SystemCaretStatusText.Text = $"读取 TextBox 光标矩形失败：{ex.Message}";
            CustomCaret.Visibility = Visibility.Collapsed;
            return;
        }

        // GetRectFromCharacterIndex can return an empty rectangle for an empty TextBox.
        // Use the TextBox's own padding/font metrics only for this empty-content fallback.
        if (caretRect.IsEmpty || caretRect.Height <= 0)
        {
            Thickness border = EditorTextBox.BorderThickness;
            Thickness padding = EditorTextBox.Padding;
            caretRect = new Rect(
                border.Left + padding.Left,
                border.Top + padding.Top,
                0,
                Math.Max(14, EditorTextBox.FontSize * 1.25));
        }

        Canvas.SetLeft(CustomCaret, caretRect.X);
        Canvas.SetTop(CustomCaret, caretRect.Y);
        CustomCaret.Height = Math.Max(14, caretRect.Height);

        bool hasSelection = EditorTextBox.SelectionLength > 0;
        CustomCaret.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;

        if (hasSelection)
        {
            blinkTimer.Stop();
            customCaretVisible = false;
        }

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        Point clientPoint;
        try
        {
            GeneralTransform transform = EditorTextBox.TransformToVisual(RootGrid);
            clientPoint = transform.TransformPoint(new Point(caretRect.X, caretRect.Y));
        }
        catch (Exception ex)
        {
            SystemCaretStatusText.Text = $"转换光标坐标失败：{ex.Message}";
            return;
        }

        int xPixels = (int)Math.Round(clientPoint.X * scale);
        int yPixels = (int)Math.Round(clientPoint.Y * scale);
        int heightPixels = Math.Max(1, (int)Math.Ceiling(caretRect.Height * scale));

        if (forceRecreate || !transparentCaretCreated || transparentCaretHeightPixels != heightPixels)
        {
            if (!CreateTransparentSystemCaret(heightPixels))
            {
                DiagnosticsText.Text =
                    $"SelectionStart={EditorTextBox.SelectionStart}, SelectionLength={EditorTextBox.SelectionLength}, " +
                    $"Text.Length={text.Length}; 光标矩形 DIP=({caretRect.X:0.0}, {caretRect.Y:0.0}, {caretRect.Height:0.0})";
                return;
            }
        }

        if (!SetCaretPos(xPixels, yPixels))
        {
            int firstError = Marshal.GetLastWin32Error();

            // WinUI may recreate its own native caret during an edit. Reinstall the
            // transparent caret once and retry, following WPF CaretElement's recovery path.
            if (CreateTransparentSystemCaret(heightPixels) && SetCaretPos(xPixels, yPixels))
            {
                lastSystemCaretStatus = "透明 Win32 光标已重新创建，位置同步成功。";
            }
            else
            {
                int secondError = Marshal.GetLastWin32Error();
                lastSystemCaretStatus =
                    $"SetCaretPos 失败（首次错误 {firstError}，重试错误 {secondError}）。";
            }
        }
        else
        {
            lastSystemCaretStatus = "透明 Win32 光标已创建，系统辅助光标位置同步成功。";
        }

        SystemCaretStatusText.Text = lastSystemCaretStatus;
        DiagnosticsText.Text =
            $"SelectionStart={EditorTextBox.SelectionStart}, SelectionLength={EditorTextBox.SelectionLength}, " +
            $"Text.Length={text.Length}; 光标矩形 DIP=({caretRect.X:0.0}, {caretRect.Y:0.0}, h={caretRect.Height:0.0}); " +
            $"窗口客户区位置=({xPixels}, {yPixels}) px；DPI scale={scale:0.00}";
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

    [LibraryImport("gdi32.dll", EntryPoint = "CreateBitmap", SetLastError = true)]
    private static partial IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [LibraryImport("gdi32.dll", EntryPoint = "DeleteObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll", EntryPoint = "CreateCaret", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateCaret(IntPtr hwnd, IntPtr bitmap, int width, int height);

    [LibraryImport("user32.dll", EntryPoint = "ShowCaret", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowCaret(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DestroyCaret", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyCaret();

    [LibraryImport("user32.dll", EntryPoint = "SetCaretPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCaretPos(int x, int y);

    [LibraryImport("user32.dll", EntryPoint = "GetCaretBlinkTime")]
    private static partial uint GetCaretBlinkTime();
}
