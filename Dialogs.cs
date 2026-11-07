using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VibeLauncher;

public static class Dialogs
{
    public static Task<string?> PromptAsync(Window owner, string title, string label, string initial = "")
    {
        var completion = new TaskCompletionSource<string?>();

        var window = new Window
        {
            Title = title,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI")
        };

        var panel = new StackPanel { Margin = new Thickness(24) };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("TextBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });

        panel.Children.Add(new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Foreground = (Brush)Application.Current.FindResource("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var input = new TextBox
        {
            Text = initial,
            Height = 36,
            Margin = new Thickness(0, 0, 0, 16)
        };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var ok = new Button
        {
            Content = "Продолжить",
            Style = (Style)Application.Current.FindResource("PrimaryButton"),
            Padding = new Thickness(18, 8, 18, 8),
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "Отмена",
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true
        };

        ok.Click += (_, _) =>
        {
            completion.TrySetResult(input.Text.Trim());
            window.Close();
        };
        cancel.Click += (_, _) =>
        {
            completion.TrySetResult(null);
            window.Close();
        };
        window.Closed += (_, _) => completion.TrySetResult(null);

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        window.Content = panel;
        window.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        window.ShowDialog();
        return completion.Task;
    }
}
