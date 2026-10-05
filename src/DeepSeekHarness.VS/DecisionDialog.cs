using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeepSeekHarness.VS
{
    /// <summary>
    /// The Accept / Reject prompt shown next to the diff.
    /// </summary>
    /// <remarks>
    /// Deliberately modeless: the user must be able to scroll and compare in the diff
    /// window while this is open, and a modal dialog would block the diff's own UI.
    /// The window is built in code rather than XAML so the build does not depend on the
    /// WPF markup compiler picking up loose .xaml items.
    ///
    /// Closing the dialog by any means other than the buttons counts as a rejection, so a
    /// dismissed prompt can never be mistaken for consent.
    /// </remarks>
    internal sealed class DecisionDialog : Window
    {
        private readonly TaskCompletionSource<DiffOutcome> _completion =
            new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TextBox _reason;
        private bool _answered;

        private DecisionDialog(string filePath)
        {
            Title = "DeepSeek Harness — review change";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(filePath),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = filePath
            });

            panel.Children.Add(new TextBlock
            {
                Text = "The model wants to write the file on the right. Accept to save it, " +
                       "Reject to send it back with an optional explanation.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12),
                Foreground = Brushes.DimGray
            });

            panel.Children.Add(new TextBlock { Text = "Reason (optional, sent to the model on Reject):" });

            _reason = new TextBox
            {
                Margin = new Thickness(0, 4, 0, 14),
                MinHeight = 48,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            panel.Children.Add(_reason);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var reject = new Button { Content = "Reject", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            reject.Click += (_, __) => Answer(DiffOutcome.Reject(_reason.Text));

            var accept = new Button { Content = "Accept", MinWidth = 90, IsDefault = true };
            accept.Click += (_, __) => Answer(DiffOutcome.Accept());

            buttons.Children.Add(reject);
            buttons.Children.Add(accept);
            panel.Children.Add(buttons);

            Content = panel;

            // Escape / title-bar close is a rejection, never silent consent.
            Closing += (_, __) => { if (!_answered) Complete(DiffOutcome.Reject(_reason.Text)); };
            Loaded += (_, __) => _reason.Focus();
        }

        /// <summary>Shows the prompt and resolves once the user answers or dismisses it.</summary>
        public static Task<DiffOutcome> ShowAsync(string filePath)
        {
            var dialog = new DecisionDialog(filePath);

            // No owner: Visual Studio's main window may not be a WPF window, and an
            // unowned window still surfaces correctly in the taskbar.
            dialog.Show();
            dialog.Activate();

            return dialog._completion.Task;
        }

        private void Answer(DiffOutcome outcome)
        {
            Complete(outcome);
            Close();
        }

        private void Complete(DiffOutcome outcome)
        {
            if (_answered) return;
            _answered = true;
            _completion.TrySetResult(outcome);
        }
    }
}
