using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Bob.Windows.Platform;

internal sealed class ShortcutSettingsWindow : Window
{
    public ShortcutSettingsWindow(DesktopLifetime desktop)
    {
        Title = "BOB · Keyboard shortcuts";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80);
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 22, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "Show / hide BOB · works from any app", Margin = new Thickness(0, 16, 0, 6) });
        var editor = new TextBox { Text = desktop.HotkeyLabel, IsReadOnly = true, Padding = new Thickness(8),
            ToolTip = "Press Ctrl or Alt with a letter, number, or function key." };
        var hint = new TextBlock { Text = desktop.HotkeyStatus, TextWrapping = TextWrapping.Wrap,
            FontSize = 12, Margin = new Thickness(0, 6, 0, 16) };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Tab or Key.Escape) return;
            e.Handled = true;
            if (e.IsRepeat) return;
            var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey
                : e.Key == Key.System ? e.SystemKey : e.Key;
            desktop.TryChangeHotkey(key, Keyboard.Modifiers);
            editor.Text = desktop.HotkeyLabel;
            hint.Text = desktop.HotkeyStatus;
        };
        panel.Children.Add(editor);
        panel.Children.Add(hint);
        panel.Children.Add(new TextBlock
        {
            Text = "Enter · Send     Shift+Enter · New line\nCtrl+L · Write a message\nCtrl+O · Send files     Ctrl+Shift+O · Send images\nCtrl+Shift+R · Choose receive folder\nCtrl+Shift+E · Open receive folder\nCtrl+C · Copy selection     Esc · Close preview / hide\nF1 · Keyboard shortcuts",
            FontSize = 13, LineHeight = 22
        });
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }
}
