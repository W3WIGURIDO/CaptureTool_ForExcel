using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CaptureTool
{
    // 共有違反が継続する場合だけ生成する軽量な待機画面。
    public sealed class SaveLockWaitWindow : Window
    {
        private bool closeAfterWait;
        private bool cancelRequested;
        private readonly Button cancelButton;

        public event EventHandler CancelRequested;

        public SaveLockWaitWindow(string filePath)
        {
            Title = "保存待機";
            Width = 410;
            Height = 158;
            MinWidth = 330;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Topmost = true;

            var layout = new StackPanel { Margin = new Thickness(16) };
            layout.Children.Add(new TextBlock
            {
                Text = "ファイルが使用中です。保存できるまで待機しています。",
                TextWrapping = TextWrapping.Wrap
            });
            layout.Children.Add(new TextBlock
            {
                Text = filePath ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 10),
                MaxHeight = 36
            });
            cancelButton = new Button
            {
                Content = "キャンセル",
                Width = 90,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            cancelButton.Click += (sender, args) => RequestCancel();
            layout.Children.Add(cancelButton);
            Content = layout;
        }

        private void RequestCancel()
        {
            if (cancelRequested)
                return;
            cancelRequested = true;
            cancelButton.IsEnabled = false;
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!closeAfterWait)
            {
                e.Cancel = true;
                RequestCancel();
            }
            base.OnClosing(e);
        }

        public void CloseAfterWait()
        {
            closeAfterWait = true;
            Close();
        }
    }
}
