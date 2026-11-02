using System.Windows;
using System.Windows.Input;

namespace DepupCode.Views;

public partial class InputDialog : Window
{
    public string Value => InputBox.Text.Trim();

    public InputDialog(string title, string message, string initial = "", string hint = "", string okText = "Simpan")
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        InputBox.Text = initial;
        HintText.Text = hint;
        HintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = okText;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public static string? Show(Window owner, string title, string message, string initial = "", string hint = "", string okText = "Simpan")
    {
        var dialog = new InputDialog(title, message, initial, hint, okText) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (InputBox.Text.Trim().Length == 0)
        {
            HintText.Text = "Nilai tidak boleh kosong.";
            HintText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#F87171")!;
            HintText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
