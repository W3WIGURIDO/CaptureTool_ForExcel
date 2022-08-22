using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CaptureTool
{
    /// <summary>
    /// StringInputWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class StringInputWindow : Window
    {
        private string _Text = string.Empty;
        public string Text
        {
            get => _Text;
            set
            {
                _Text = value;
            }
        }

        private bool _SelectAllText = false;
        public bool SelectAllText
        {
            get => _SelectAllText;
            set
            {
                _SelectAllText = value;
            }
        }

        private bool _InputFinished = false;
        public bool InputFinished
        {
            get => _InputFinished;
        }

        public StringInputWindow()
        {
            InitializeComponent();
            inputBox.DataContext = this;
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            _InputFinished = true;
            Close();
        }

        public new string ShowDialog()
        {
            if (Owner != null)
            {
                double x = Owner.Left + (Owner.Width - Width) / 2;
                double y = Owner.Top + (Owner.Height - Height) / 2;
                Left = x;
                Top = y;
            }
            base.ShowDialog();
            return Text;
        }
        public string ShowDialog(out bool result)
        {
            string text = ShowDialog();
            result = InputFinished;
            return text;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            inputBox.Focus();
            if (SelectAllText)
            {
                inputBox.SelectAll();
            }
        }
    }
}
