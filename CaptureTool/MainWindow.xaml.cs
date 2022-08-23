using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Navigation;
using System.Windows.Shapes;


namespace CaptureTool
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        private static MainWindow _ActiveWindow = null;
        public static MainWindow ActiveWindow { get => _ActiveWindow; }
        private HotKey windowHotKey;
        private HotKey screenHotKey;
        private HotKey mSaveHotKey;
        private HotKey addSheetHotKey;
        private List<HotKey> hotKeys;
        private Settings settings = new Settings();
        private NotifyIconWrapper notifyIcon;
        private static string WindowCapture = "WindowCapture";
        private static string ScreenCapture = "ScreenCapture";
        private static string ManualSaveStr = "ManualSave";
        private static string AddSheetStr = "AddSheet";
        //private bool loadFinished = false;
        private bool isNoFileMode = false;
        private MiniWindow miniWindow;
        public IntPtr Handle { get; }
        private static MainWindow mainWindow;
        private BridgeClosedXML closedXML;
        private Dictionary<string, ImageGridWindow> imageGridList = new Dictionary<string, ImageGridWindow>();
        private bool visibleAddSheetWindow = false;

        public static MainWindow GetMainWindow()
        {
            return mainWindow;
        }

        public MainWindow()
        {
            InitializeComponent();

            //System.Windows.Forms.Application.EnableVisualStyles();
            //System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

            mainWindow = this;
            this.DataContext = settings;
            _ActiveWindow = this;
            Handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (ExcelOpenWindow.ExcelFileSelect(settings))
            {
                closedXML = new BridgeClosedXML(settings);
            }
            else
            {
                Environment.Exit(0);
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (closedXML.ModFlag)
            {
                bool dResult = ShowNonSaveMessage();
                if (!dResult)
                {
                    e.Cancel = true;
                    return;
                }
            }
            DisposeHotKeys();
            if (settings.EnableAutoSave == true)
            {
                settings.SaveSettings();
            }
            imageGridList.Values.ToArray().Select(igw =>
            {
                if (igw.Visibility != Visibility.Hidden)
                {
                    igw.Close();
                    return igw.Visibility;
                }
                return Visibility.Collapsed;
            }).ToArray();
        }

        private void StartHotKey()
        {
            windowHotKey = new HotKey(EnumScan.FlagToMOD_KEY(settings.PreKey), settings.Key) { HotKeyName = WindowCapture };
            windowHotKey.HotKeyPush += new EventHandler(HotKey_HotKeyPush);
            screenHotKey = new HotKey(EnumScan.FlagToMOD_KEY(settings.ScreenPreKey), settings.ScreenKey) { HotKeyName = ScreenCapture };
            screenHotKey.HotKeyPush += new EventHandler(HotKey_HotKeyPush);
            mSaveHotKey = new HotKey(EnumScan.FlagToMOD_KEY(settings.MSavePreKey), settings.MSaveKey) { HotKeyName = ManualSaveStr };
            mSaveHotKey.HotKeyPush += new EventHandler(HotKey_HotKeyPushMSave);
            addSheetHotKey = new HotKey(EnumScan.FlagToMOD_KEY(settings.AddSheetPreKey), settings.AddSheetKey) { HotKeyName = AddSheetStr };
            addSheetHotKey.HotKeyPush += new EventHandler(HotKey_HotKeyPushAddSheet);
            hotKeys = new List<HotKey>() { windowHotKey, screenHotKey, mSaveHotKey, addSheetHotKey };
        }

        private void ResetHotKey()
        {
            DisposeHotKeys();
            StartHotKey();
        }

        private void DisposeHotKeys()
        {
            if (hotKeys != null)
            {
                foreach (HotKey hotKey in hotKeys)
                {
                    if (hotKey != null)
                    {
                        hotKey.Dispose();
                    }
                }
            }
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            StartHotKey();
            //loadFinished = true;
            WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
        }

        private void HotKey_HotKeyPush(object sender, EventArgs e)
        {
            if (sender is HotKey tmpHotKey)
            {
                Window targetWindow;
                if (miniWindow != null)
                {
                    targetWindow = miniWindow;
                }
                else
                {
                    targetWindow = this;
                }
                if (tmpHotKey.HotKeyName == ScreenCapture)
                {
                    Task.Run(() =>
                 {
                     targetWindow.Dispatcher.Invoke(() =>
                     {
                         targetWindow.Visibility = Visibility.Hidden;
                     }, System.Windows.Threading.DispatcherPriority.Send);
                 });
                }
                var positionSet = (PositionSet)positionSelect.SelectedValue;
                string imageFormatSelectStr = imageFormatSelect.SelectedValue.ToString();
                Task.Run(() =>
                {
                    if (tmpHotKey.HotKeyName == ScreenCapture)
                    {
                        System.Threading.Thread.Sleep(200);
                        while (targetWindow.Visibility == Visibility.Visible)
                        {
                            System.Diagnostics.Debug.WriteLine("Sleep");
                            System.Threading.Thread.Sleep(200);
                        }
                    }
                    targetWindow.Dispatcher.Invoke(() =>
                    {
                        if (MainProcess.CaptureScreen(closedXML, overlayTime: settings.OverlayTimeInt, enableOverlay: settings.EnableOverlay == true, overlayHorizontalAlignment: positionSet.HorizontalAlignment, overlayVerticalAlignment: positionSet.VerticalAlignment, screenFlag: ScreenCapture.Equals(tmpHotKey.HotKeyName), aero: settings.EnableAero == true, enableCursor: settings.EnableCursor == true, captureMode: (int)captureModeSelect.SelectedValue, imageGridWidth: settings.OverlayX, imageGridHeight: settings.OverlayY, enableSetArrow: settings.EnableSetArrow == true, pixelFormat: (System.Drawing.Imaging.PixelFormat)pixelFormatSelect.SelectedValue))
                        {
                            //settings.NumberCount++;
                            //settings.NumberCountSave();
                            if (settings.EnableImageGridSourceAutoUpdate == true)
                            {
                                UpdateViewImageWindowSource();
                            }
                        }
                    });
                    if (tmpHotKey.HotKeyName == ScreenCapture)
                    {
                        targetWindow.Dispatcher.Invoke(() =>
                        {
                            targetWindow.Visibility = Visibility.Visible;
                        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                });
            }
        }

        private void HotKey_HotKeyPushMSave(object sender, EventArgs e)
        {
            if (sender is HotKey tmpHotKey)
            {
                if (tmpHotKey.HotKeyName == ManualSaveStr)
                {
                    saveWorkBookButton_Click(null, null);
                }
            }
        }
        private void HotKey_HotKeyPushAddSheet(object sender, EventArgs e)
        {
            if (sender is HotKey tmpHotKey)
            {
                if (tmpHotKey.HotKeyName == AddSheetStr)
                {
                    this.Activate();
                    addWorkSheetButton_Click(null, null);
                }
            }
        }

        private System.Windows.Forms.Keys ShowKeyInputDialog(bool enablePreMode)
        {
            KeyInputForm keyInputForm = new KeyInputForm()
            {
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point((int)(Left), (int)(Top))
            };
            keyInputForm.PreMode = enablePreMode;
            keyInputForm.ShowDialog();
            return keyInputForm.Key;
        }

        private bool CheckModificationKey(System.Windows.Forms.Keys key)
        {
            if (key == System.Windows.Forms.Keys.LControlKey || key == System.Windows.Forms.Keys.RControlKey || key == System.Windows.Forms.Keys.Control || key == System.Windows.Forms.Keys.ControlKey || key == System.Windows.Forms.Keys.Alt ||
                key == System.Windows.Forms.Keys.LShiftKey || key == System.Windows.Forms.Keys.RShiftKey)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private void ClickStartSetting(object sender, RoutedEventArgs e)
        {
            const string inputText = "キーを入力";
            if (sender is Button button)
            {
                System.Windows.Forms.Keys buttonSetting(bool enablePreMode, string text)
                {
                    button.Content = inputText;
                    System.Windows.Forms.Keys key = ShowKeyInputDialog(enablePreMode);
                    Binding binding = new Binding(text)
                    {
                        Source = settings
                    };
                    button.SetBinding(Button.ContentProperty, binding);
                    return key;
                }

                if (sender == keyButton)
                {
                    settings.Key = buttonSetting(false, "KeyText");
                }
                else if (sender == preKeyButton)
                {
                    settings.PreKey = buttonSetting(true, "PreKeyText");
                }
                else if (sender == screenKeyButton)
                {
                    settings.ScreenKey = buttonSetting(false, "ScreenKeyText");
                }
                else if (sender == screenPreKeyButton)
                {
                    settings.ScreenPreKey = buttonSetting(true, "ScreenPreKeyText");
                }
                else if (sender == mSaveKeyButton)
                {
                    settings.MSaveKey = buttonSetting(false, "MSaveKeyText");
                }
                else if (sender == mSavePreKeyButton)
                {
                    settings.MSavePreKey = buttonSetting(true, "MSavePreKeyText");
                }
                else if (sender == addSheetKeyButton)
                {
                    settings.AddSheetKey = buttonSetting(false, "AddSheetKeyText");
                }
                else if (sender == addSheetPreKeyButton)
                {
                    settings.AddSheetPreKey = buttonSetting(true, "AddSheetPreKeyText");
                }
                ResetHotKey();
            }
        }

        private void ClickRef(object sender, RoutedEventArgs e)
        {
            WpfFolderBrowser.FolderDialogResult folderDialogResult = WpfFolderBrowser.Main.ShowFolderDialog(this, false, baseAddress: settings.Directory);
            if (folderDialogResult != null)
            {
                settings.Directory = folderDialogResult.FullName;
            }
        }

        private void ClickSave(object sender, RoutedEventArgs e)
        {
            settings.SaveSettings();
            ResetHotKey();
            OverlayDialog overlayDialog = new OverlayDialog("設定保存完了")
            {
                Owner = this
            };
            overlayDialog.Show();
        }

        private void ClickReset(object sender, RoutedEventArgs e)
        {
            if (WpfFolderBrowser.CustomMessageBox.Show(this, "設定をリセットします", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None) == MessageBoxResult.OK)
            {
                settings.ResetSettings();
            }
        }

        private void NumberResetClick(object sender, RoutedEventArgs e)
        {
            settings.NumberCount = 0;
            settings.NumberCountSave();
        }

        private void InvisibleButton_GotFocus(object sender, RoutedEventArgs e)
        {
            saveButton.Focus();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                if (settings.EnableTray == true)
                {
                    Visibility = Visibility.Hidden;
                    notifyIcon = new NotifyIconWrapper(this);
                }
            }
            else if (WindowState == WindowState.Normal)
            {
                if (notifyIcon != null)
                {
                    notifyIcon.Dispose();
                }
            }
        }

        private void FileNameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            //if (loadFinished)
            //{
            //    settings.FileName = fileNameBox.Text;
            //    settings.NumberCount = 0;
            //}
            //MainProcess.CreateFileNameNumberCountButtons(settings.FileName, countButtonPanel, settings);
        }

        private void SaveFolder_TextChanged(object sender, TextChangedEventArgs e)
        {
            settings.Directory = saveFolder.Text;
        }

        private void DigitsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            settings.DigitsText = digitsTextBox.Text;
        }

        private void OverlayTimeTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            settings.OverlayTime = overlayTimeTextBox.Text;
        }

        private void CountTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(countTextBox.Text, out int result))
            {
                settings.NumberCount = result;
            }
            else
            {
                countTextBox.Text = 0.ToString();
            }
        }

        private void CountUpButton_Click(object sender, RoutedEventArgs e)
        {
            settings.NumberCount++;
        }

        private void CountDownButton_Click(object sender, RoutedEventArgs e)
        {
            settings.NumberCount--;
        }

        private void MiniModeCheck_Checked(object sender, RoutedEventArgs e)
        {
            if (miniModeCheck.IsChecked == true)
            {
                miniWindow = new MiniWindow(settings) { Left = this.Left, Top = this.Top };
                Visibility = Visibility.Hidden;
                miniModeCheck.IsChecked = false;
                miniWindow.Show();
            }
        }

        public void ReturnFromMiniMode()
        {
            miniWindow = null;
            Visibility = Visibility.Visible;
        }

        private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            /*
            if (sender is Window windowVisibleChanged)
            {
                if (windowVisibleChanged.Visibility == Visibility.Hidden)
                {
                    var sb = new System.Windows.Media.Animation.Storyboard();
                    var da = new System.Windows.Media.Animation.DoubleAnimation();
                    System.Windows.Media.Animation.Storyboard.SetTarget(da, this);
                    System.Windows.Media.Animation.Storyboard.SetTargetProperty(da, new PropertyPath("(Window.Opacity)"));
                    da.To = 0;
                    da.Duration = TimeSpan.FromMilliseconds(1);
                    sb.Children.Add(da);
                    sb.Begin();
                }
            }
            */
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            settings.NumberCount = MainProcess.GetContinueFileName(settings);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start("Explorer", settings.Directory);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex.Message);
            }
        }

        private void ParentFolderButton_Click(object sender, RoutedEventArgs e)
        {
            string tmpDir = settings.Directory;
            if (tmpDir.Last() == '\\')
            {
                tmpDir = tmpDir.Substring(0, tmpDir.Length - 1);
            }
            string[] enSplited = tmpDir.Split('\\');
            if (enSplited.Length > 1)
            {
                settings.Directory = string.Join("\\", enSplited.Take(enSplited.Length - 1));
            }
        }

        private void addWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            if (visibleAddSheetWindow)
            {
                return;
            }
            visibleAddSheetWindow = true;
            bool result = false;
            string name;
            if (settings.AutoSetWorkSheetName == true)
            {
                string lastSheetName = settings.WorkSheets.Last().Value;
                string autoName = MainProcess.TextLastCountUp(lastSheetName);
                while (settings.WorkSheets.Values.Contains(autoName) && autoName != lastSheetName)
                {
                    autoName = MainProcess.TextLastCountUp(autoName);
                }
                name = new StringInputWindow() { Title = "名前を入力", Owner = this, Text = autoName, SelectAllText = true }.ShowDialog(out result);
            }
            else
            {
                name = new StringInputWindow() { Title = "名前を入力", Owner = this }.ShowDialog(out result);
            }
            if (!result)
            {
                return;
            }
            else if (string.IsNullOrEmpty(name))
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "名前が不正です。", "メッセージ");
            }
            else if (settings.WorkSheets.ContainsValue(name))
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "既に存在する名前です。", "メッセージ");
            }
            else
            {
                closedXML.AddWorkSheet(name);
                settings.WorkSheetsIndex = settings.WorkSheets.Values.ToList().IndexOf(name);
            }
            visibleAddSheetWindow = false;
        }

        private void renameWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            string prevName = string.Empty;
            if (WorkSheetSelect.SelectedItem is KeyValuePair<int, string> kvp)
            {
                prevName = kvp.Value;
            }
            string name = new StringInputWindow() { Title = "名前を入力", Owner = this, Text = prevName, SelectAllText = true }.ShowDialog(out bool result);
            if (!result)
            {
                return;
            }
            else if (prevName.Equals(name))
            {
                Debug.WriteLine(name);
            }
            else if (string.IsNullOrEmpty(name))
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "名前が不正です。", "メッセージ");
            }
            else if (settings.WorkSheets.ContainsValue(name))
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "既に存在する名前です。", "メッセージ");
            }
            else
            {
                string prevsheetName = settings.SelectedWorkSheetsValue;
                ImageGridWindow previmageGridWindow = null;
                if (imageGridList.ContainsKey(prevsheetName))
                {
                    previmageGridWindow = imageGridList[prevsheetName];
                    imageGridList.Remove(prevsheetName);
                }

                WorkSheetSelect.SelectionChanged -= WorkSheetSelect_SelectionChanged;
                int prevIndex = WorkSheetSelect.SelectedIndex;
                closedXML.RenameWorkSheet(name);
                WorkSheetSelect.SelectedIndex = prevIndex;
                WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;

                if (previmageGridWindow != null)
                {
                    previmageGridWindow.Title = string.Format("シート：[{0}]の画像一覧", name);
                    imageGridList.Add(name, previmageGridWindow);
                }
            }
        }

        private void WorkSheetSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is ComboBox combo && combo.SelectedValue is int position)
            {
                closedXML.ChangeSelectionWorkSheet(position);
            }
        }

        private void saveAsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExcelOpenWindow.ExcelFileSaveAs(this, settings))
            {
                closedXML.SaveAsNowBook();
            }
        }

        private void openFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (closedXML.ModFlag)
            {
                bool dResult = ShowNonSaveMessage();
                if (!dResult)
                {
                    return;
                }
            }
            if (ExcelOpenWindow.ExcelFileSelect(this, settings))
            {
                WorkSheetSelect.SelectionChanged -= WorkSheetSelect_SelectionChanged;
                if (!closedXML.OpenWorkBook())
                {
                    ChangeNoFileMode(false);
                }
                else
                {
                    if (isNoFileMode)
                    {
                        ChangeNoFileMode(true);
                    }
                    WorkSheetSelect.SelectedIndex = 0;
                    WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
                }
            }
        }

        private void newFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (closedXML.ModFlag)
            {
                bool dResult = ShowNonSaveMessage();
                if (!dResult)
                {
                    return;
                }
            }
            if (ExcelOpenWindow.ExcelFileSaveAs(this, settings))
            {
                WorkSheetSelect.SelectionChanged -= WorkSheetSelect_SelectionChanged;
                if (!closedXML.CreateNewBook())
                {
                    ChangeNoFileMode(false);
                }
                else
                {
                    if (isNoFileMode)
                    {
                        ChangeNoFileMode(true);
                    }
                    WorkSheetSelect.SelectedIndex = 0;
                    WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
                }
            }
        }

        private void ChangeNoFileMode(bool isEnabledValue)
        {
            workSheetGrid.IsEnabled = isEnabledValue;
            saveAsButton.IsEnabled = isEnabledValue;
            WorkSheetSelect.IsEnabled = isEnabledValue;
            if (isEnabledValue)
            {
                isNoFileMode = false;
                StartHotKey();
            }
            else
            {
                isNoFileMode = true;
                DisposeHotKeys();
            }
        }

        private void viewImageListButton_Click(object sender, RoutedEventArgs e)
        {
            string sheetName = settings.SelectedWorkSheetsValue;
            if (imageGridList.ContainsKey(sheetName))
            {
                if (imageGridList[sheetName].Visibility != Visibility.Hidden)
                {
                    imageGridList[sheetName].Activate();
                }
            }
            else
            {
                ImageGridWindow imageGridWindow = new ImageGridWindow(ImageGridSource.GetSourceFromMemoryStream(closedXML.GetImageList())) { Title = string.Format("シート：[{0}]の画像一覧", settings.SelectedWorkSheetsValue) };
                imageGridWindow.Closed += (igsender, ige) =>
                {
                    if (imageGridList.ContainsKey(sheetName))
                    {
                        imageGridList.Remove(sheetName);
                    }
                };
                imageGridList.Add(sheetName, imageGridWindow);
                imageGridWindow.Show();
            }
        }

        private void reopenFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (closedXML.ModFlag)
            {
                bool dResult = ShowNonSaveMessage();
                if (!dResult)
                {
                    return;
                }
            }
            if (WpfFolderBrowser.CustomMessageBox.Show(this, "ファイルを開き直します。", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None) == MessageBoxResult.OK)
            {
                ReOpenFile();
            }
        }

        private void ReOpenFile()
        {
            int prevIndex = settings.WorkSheetsIndex;
            string prevName = settings.SelectedWorkSheetsValue;
            closedXML.OpenWorkBook();
            if (settings.WorkSheetsIndex == prevIndex)
            {
                closedXML.ChangeSelectionWorkSheet(settings.SelectedWorkSheetsKey);
            }
            else
            {
                int nowIndex = settings.WorkSheets.Values.ToList().IndexOf(prevName);
                settings.WorkSheetsIndex = nowIndex;
            }
        }

        private void UpdateViewImageWindowSource()
        {
            string sheetName = settings.SelectedWorkSheetsValue;
            if (imageGridList.ContainsKey(sheetName))
            {
                if (imageGridList[sheetName].Visibility != Visibility.Hidden)
                {
                    imageGridList[sheetName].SetPhotos(ImageGridSource.GetPhotosFromMemoryStream(closedXML.GetImageList()));
                }
            }
        }

        private void deleteWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            if (settings.WorkSheets.Count >= 2)
            {
                if (WpfFolderBrowser.CustomMessageBox.Show(this, "ワークシートを削除しますか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None) == MessageBoxResult.OK)
                {
                    int prevSelect = settings.WorkSheetsIndex;
                    closedXML.DeleteWorkSheet();
                    if (prevSelect == 0)
                    {
                        settings.WorkSheetsIndex = 0;
                    }
                    else if (prevSelect > 0)
                    {
                        settings.WorkSheetsIndex = prevSelect - 1;
                    }
                }
            }
            else
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "ワークシートは１つ以上存在する必要があります。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None);
            }
        }

        private void deleteImageListButton_Click(object sender, RoutedEventArgs e)
        {
            if (closedXML.ModFlag)
            {
                bool dResult = ShowNonSaveMessage2();
                if (!dResult)
                {
                    return;
                }
            }
            string sheetName = settings.SelectedWorkSheetsValue;
            ImageGridWindow imageGridWindow = new ImageGridWindow(ImageGridSource.GetSourceFromMemoryStream(closedXML.GetImageList(), true)) { Title = string.Format("シート：[{0}]の削除する画像を選択", settings.SelectedWorkSheetsValue) };
            if (imageGridWindow.ShowDialog(out bool[] chResult) && chResult.Contains(true))
            {
                closedXML.ReplaceDeletedImagesSheet(chResult);
                ReOpenFile();
            }
        }

        private void sortWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            SheetSelectionWindow sheetSelectionWindow = new SheetSelectionWindow(settings.WorkSheets);
            if (sheetSelectionWindow.ShowDialog(out Dictionary<int, string> result))
            {
                string prevName = settings.SelectedWorkSheetsValue;
                closedXML.SortWorkSheet(result);
                int nowIndex = settings.WorkSheets.Values.ToList().IndexOf(prevName);
                settings.WorkSheetsIndex = nowIndex;
            }
        }

        private void copyWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            bool result = false;
            string name;
            if (settings.AutoSetWorkSheetName == true)
            {
                string lastSheetName = settings.SelectedWorkSheetsValue;
                int count = 1;
                string autoName = lastSheetName + string.Format("({0})", count++);
                while (settings.WorkSheets.Values.Contains(autoName))
                {
                    autoName = lastSheetName + string.Format("({0})", count++);
                }
                name = new StringInputWindow() { Title = "名前を入力", Owner = this, Text = autoName, SelectAllText = true }.ShowDialog(out result);
            }
            else
            {
                name = new StringInputWindow() { Title = "名前を入力", Owner = this }.ShowDialog(out result);
            }
            if (!result)
            {
                return;
            }
            else if (string.IsNullOrEmpty(name))
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "名前が不正です。", "メッセージ");
            }
            else if (settings.WorkSheets.ContainsValue(name))
            {
                WpfFolderBrowser.CustomMessageBox.Show(this, "既に存在する名前です。", "メッセージ");
            }
            else
            {
                closedXML.CopyWorkSheet(name);
            }
        }

        private void EnableWorkBookAutoSaveCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            bool isEnableWorkBookAutoSave = settings.EnableWorkBookAutoSave == true;
            if (closedXML != null)
            {
                closedXML.AutoSave = isEnableWorkBookAutoSave;
            }
        }

        private void saveWorkBookButton_Click(object sender, RoutedEventArgs e)
        {
            closedXML.ManualSave();
            OverlayDialog overlayDialog = new OverlayDialog("上書き保存完了")
            {
                Owner = this
            };
            overlayDialog.Show();
        }

        private bool ShowNonSaveMessage()
        {
            var dialogResult = WpfFolderBrowser.CustomMessageBox.Show(this, "ワークブックが未保存です。\r\n保存しますか？", "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None);
            if (dialogResult == MessageBoxResult.Yes)
            {
                closedXML.ManualSave();
                return true;
            }
            else if (dialogResult == MessageBoxResult.No)
            {
                var dialogResult2 = WpfFolderBrowser.CustomMessageBox.Show(this, "ワークブックは保存されません。\r\nよろしいですか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, MessageBoxOptions.None);
                if (dialogResult2 == MessageBoxResult.No)
                {
                    return false;
                }
                return true;
            }
            else
            {
                return false;
            }
        }

        private bool ShowNonSaveMessage2()
        {
            var dialogResult = WpfFolderBrowser.CustomMessageBox.Show(this, "ワークブックを保存します。\r\nよろしいですか？", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None);
            if (dialogResult == MessageBoxResult.OK)
            {
                closedXML.ManualSave();
                return true;
            }
            else
            {
                return false;
            }
        }
    }
}
