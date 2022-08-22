using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Microsoft.Win32;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Diagnostics;

namespace CaptureTool
{
    /// <summary>
    /// ExcelOpenWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class ExcelOpenWindow : Window
    {
        private readonly Settings settings;
        public ExcelOpenWindow(Settings settings)
        {
            this.settings = settings;
            InitializeComponent();
        }

        private void openButton_Click(object sender, RoutedEventArgs e)
        {
        }

        public static bool ExcelFileSelect(Window owner, Settings settings)
        {
            OpenFileDialog openFileDialog = CreateExcelFileDialog();
            if (openFileDialog.ShowDialog(owner) == true)
            {
                Debug.WriteLine(openFileDialog.FileName);
                settings.FileName = openFileDialog.FileName;
                return true;
            }
            return false;
        }
        public static bool ExcelFileSelect(Settings settings)
        {
            OpenFileDialog openFileDialog = CreateExcelFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                Debug.WriteLine(openFileDialog.FileName);
                settings.FileName = openFileDialog.FileName;
                return true;
            }
            return false;
        }

        public static bool ExcelFileSaveAs(Window owner, Settings settings)
        {
            SaveFileDialog saveFileDialog = CreateExcelSaveDialog();
            if (saveFileDialog.ShowDialog(owner) == true)
            {
                Debug.WriteLine(saveFileDialog.FileName);
                settings.FileName = saveFileDialog.FileName;
                return true;
            }
            return false;
        }

        private static SaveFileDialog CreateExcelSaveDialog()
        {
            return new SaveFileDialog()
            {
                Filter = "Excel文書(.xlsx)|*.xlsx|マクロ付きExcel文書(.xlsm)|*.xlsm|すべてのファイル|*.*",
                Title = "名前を付けて保存"
            };
        }

        private static OpenFileDialog CreateExcelFileDialog()
        {
            return new OpenFileDialog()
            {
                Filter = "Excel文書(.xlsx)|*.xlsx|マクロ付きExcel文書(.xlsm)|*.xlsm|すべてのファイル|*.*",
                CheckFileExists = false,
                Title = "開く　または　新規作成"
            };
        }
    }
}
