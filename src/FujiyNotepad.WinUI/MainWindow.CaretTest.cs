using Microsoft.UI.Xaml;

namespace FujiyNotepad.WinUI;

public sealed partial class MainWindow
{
    private CaretTestWindow? caretTestWindow;

    private void CaretTest_Click(object sender, RoutedEventArgs e)
    {
        if (caretTestWindow is null)
        {
            caretTestWindow = new CaretTestWindow();
            caretTestWindow.Closed += (_, _) => caretTestWindow = null;
        }

        caretTestWindow.Activate();
    }
}
