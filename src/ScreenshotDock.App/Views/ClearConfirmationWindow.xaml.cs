namespace ScreenshotDock.App.Views;

public partial class ClearConfirmationWindow : Window
{
    public ClearConfirmationWindow(int count)
    {
        InitializeComponent();
        CountLabel.Text = $"确定清空当前 {count} 张暂存图片吗？";
    }

    private void CancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
    private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
