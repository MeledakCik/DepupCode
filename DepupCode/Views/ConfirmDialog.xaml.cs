using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DepupCode.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText = "Hapus", bool destructive = true)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (!destructive)
        {
            IconText.Text = "?";
            IconText.Foreground = (Brush)new BrushConverter().ConvertFromString("#A78BFA")!;
            ConfirmButton.Background = (Brush)new BrushConverter().ConvertFromString("#2A1B4D")!;
            ConfirmButton.BorderBrush = (Brush)new BrushConverter().ConvertFromString("#4C2889")!;
        }
    }

    public static bool Show(Window owner, string title, string message, string confirmText = "Hapus", bool destructive = true)
    {
        var dialog = new ConfirmDialog(title, message, confirmText, destructive) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
