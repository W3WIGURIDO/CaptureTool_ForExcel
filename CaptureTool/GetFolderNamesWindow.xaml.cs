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
    /// GetFolderNamesWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class GetFolderNamesWindow : Window
    {
        bool isOk = false;
        public GetFolderNamesWindow()
        {
            InitializeComponent();
        }

        public new string ShowDialog()
        {
            base.ShowDialog();
            if (isOk)
            {
                return folderNamesBox.Text;
            }
            else
            {
                return null;
            }

        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            isOk = true;
            Close();
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            isOk = false;
            Close();
        }
    }
}
