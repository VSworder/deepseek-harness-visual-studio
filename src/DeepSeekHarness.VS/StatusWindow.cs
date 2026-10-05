using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// Shows the status report in a window the user can read and copy from.
    /// </summary>
    /// <remarks>
    /// A plain message box was not enough: the report is longer than a box comfortably
    /// shows, and a bug report needs the text verbatim. Built in code rather than XAML so
    /// the build does not depend on the markup compiler picking up loose .xaml items.
    /// </remarks>
    internal static class StatusWindow
    {
        public static void Show(string report)
        {
            var window = new Window
            {
                Title = "DeepSeek Harness — status",
                Width = 780,
                Height = 520,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var headline = new TextBlock
            {
                Text = "Is the diff gate armed?",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            Grid.SetRow(headline, 0);
            grid.Children.Add(headline);

            var text = new TextBox
            {
                Text = report,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                FontSize = 12,
                Padding = new Thickness(8)
            };
            Grid.SetRow(text, 1);
            grid.Children.Add(text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            var copy = new Button { Content = "Copy", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
            copy.Click += (_, __) =>
            {
                try { Clipboard.SetText(report); } catch { /* clipboard can be busy */ }
            };

            var close = new Button { Content = "Close", MinWidth = 90, IsDefault = true, IsCancel = true };
            close.Click += (_, __) => window.Close();

            buttons.Children.Add(copy);
            buttons.Children.Add(close);
            Grid.SetRow(buttons, 2);
            grid.Children.Add(buttons);

            window.Content = grid;
            window.Show();
            window.Activate();
        }
    }
}
