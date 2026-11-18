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
        public IntPtr Handle
        {
            get
            {
                return new System.Windows.Interop.WindowInteropHelper(this).Handle;
            }
        }
        private static MainWindow mainWindow;
        private BridgeClosedXML closedXML;
        private Dictionary<string, ImageGridWindow> imageGridList = new Dictionary<string, ImageGridWindow>();
        private bool visibleAddSheetWindow = false;

        private Task _workbookQueue = Task.FromResult(0);
        private bool _closingRequested;
        private bool _closeApproved;

        // UI スレッド上でブック操作を順番に実行する。Task.Yield により
        // ダイアログがメッセージループを回す前に _workbookQueue を更新できる。
        private Task EnqueueWorkbookOperation(Func<Task> operation)
        {
            if (_closingRequested)
                return Task.FromResult(0);

            Task previous = _workbookQueue;
            Task current = RunWorkbookOperationAsync(previous, operation);
            _workbookQueue = current;
            return current;
        }

        private Task EnqueueWorkbookOperation(Action operation)
        {
            return EnqueueWorkbookOperation((Func<Task>)(() =>
            {
                operation();
                return Task.FromResult(0);
            }));
        }

        private async Task RunWorkbookOperationAsync(Task previous, Func<Task> operation)
        {
            await Task.Yield();
            try
            {
                await previous;
                await operation();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show(this, ex.Message, "ブック操作エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        

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
            if (ExcelOpenWindow.ExcelFileSelect(settings))
            {
                closedXML = new BridgeClosedXML(settings);
            }
            else
            {
                Environment.Exit(0);
            }
        }

        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_closeApproved)
            {
                e.Cancel = true;
                if (_closingRequested)
                    return;

                _closingRequested = true; // 新しいホットキー・ブック操作を受け付けない
                await Task.Yield(); // 元の Closing イベントが戻ってから閉じ直す
                try
                {
                    await _workbookQueue; // 受付済みの保存・キャプチャを最後まで待つ
                    if (!TryApplyImageLayout() ||
                        (closedXML != null && closedXML.ModFlag && !ShowNonSaveMessage()))
                    {
                        _closingRequested = false;
                        return;
                    }

                    if (settings.EnableAutoSave == true)
                        settings.SaveSettings();

                    _closeApproved = true;
                    Close();
                }
                catch (Exception ex)
                {
                    _closeApproved = false;
                    _closingRequested = false;
                    MessageBox.Show(this, ex.Message, "終了エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return;
            }

            DisposeHotKeys();
            if (notifyIcon != null)
            {
                notifyIcon.Dispose();
                notifyIcon = null;
            }
            CloseImageGridWindows();
            closedXML?.UnloadWorkBook();
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
            if (_closingRequested || !(sender is HotKey hotKey))
                return;

            bool screenCapture = hotKey.HotKeyName == ScreenCapture;
            EnqueueWorkbookOperation((Func<Task>)(async () =>
            {
                if (!TryCommitNextInsertRow(true))
                    return;

                Window targetWindow = miniWindow ?? (Window)this;
                bool hideWindow = screenCapture || MainProcess.GetForegroundWindow().Equals(Handle);
                var positionSet = (PositionSet)positionSelect.SelectedValue;

                try
                {
                    if (hideWindow)
                    {
                        targetWindow.Visibility = Visibility.Hidden;
                        await Task.Delay(200); // UI スレッドは待機中も操作可能
                    }

                    // 画像の追加から自動保存の完了まで、この処理列の１件として実行する。
                    if (MainProcess.CaptureScreen(closedXML,
                        overlayTime: settings.OverlayTimeInt,
                        enableOverlay: settings.EnableOverlay == true,
                        overlayHorizontalAlignment: positionSet.HorizontalAlignment,
                        overlayVerticalAlignment: positionSet.VerticalAlignment,
                        screenFlag: screenCapture,
                        aero: settings.EnableAero == true,
                        enableCursor: settings.EnableCursor == true,
                        captureMode: (int)captureModeSelect.SelectedValue,
                        imageGridWidth: settings.OverlayX,
                        imageGridHeight: settings.OverlayY,
                        enableSetArrow: settings.EnableSetArrow == true,
                        pixelFormat: (System.Drawing.Imaging.PixelFormat)pixelFormatSelect.SelectedValue) &&
                        settings.EnableImageGridSourceAutoUpdate == true)
                    {
                        UpdateViewImageWindowSource();
                    }
                }
                finally
                {
                    if (hideWindow && targetWindow.IsLoaded)
                        targetWindow.Visibility = Visibility.Visible;
                }
            }));
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
        private async void HotKey_HotKeyPushAddSheet(object sender, EventArgs e)
        {
            if (_closingRequested || !(sender is HotKey hotKey) ||
                hotKey.HotKeyName != AddSheetStr || visibleAddSheetWindow)
                return;

            visibleAddSheetWindow = true;
            try
            {
                await EnqueueWorkbookOperation(() =>
                {
                    Activate();
                    AddWorkSheetCore();
                });
            }
            finally
            {
                visibleAddSheetWindow = false;
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

        private bool TryCommitNextInsertRow(bool showError)
        {
            if (nextInsertRowTextBox.Text == settings.Row.ToString())
                return true;
            if (!int.TryParse(nextInsertRowTextBox.Text, out int row) ||
                row < 1 || row > 1048576)
            {
                if (showError)
                {
                    MessageBox.Show(this, "次回挿入行は1～1048576の整数で指定してください。",
                        "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                    nextInsertRowTextBox.Focus();
                    nextInsertRowTextBox.SelectAll();
                }
                return false;
            }
            if (closedXML == null) return false;
            closedXML.SetOneShotRow(row);
            return true;
        }

        private void NextInsertRowTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 入力途中の値は保持し、挿入開始時にも検証する。
            TryCommitNextInsertRow(false);
        }

        private void NextInsertRowTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (TryCommitNextInsertRow(true)) saveButton.Focus();
                e.Handled = true;
            }
        }

        private void RowManagementModeSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (closedXML == null || rowManagementModeSelect.SelectedIndex < 0)
                    return;
                int previousMode = settings.RowManagementModeIndex;
                int requestedMode = rowManagementModeSelect.SelectedIndex;
                if (requestedMode == previousMode) return;
                try
                {
                    if (!closedXML.SwitchRowManagementMode(requestedMode))
                        rowManagementModeSelect.SelectedIndex = previousMode;
                }
                catch (Exception ex)
                {
                    rowManagementModeSelect.SelectedIndex = previousMode;
                    MessageBox.Show(this, ex.Message, "切替エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }

            });
        }

        private void RowPositionCache_Changed(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (closedXML != null && settings.IsPicturePositionMode)
                    closedXML.RefreshCurrentRow();

            });
        }

        private bool TryApplyImageLayout()
        {
            if (!int.TryParse(startRowTextBox.Text, out int startRow) ||
                startRow < 1 || startRow > 1048576)
            {
                MessageBox.Show(this, "開始行は1～1048576の整数で指定してください。", "入力エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                startRowTextBox.Focus();
                startRowTextBox.SelectAll();
                return false;
            }
            if (!int.TryParse(imageGapRowsTextBox.Text, out int gapRows) ||
                gapRows < 0 || gapRows > 1048575)
            {
                MessageBox.Show(this, "画像間の空白行数は0～1048575の整数で指定してください。", "入力エラー",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                imageGapRowsTextBox.Focus();
                imageGapRowsTextBox.SelectAll();
                return false;
            }

            int previousStartRow = settings.StartRow;
            bool changed = startRow != settings.StartRow || gapRows != settings.ImageGapRows;
            settings.StartRow = startRow;
            settings.ImageGapRows = gapRows;
            if (changed)
            {
                closedXML.ApplyStartRowToEmptySheet(previousStartRow);
                if (settings.IsPicturePositionMode)
                    closedXML.RefreshCurrentRow();
            }
            return true;
        }

        private void ApplyImageLayoutButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                TryApplyImageLayout();

            });
        }

        private void ClickSave(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (!TryApplyImageLayout())
                    return;
                settings.SaveSettings();
                ResetHotKey();
                OverlayDialog overlayDialog = new OverlayDialog("設定保存完了")
                {
                    Owner = this
                };
                overlayDialog.Show();

            });
        }

        private void ClickReset(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (WpfFolderBrowser.CustomMessageBox.Show(this, "設定をリセットします", "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None) == MessageBoxResult.OK)
                {
                    int previousStartRow = settings.StartRow;
                    if (settings.RowManagementModeIndex == 1)
                        closedXML.SwitchRowManagementMode(0);
                    settings.ResetSettings();
                    closedXML.ApplyStartRowToEmptySheet(previousStartRow);
                    closedXML.RefreshCurrentRow();
                }

            });
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
                    notifyIcon = null;
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
            EnqueueWorkbookOperation(AddWorkSheetCore);
        }

        private void AddWorkSheetCore()
        {

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
        }

        private void renameWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
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

            });
        }

        private bool revertingWorksheetSelection;
        private void WorkSheetSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (revertingWorksheetSelection || closedXML == null)
                    return;
                if (sender is ComboBox combo && combo.SelectedValue is int position &&
                    position != closedXML.CurrentWorksheetPosition)
                {
                    int previousPosition = closedXML.CurrentWorksheetPosition;
                    if (!TryCommitNextInsertRow(true))
                    {
                        revertingWorksheetSelection = true;
                        combo.SelectedValue = previousPosition;
                        revertingWorksheetSelection = false;
                        return;
                    }
                    try
                    {
                        closedXML.ChangeSelectionWorkSheet(position);
                    }
                    catch (Exception ex)
                    {
                        revertingWorksheetSelection = true;
                        combo.SelectedValue = previousPosition;
                        revertingWorksheetSelection = false;
                        MessageBox.Show(this, ex.Message, "シート切替エラー",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

            });
        }

        private void saveAsButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                string previousPath = settings.FileName;
                if (ExcelOpenWindow.ExcelFileSaveAs(this, settings) &&
                    !closedXML.SaveAsNowBook())
                {
                    settings.FileName = previousPath;
                }

            });
        }

        private void openFileButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (closedXML.ModFlag && !ShowNonSaveMessage())
                    return;

                string previousPath = settings.FileName;
                if (!ExcelOpenWindow.ExcelFileSelect(this, settings))
                    return;

                WorkSheetSelect.SelectionChanged -= WorkSheetSelect_SelectionChanged;
                try
                {
                    CloseImageGridWindows();
                    if (!closedXML.OpenWorkBook())
                    {
                        settings.FileName = previousPath;
                        return;
                    }
                    if (isNoFileMode)
                        ChangeNoFileMode(true);
                    WorkSheetSelect.SelectedIndex = 0;
                }
                finally
                {
                    WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
                }
            });
        }

        private void newFileButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (closedXML.ModFlag && !ShowNonSaveMessage())
                    return;

                string previousPath = settings.FileName;
                if (!ExcelOpenWindow.ExcelFileSaveAs(this, settings))
                    return;

                WorkSheetSelect.SelectionChanged -= WorkSheetSelect_SelectionChanged;
                try
                {
                    CloseImageGridWindows();
                    if (!closedXML.CreateNewBook())
                    {
                        settings.FileName = previousPath;
                        return;
                    }
                    if (isNoFileMode)
                        ChangeNoFileMode(true);
                    WorkSheetSelect.SelectedIndex = 0;
                }
                finally
                {
                    WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
                }
            });
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
            EnqueueWorkbookOperation(() =>
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

            });
        }

        private void reopenFileButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
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

            });
        }

        // ブックを切り替える前に、旧ブックの画像ストリームを参照する一覧を閉じる。
        private void CloseImageGridWindows()
        {
            foreach (var gridWindow in imageGridList.Values.ToArray())
                gridWindow.Close();
            imageGridList.Clear();
        }

        private void ReOpenFile()
        {
            int prevIndex = settings.WorkSheetsIndex;
            string prevName = settings.SelectedWorkSheetsValue;
            CloseImageGridWindows();
            if (!closedXML.OpenWorkBook()) return;
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
            EnqueueWorkbookOperation(() =>
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

            });
        }

        private void deleteImageListButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
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
                ImageGridWindow imageGridWindow = new ImageGridWindow(ImageGridSource.GetSourceFromMemoryStream(closedXML.GetImageList(), true)) { Title = string.Format("シート：[{0}]の削除する画像を選択(※注意　削除後、シートは自動保存されます)", settings.SelectedWorkSheetsValue) };
                if (imageGridWindow.ShowDialog(out bool[] chResult) && chResult.Contains(true))
                {
                    closedXML.ReplaceDeletedImagesSheet(chResult);
                    ReOpenFile();
                }

            });
        }

        private void sortWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                SheetSelectionWindow sheetSelectionWindow = new SheetSelectionWindow(settings.WorkSheets);
                if (sheetSelectionWindow.ShowDialog(out Dictionary<int, string> result))
                {
                    string prevName = settings.SelectedWorkSheetsValue;
                    closedXML.SortWorkSheet(result);
                    int nowIndex = settings.WorkSheets.Values.ToList().IndexOf(prevName);
                    settings.WorkSheetsIndex = nowIndex;
                }

            });
        }

        private void copyWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
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

            });
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
            EnqueueWorkbookOperation(() =>
            {
                closedXML.ManualSave();
                OverlayDialog overlayDialog = new OverlayDialog("上書き保存完了")
                {
                    Owner = this
                };
                overlayDialog.Show();

            });
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

        private void addImageListButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (!TryCommitNextInsertRow(true)) return;
                FileOrFolderWindow fileOrFolderWindow = new FileOrFolderWindow(settings, closedXML) { Owner = this };
                bool imageReaded = fileOrFolderWindow.ShowDialog();
                if (imageReaded)
                {
                    if (settings.EnableImageGridSourceAutoUpdate == true)
                    {
                        UpdateViewImageWindowSource();
                    }
                }

            });
        }

        private void batchButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (closedXML.ModFlag)
                {
                    bool dResult = ShowNonSaveMessage();
                    if (!dResult)
                    {
                        return;
                    }
                }

                string names = new GetFolderNamesWindow() { Owner = this }.ShowDialog();
                if (string.IsNullOrEmpty(names))
                {
                    return;
                }

                string[] nameArray = names.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Select(dir =>
                {
                    if (dir.StartsWith("\"") && dir.EndsWith("\""))
                    {
                        return dir.Replace("\"", "");
                    }
                    return dir;
                }).ToArray();
                bool lastResult = true;
                int lastCount = 0;
                int folderCount = 0;
                foreach (string dir in nameArray)
                {
                    if (System.IO.Directory.Exists(dir))
                    {
                        string fileNametmp = System.IO.Path.GetFileName(dir);
                        CloseImageGridWindows();
                        string previousPath = settings.FileName;
                        settings.FileName = System.IO.Path.Combine(dir, fileNametmp + ".xlsx");
                        lastResult = closedXML.CreateNewBook();
                        if (!lastResult)
                        {
                            settings.FileName = previousPath;
                            return; // 前のブックを維持し、保存失敗後に処理を続けない
                        }
                        if (lastResult)
                        {
                            lastCount = closedXML.AddImageFromFolder(dir);
                            if (lastCount > 0)
                            {
                                if (!closedXML.SaveAsNowBook())
                                    return; // 保存失敗したブックを破棄しない
                                folderCount++;
                            }
                            else
                            {
                                closedXML.UnloadWorkBook();
                                System.IO.File.Delete(settings.FileName);
                                lastResult = false;
                            }
                        }
                    }
                }

                var dialogResult = WpfFolderBrowser.CustomMessageBox.Show(this, string.Format("一括処理が完了しました。\r\n指定フォルダ数：{0}\r\n処理済みフォルダ数：{1}", nameArray.Length, folderCount), "確認", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, MessageBoxOptions.None);

                string previousWorkbookPath = settings.FileName;
                if (ExcelOpenWindow.ExcelFileSelect(settings))
                {
                    BridgeClosedXML previousBook = closedXML;
                    BridgeClosedXML nextBook;
                    try
                    {
                        nextBook = new BridgeClosedXML(settings);
                    }
                    catch
                    {
                        settings.FileName = previousWorkbookPath;
                        throw;
                    }
                    closedXML = nextBook;
                    previousBook?.UnloadWorkBook();
                }
                else
                {
                    // 強制終了は予約済み操作の完了待ちを迂回する。
                    Close();
                }

            });
        }
    }
}
