using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
    /// SheetSelectionWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class SheetSelectionWindow : Window
    {
        private SheetSelectionSource sheetSelectionSource;
        private bool isOK = false;
        public SheetSelectionWindow(Dictionary<int, string> sheetDictionary)
        {
            InitializeComponent();
            sheetSelectionSource = new SheetSelectionSource()
            {
                WorkSheetInfos = sheetDictionary.Select(ws =>
                {
                    bool upisEnabled = true;
                    if (ws.Key == 1)
                    {
                        upisEnabled = false;
                    }
                    bool downisEnabled = true;
                    if (ws.Key == sheetDictionary.Count)
                    {
                        downisEnabled = false;
                    }
                    return new WorkSheetInfo() { SheetName = ws.Value, Potision = ws.Key, UpisEnabled = upisEnabled, DownisEnabled = downisEnabled };
                }).ToList()
            };
            sheetSelectionSource.WorkSheetInfos.Last().DownisEnabled = false;
            DataContext = sheetSelectionSource;
        }

        private void upButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is WorkSheetInfo sheetInfo)
            {
                int lastIndex = sheetSelectionSource.WorkSheetInfos.Count - 1;
                int index = sheetSelectionSource.WorkSheetInfos.IndexOf(sheetInfo);
                if (index > 0)
                {
                    if (index == 1)
                    {
                        sheetInfo.UpisEnabled = false;
                        sheetSelectionSource.WorkSheetInfos[0].UpisEnabled = true;
                    }
                    else if (index == lastIndex)
                    {
                        sheetSelectionSource.WorkSheetInfos[index - 1].DownisEnabled = false;
                    }
                    List<WorkSheetInfo> tmpList = sheetSelectionSource.WorkSheetInfos.ToList();
                    sheetInfo.DownisEnabled = true;
                    tmpList.Remove(sheetInfo);
                    tmpList.Insert(index - 1, sheetInfo);
                    sheetSelectionSource.WorkSheetInfos = tmpList;
                }
            }
        }

        private void downButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is WorkSheetInfo sheetInfo)
            {
                int lastIndex = sheetSelectionSource.WorkSheetInfos.Count - 1;
                int index = sheetSelectionSource.WorkSheetInfos.IndexOf(sheetInfo);
                if (index < lastIndex)
                {
                    if (index == lastIndex - 1)
                    {
                        sheetInfo.DownisEnabled = false;
                        sheetSelectionSource.WorkSheetInfos[lastIndex].DownisEnabled = true;
                    }
                    else if (index == 0)
                    {
                        sheetSelectionSource.WorkSheetInfos[1].UpisEnabled = false;
                    }
                    List<WorkSheetInfo> tmpList = sheetSelectionSource.WorkSheetInfos.ToList();
                    sheetInfo.UpisEnabled = true;
                    tmpList[index + 1].DownisEnabled = true;
                    tmpList.Remove(sheetInfo);
                    tmpList.Insert(index + 1, sheetInfo);
                    sheetSelectionSource.WorkSheetInfos = tmpList;
                }
            }
        }

        public bool ShowDialog(out Dictionary<int, string> result)
        {
            base.ShowDialog();
            int count = 1;
            result = sheetSelectionSource.WorkSheetInfos.ToDictionary(wsi => { return count++; }, wsi => { return wsi.SheetName; });
            return isOK;
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            isOK = true;
            Close();
        }
    }
    public class WorkSheetInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        public string SheetName { get; set; }

        private int _Potision;
        public int Potision
        {
            get => _Potision;
            set
            {
                _Potision = value;
                RaisePropertyChanged();
            }
        }

        private bool _UpisEnabled;
        public bool UpisEnabled
        {
            get => _UpisEnabled;
            set
            {
                _UpisEnabled = value;
                RaisePropertyChanged();
            }
        }

        private bool _DownisEnabled;
        public bool DownisEnabled
        {
            get => _DownisEnabled;
            set
            {
                _DownisEnabled = value;
                RaisePropertyChanged();
            }
        }
    }

    public class SheetSelectionSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private List<WorkSheetInfo> _WorkSheetInfos;
        public List<WorkSheetInfo> WorkSheetInfos
        {
            get => _WorkSheetInfos;
            set
            {
                if (value != null)
                {
                    _WorkSheetInfos = value;
                    RaisePropertyChanged();
                }
            }
        }
    }
}
