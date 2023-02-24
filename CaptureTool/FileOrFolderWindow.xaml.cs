using Microsoft.Win32;
using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
    /// FileOrFolderWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class FileOrFolderWindow : Window
    {
        private Settings settings;
        private BridgeClosedXML closedXML;
        private bool imageReaded = false;
        public FileOrFolderWindow(Settings settings, BridgeClosedXML bridgeClosedXML)
        {
            InitializeComponent();
            this.settings = settings;
            this.closedXML = bridgeClosedXML;
        }

        private void fileSelectButton_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog()
            {
                Filter = "すべてのファイル|*.*",
                CheckFileExists = false,
                Title = "開く"
            };
            if (ofd.ShowDialog() == true)
            {
                closedXML.AddImageFromFile(ofd.FileName);
            }
            imageReaded = true;
            Close();
        }

        private void folderSelectButton_Click(object sender, RoutedEventArgs e)
        {
            using (var cofd = new CommonOpenFileDialog()
            {
                Title = "フォルダを選択してください",
                InitialDirectory = settings.Directory,
                // フォルダ選択モードにする
                IsFolderPicker = true,
            })
            {
                if (cofd.ShowDialog() != CommonFileDialogResult.Ok)
                {
                    Close();
                }

                closedXML.AddImageFromFolder(cofd.FileName);
                imageReaded = true;
                Close();
            }
        }

        private void calcelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (Owner != null)
            {
                Left = Owner.Left + Owner.Width / 2 - Width / 2;
                Top = Owner.Top + Owner.Height / 2 - Height / 2;
            }
        }

        public new bool ShowDialog()
        {
            base.ShowDialog();
            return imageReaded;
        }
    }
}
