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
        // 一覧はブック全体で1枚だけ。現在の選択シートへ追従する。
        private ImageGridWindow imageGridWindow;
        private ImageGridWindow[] OpenImageGridWindows =>
            imageGridWindow == null ? new ImageGridWindow[0] : new[] { imageGridWindow };
        private int appliedImageListDisplayModeIndex;
        private bool visibleAddSheetWindow = false;
        private double mainPageHeight = 330;
        private bool excelSettingsVisited;

        private Task _workbookQueue = Task.FromResult(0);
        private bool _closingRequested;
        private bool _closeApproved;
        private bool _saveWaitCancelled;
        private int _saveCancelGeneration;
        private bool _discardOnClose;
        private System.Threading.CancellationTokenSource _activeSaveWaitCancellation;
        private readonly System.Windows.Threading.DispatcherTimer _saveStatusTimer =
            new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(700) };

        // UI スレッド上でブック操作を順番に実行する。Task.Yield により
        // ダイアログがメッセージループを回す前に _workbookQueue を更新できる。
        private Task EnqueueWorkbookOperation(Func<Task> operation, bool allowWhenSaveCancelled = false)
        {
            if (_closingRequested || (_saveWaitCancelled && !allowWhenSaveCancelled))
                return Task.FromResult(0);

            Task previous = _workbookQueue;
            int generation = _saveCancelGeneration;
            Task current = RunWorkbookOperationAsync(
                previous, operation, allowWhenSaveCancelled, generation);
            _workbookQueue = current;
            return current;
        }

        private Task EnqueueWorkbookOperation(Action operation, bool allowWhenSaveCancelled = false)
        {
            return EnqueueWorkbookOperation((Func<Task>)(() =>
            {
                operation();
                return Task.FromResult(0);
            }), allowWhenSaveCancelled);
        }

        private async Task RunWorkbookOperationAsync(
            Task previous, Func<Task> operation, bool allowWhenSaveCancelled, int generation)
        {
            await Task.Yield();
            try
            {
                await previous;
                // キャンセル前に予約された要求も自動実行しない。
                if (generation != _saveCancelGeneration ||
                    (_saveWaitCancelled && !allowWhenSaveCancelled))
                    return;
                await operation();
                if (closedXML != null && closedXML.HasPendingFileLock &&
                    !_saveWaitCancelled && !_discardOnClose)
                    await WaitForFileUnlockAsync();
                else if (closedXML != null && !closedXML.ModFlag)
                    _saveWaitCancelled = false;
                RefreshImageGridSaveStatuses();
                UpdateMainSaveStatus();
            }
            catch (Exception ex)
            {
                if (closedXML != null && closedXML.HasPendingFileLock)
                {
                    _saveWaitCancelled = true;
                    _saveCancelGeneration++;
                }
                Debug.WriteLine(ex);
                UpdateMainSaveStatus();
                if (!_discardOnClose)
                    MessageBox.Show(this, ex.Message, "ブック操作エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task WaitForFileUnlockAsync()
        {
            SaveLockWaitWindow waitWindow = null;
            using (var cancellation = new System.Threading.CancellationTokenSource())
            {
                _activeSaveWaitCancellation = cancellation;
                try
                {
                    // 短いロックは表示を出さず、１回だけ静かに再試行する。
                    await Task.Delay(700, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (closedXML.RetryPendingSave())
                    {
                        _saveWaitCancelled = false;
                        return;
                    }

                    waitWindow = new SaveLockWaitWindow(closedXML.PendingSavePath);
                    waitWindow.CancelRequested += (sender, args) => cancellation.Cancel();
                    waitWindow.Show();

                    int delay = 1500;
                    while (true)
                    {
                        await Task.Delay(delay, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (closedXML.RetryPendingSave())
                        {
                            _saveWaitCancelled = false;
                            return;
                        }
                        delay = Math.Min(delay * 2, 5000);
                    }
                }
                catch (OperationCanceledException)
                {
                    _saveWaitCancelled = true;
                    _saveCancelGeneration++; // 待機前に予約された操作を再開後も実行しない
                }
                finally
                {
                    if (ReferenceEquals(_activeSaveWaitCancellation, cancellation))
                        _activeSaveWaitCancellation = null;
                    if (waitWindow != null)
                        waitWindow.CloseAfterWait();
                    UpdateMainSaveStatus();
                }
            }
            if (_saveWaitCancelled && !_discardOnClose)
            {
                string message = "保存待機を中止しました。ブックは未保存のまま保持しています。\n" +
                    "待機中のキャプチャ要求は実行しません。手動保存または別名保存をしてください。";
                if (Visibility == Visibility.Visible)
                    MessageBox.Show(this, message, "保存待機中止",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show(message, "保存待機中止",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
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
            appliedImageListDisplayModeIndex = settings.ImageListDisplayModeIndex;
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

        private bool HasPendingExitChanges()
        {
            return !_workbookQueue.IsCompleted ||
                   (closedXML != null && (closedXML.ModFlag || closedXML.HasPendingFileLock)) ||
                   _saveWaitCancelled ||
                   (excelSettingsVisited &&
                    (startRowTextBox.Text != settings.StartRow.ToString() ||
                     imageGapRowsTextBox.Text != settings.ImageGapRows.ToString())) ||
                   (settings.EnableImageDescriptions &&
                    !string.IsNullOrWhiteSpace(nextImageDescriptionTextBox?.Text)) ||
                   OpenImageGridWindows.Any(grid => grid.Photos.Any(photo =>
                       photo.HasDirtyDescription || photo.IsCommitPending));
        }

        // 終了だけは編集内容を破棄できる。ブック切替時の厳格な保護は維持する。
        private async void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_closeApproved)
            {
                _saveStatusTimer.Stop();
                DisposeHotKeys();
                if (notifyIcon != null)
                {
                    notifyIcon.Dispose();
                    notifyIcon = null;
                }
                if (_discardOnClose)
                {
                    // 保存せず終了：一覧の反映・保存を呼ばない。
                    foreach (var grid in OpenImageGridWindows)
                        grid.AllowCloseAfterCommit();
                    imageGridWindow = null;
                }
                else
                    CloseImageGridWindows();
                closedXML?.UnloadWorkBook();
                return;
            }

            e.Cancel = true;
            if (_closingRequested) return;
            _closingRequested = true;
            if (imageGridWindow != null)
                imageGridWindow.IsEnabled = false; // ダイアログ中に説明を変更させない
            await Task.Yield();
            try
            {
                while (true)
                {
                    UpdateMainSaveStatus();
                    ExitDecision decision = HasPendingExitChanges()
                        ? new UnsavedExitDialog(mainSaveStatusText.Text) { Owner = this }.Ask()
                        : ExitDecision.SaveAndClose;
                    if (decision == ExitDecision.Cancel) return;
                    if (decision == ExitDecision.Discard)
                    {
                        if (MessageBox.Show(this,
                            "ブックの未保存の変更、未反映・未使用の説明、未適用の設定を" +
                            "破棄して終了します。\nこの操作は取り消せません。よろしいですか？",
                            "保存せず終了", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
                            continue;

                        _discardOnClose = true;
                        _saveCancelGeneration++; // 待機中のブック操作を再開させない
                        _activeSaveWaitCancellation?.Cancel();
                        await _workbookQueue; // 実行中の処理が終わってからブックを解放する
                        _closeApproved = true;
                        Close();
                        return;
                    }

                    await _workbookQueue;
                    if (_saveWaitCancelled && closedXML != null && closedXML.HasPendingFileLock)
                    {
                        MessageBox.Show(this,
                            "保存待機は中止されています。ファイルを使用しているアプリを閉じて再試行するか、" +
                            "保存せず終了を選択してください。",
                            "保存待機中", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    if (!TryApplyImageLayout() &&
                        MessageBox.Show(this,
                            "入力中の配置設定は適用できません。設定の変更を破棄し、" +
                            "ブックのみ保存して終了しますか？",
                            "配置設定未適用", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
                        continue;

                    if (OpenImageGridWindows.Any(grid => !CommitGridDescriptionsCore(grid)))
                    {
                        UpdateMainSaveStatus();
                        MessageBox.Show(this,
                            "一覧の説明をブックに反映できません。原因は一覧の該当項目に表示しています。" +
                            "\n内容を保持したままキャンセルするか、破棄して終了できます。",
                            "説明未反映", MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }

                    if (closedXML != null && closedXML.HasWorkbook)
                    {
                        try
                        {
                            _saveWaitCancelled = false;
                            if (closedXML.ModFlag || closedXML.HasPendingFileLock ||
                                !closedXML.HasSavedFile)
                                closedXML.ManualSave();
                            if (closedXML.HasPendingFileLock)
                                await WaitForFileUnlockAsync();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex);
                            MessageBox.Show(this, ex.Message, "保存失敗",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            continue;
                        }
                        if (_saveWaitCancelled || closedXML.ModFlag)
                        {
                            UpdateMainSaveStatus();
                            MessageBox.Show(this,
                                "ブックの保存は完了していません。再試行するか、保存せず終了を選択してください。",
                                "ブック未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                            continue;
                        }
                    }

                    if (settings.EnableImageDescriptions &&
                        !string.IsNullOrWhiteSpace(nextImageDescriptionTextBox?.Text) &&
                        MessageBox.Show(this,
                            "次の画像用の説明は、まだ画像に関連付いておらずExcelに保存できません。" +
                            "\nこの入力を破棄して終了しますか？",
                            "未使用の説明", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes)
                        return;

                    if (settings.EnableAutoSave == true)
                    {
                        try { settings.SaveSettings(); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(ex);
                            if (MessageBox.Show(this,
                                "ブックは保存されましたが、設定を保存できませんでした。\n" +
                                ex.Message + "\n設定を保存せずに終了しますか？",
                                "設定の保存失敗", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                                MessageBoxResult.No) != MessageBoxResult.Yes)
                                return;
                        }
                    }

                    _closeApproved = true;
                    Close();
                    return;
                }
            }
            catch (Exception ex)
            {
                _closeApproved = false;
                _discardOnClose = false;
                Debug.WriteLine(ex);
                MessageBox.Show(this, ex.Message, "終了エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (!_closeApproved)
                {
                    _closingRequested = false;
                    if (imageGridWindow != null)
                        imageGridWindow.IsEnabled = true;
                    UpdateMainSaveStatus();
                }
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (settingsView.Visibility == Visibility.Visible) return;
            mainPageHeight = Height;
            mainView.Visibility = Visibility.Collapsed;
            settingsView.Visibility = Visibility.Visible;
            if (WindowState == WindowState.Normal)
                Height = Math.Min(Math.Max(Height, 610),
                    Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24));
            settingsTabs.SelectedIndex = 0;
        }

        private void ShowExcelSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenSettings_Click(sender, e);
            settingsTabs.SelectedIndex = 1;
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            if (settingsView.Visibility != Visibility.Visible) return;
            settingsView.Visibility = Visibility.Collapsed;
            mainView.Visibility = Visibility.Visible;
            if (WindowState == WindowState.Normal)
                Height = Math.Max(MinHeight, mainPageHeight);
            if (!isNoFileMode && closedXML != null && closedXML.HasWorkbook)
                ResetHotKey(); // 設定画面のキー変更を作業画面へ戻る時点で反映
            openSettingsButton.Focus();
        }

        private void SettingsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is TabControl tab && tab.SelectedIndex == 1)
                excelSettingsVisited = true;
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

        private void UpdateMainSaveStatus()
        {
            if (mainSaveStatusText == null) return;
            string filePath = settings.FileName;
            bookNameLabel.Text = string.IsNullOrEmpty(filePath)
                ? "ブック未選択" : System.IO.Path.GetFileName(filePath);
            bookNameLabel.ToolTip = filePath;
            captureKeysHintText.Text = string.Format(
                "撮影キー：ウィンドウ {0}+{1} ／ 画面 {2}+{3}",
                settings.PreKeyText, settings.KeyText,
                settings.ScreenPreKeyText, settings.ScreenKeyText);
            var photos = OpenImageGridWindows.SelectMany(grid => grid.Photos).ToArray();
            int drafts = photos.Count(photo => photo.HasDirtyDescription);
            bool unusedNextDescription = settings.EnableImageDescriptions &&
                !string.IsNullOrWhiteSpace(nextImageDescriptionTextBox?.Text);
            bool layoutNotApplied = excelSettingsVisited &&
                (startRowTextBox.Text != settings.StartRow.ToString() ||
                 imageGapRowsTextBox.Text != settings.ImageGapRows.ToString());
            var failure = photos.FirstOrDefault(photo => photo.HasDirtyDescription &&
                (photo.StatusText.StartsWith("保存失敗", StringComparison.Ordinal) ||
                 photo.StatusText.StartsWith("未反映", StringComparison.Ordinal)));
            bool pending = closedXML != null && closedXML.HasPendingFileLock;
            bool modified = closedXML != null && closedXML.ModFlag;
            string status = closedXML == null || !closedXML.HasWorkbook ? "ブックなし" :
                _saveWaitCancelled ? "保存待機を中止・ブック未保存" :
                pending ? "ファイル使用中・保存待機中" :
                modified ? "ブック未保存" :
                !_workbookQueue.IsCompleted ? "ブック操作中" : "ブック保存済み";
            if (drafts > 0)
                status += string.Format(" ／ 説明未反映 {0}件", drafts);
            if (unusedNextDescription)
                status += " ／ 次の画像説明は未使用";
            if (layoutNotApplied)
                status += " ／ 配置設定が未適用";
            if (_closingRequested)
                status += " ／ 終了処理中";
            mainSaveStatusText.Text = status;
            mainSaveStatusText.ToolTip = failure != null
                ? "説明を反映できません：" + failure.StatusDetail +
                  "。一覧画面に編集中の文字列は残っています。"
                : _saveWaitCancelled ? "保存待機は中止されました。手動保存または別名保存で再試行できます。"
                : pending ? "Excelファイルのロック解除を待っています。待機を中止してもブックはメモリ上に残ります。"
                : modified || drafts > 0 || unusedNextDescription || layoutNotApplied
                    ? "ブック、画像一覧、または設定の入力に未保存・未適用の変更があります。"
                    : "表示しているブックの保存状態です。";
            mainSaveStatusText.Foreground = failure != null || _saveWaitCancelled
                ? System.Windows.Media.Brushes.DarkRed
                : pending || modified || drafts > 0 || unusedNextDescription || layoutNotApplied
                    ? System.Windows.Media.Brushes.DarkOrange
                    : System.Windows.Media.Brushes.DarkGreen;
        }

        // 作業画面の初回表示時だけ実コンテンツを測る。
        // SizeToContentを常用すると保存状態の更新や設定ページ表示で高さが揺れるため使用しない。
        private void FitInitialMainHeight()
        {
            if (!IsLoaded || WindowState != WindowState.Normal ||
                mainView.Visibility != Visibility.Visible || mainView.ActualWidth <= 0)
                return;

            mainView.Measure(new Size(mainView.ActualWidth, double.PositiveInfinity));
            double mainHeight = mainView.DesiredSize.Height;
            if (double.IsNaN(mainHeight) || double.IsInfinity(mainHeight))
                return;

            double chromeAndMargin = Math.Max(0, ActualHeight - rootLayout.ActualHeight);
            // ステータスが2行になってもウィンドウが伸び縮みしないよう余白を確保。
            double statusHeight = Math.Max(38,
                mainStatusPanel.ActualHeight + mainStatusPanel.Margin.Top);
            double desired = mainHeight + statusHeight + chromeAndMargin + 8;
            double maximum = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 24);
            Height = Math.Max(MinHeight, Math.Min(desired, maximum));
            mainPageHeight = Height;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _saveStatusTimer.Tick += (timerSender, args) => UpdateMainSaveStatus();
            _saveStatusTimer.Start();
            UpdateMainSaveStatus();
            StartHotKey();
            Dispatcher.BeginInvoke(new Action(FitInitialMainHeight),
                System.Windows.Threading.DispatcherPriority.Loaded);
            //loadFinished = true;
            WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
            if (closedXML != null && closedXML.HasPendingFileLock)
                EnqueueWorkbookOperation(() => { });
        }

        private void HotKey_HotKeyPush(object sender, EventArgs e)
        {
            if (_closingRequested || _saveWaitCancelled || !(sender is HotKey hotKey))
                return;

            bool screenCapture = hotKey.HotKeyName == ScreenCapture;
            EnqueueWorkbookOperation((Func<Task>)(async () =>
            {
                if (!TryCommitNextInsertRow(true))
                    return;

                string draftDescription = settings.EnableImageDescriptions
                    ? nextImageDescriptionTextBox.Text : null;
                Window targetWindow = miniWindow ?? (Window)this;
                bool hideWindow = screenCapture || MainProcess.GetForegroundWindow().Equals(Handle);
                int positionIndex = Math.Max(0, Math.Min(settings.PositionIndex,
                    settings.ViewPosition.Count - 1));
                var positionSet = settings.ViewPosition.Values.ElementAt(positionIndex);
                int pixelIndex = Math.Max(0, Math.Min(settings.PixelFormatIndex,
                    settings.PixelFormats.Count - 1));
                var pixelFormat = settings.PixelFormats.Keys.ElementAt(pixelIndex);
                int captureMode = Math.Max(0, Math.Min(settings.CaptureModeIndex, 2));

                try
                {
                    if (hideWindow)
                    {
                        targetWindow.Visibility = Visibility.Hidden;
                        await Task.Delay(200); // UI スレッドは待機中も操作可能
                    }
                    if (_closingRequested || _discardOnClose) return;

                    // 画像の追加から自動保存の完了まで、この処理列の１件として実行する。
                    bool captured = MainProcess.CaptureScreen(closedXML,
                        overlayTime: settings.OverlayTimeInt,
                        enableOverlay: settings.EnableOverlay == true,
                        overlayHorizontalAlignment: positionSet.HorizontalAlignment,
                        overlayVerticalAlignment: positionSet.VerticalAlignment,
                        screenFlag: screenCapture,
                        aero: settings.EnableAero == true,
                        enableCursor: settings.EnableCursor == true,
                        captureMode: captureMode,
                        imageGridWidth: settings.OverlayX,
                        imageGridHeight: settings.OverlayY,
                        enableSetArrow: settings.EnableSetArrow == true,
                        pixelFormat: pixelFormat,
                        description: draftDescription);
                    if (captured)
                    {
                        if (draftDescription != null && nextImageDescriptionTextBox.Text == draftDescription)
                            nextImageDescriptionTextBox.Clear();
                        if (settings.EnableImageGridSourceAutoUpdate == true)
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
            if (closedXML == null || !closedXML.HasWorkbook) return false;
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
                if (TryCommitNextInsertRow(true)) viewImageListButton.Focus();
                e.Handled = true;
            }
        }

        private void RowManagementModeSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (closedXML == null || !closedXML.HasWorkbook ||
                    rowManagementModeSelect.SelectedIndex < 0)
                    return;
                int previousMode = settings.RowManagementModeIndex;
                int requestedMode = rowManagementModeSelect.SelectedIndex;
                if (requestedMode == previousMode) return;
                if (requestedMode == 1 && settings.EnableImageDescriptions && settings.StartRow < 3)
                {
                    MessageBox.Show(this, "旧方式で説明を使う場合、先に開始行を3以上に設定してください。",
                        "切替エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                    rowManagementModeSelect.SelectedIndex = previousMode;
                    return;
                }
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
                if (closedXML != null && closedXML.HasWorkbook && settings.IsPicturePositionMode)
                    closedXML.RefreshCurrentRow();

            });
        }

        private bool TryApplyImageLayout()
        {
            // TabItem内の未表示TextBoxは遅延ロードされる場合がある。
            // Excel出力タブを開いていなければ、検証済みの現在設定を使用する。
            if (!excelSettingsVisited) return true;
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

            if (settings.EnableImageDescriptions &&
                (startRow < (settings.IsPicturePositionMode ? 2 : 3) || gapRows < 1))
            {
                MessageBox.Show(this,
                    settings.IsPicturePositionMode
                        ? "説明を使う場合、開始行は2以上・空白行数は1以上にしてください。"
                        : "旧方式ではA1を使うため、開始行は3以上・空白行数は1以上にしてください。",
                    "配置設定", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            int previousStartRow = settings.StartRow;
            bool changed = startRow != settings.StartRow || gapRows != settings.ImageGapRows;
            settings.StartRow = startRow;
            settings.ImageGapRows = gapRows;
            if (changed)
            {
                if (closedXML != null && closedXML.HasWorkbook)
                {
                    closedXML.ApplyStartRowToEmptySheet(previousStartRow);
                    if (settings.IsPicturePositionMode)
                        closedXML.RefreshCurrentRow();
                }
            }
            return true;
        }

        private void EnableImageDescriptions_Changed(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (!settings.EnableImageDescriptions) return;
                if (!TryApplyImageLayout())
                    settings.EnableImageDescriptions = false;
            });
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
                if (!isNoFileMode && closedXML != null && closedXML.HasWorkbook)
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
                    if (closedXML != null && closedXML.HasWorkbook &&
                        settings.RowManagementModeIndex == 1)
                        closedXML.SwitchRowManagementMode(0);
                    settings.ResetSettings();
                    if (closedXML != null && closedXML.HasWorkbook)
                    {
                        closedXML.ApplyStartRowToEmptySheet(previousStartRow);
                        closedXML.RefreshCurrentRow();
                    }
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
            if (settingsView.Visibility == Visibility.Visible)
                saveButton.Focus();
            else
                viewImageListButton.Focus();
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
                    if (closedXML.AddWorkSheet(name))
                    {
                        int index = settings.WorkSheets.Values.ToList().IndexOf(name);
                        if (index >= 0)
                            settings.WorkSheetsIndex = index;
                    }
                }
        }

        private void renameWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (!settings.TryGetSelectedWorkSheet(out var selectedSheet))
                    return;
                string previousName = selectedSheet.Value;
                string name = new StringInputWindow()
                {
                    Title = "名前を入力", Owner = this, Text = previousName, SelectAllText = true
                }.ShowDialog(out bool result);
                if (!result || name == previousName)
                    return;
                if (string.IsNullOrEmpty(name) || settings.WorkSheets.ContainsValue(name))
                {
                    WpfFolderBrowser.CustomMessageBox.Show(this, "名前が不正か、既に存在しています。", "メッセージ");
                    return;
                }

                int previousIndex = WorkSheetSelect.SelectedIndex;
                bool renamed;
                WorkSheetSelect.SelectionChanged -= WorkSheetSelect_SelectionChanged;
                try
                {
                    renamed = closedXML.RenameWorkSheet(name);
                    if (renamed)
                        WorkSheetSelect.SelectedIndex = previousIndex;
                }
                finally
                {
                    WorkSheetSelect.SelectionChanged += WorkSheetSelect_SelectionChanged;
                }
                if (!renamed)
                    return;

                if (imageGridWindow != null && imageGridWindow.ActiveSheetName == previousName)
                    imageGridWindow.RenameSheet(name);
            });
        }

        private bool revertingWorksheetSelection;
        private void WorkSheetSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (revertingWorksheetSelection || closedXML == null || !closedXML.HasWorkbook)
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
                    if (imageGridWindow != null &&
                        (!CommitGridDescriptionsCore(imageGridWindow) || closedXML.HasPendingFileLock))
                    {
                        revertingWorksheetSelection = true;
                        combo.SelectedValue = previousPosition;
                        revertingWorksheetSelection = false;
                        return; // 保存失敗・ロック待ちでは編集中の一覧を残す
                    }
                    try
                    {
                        closedXML.ChangeSelectionWorkSheet(position);
                        ShowCurrentSheetInImageGrid();
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            if (closedXML.CurrentWorksheetPosition != previousPosition)
                                closedXML.ChangeSelectionWorkSheet(previousPosition);
                        }
                        catch (Exception rollbackError) { Debug.WriteLine(rollbackError); }
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
                bool descriptionsCommitted = OpenImageGridWindows.All(CommitGridDescriptionsCore);
                if (!descriptionsCommitted &&
                    MessageBox.Show(this,
                        "一覧に反映できていない説明があります。未反映の説明を含まずに別名保存しますか？" +
                        "\n編集中の説明は一覧に残ります。",
                        "説明は未反映", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                        MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
                string previousPath = settings.FileName;
                if (ExcelOpenWindow.ExcelFileSaveAs(this, settings))
                {
                    bool previouslyCancelled = _saveWaitCancelled;
                    _saveWaitCancelled = false;
                    try
                    {
                        if (!closedXML.SaveAsNowBook() && !closedXML.HasPendingFileLock)
                        {
                            settings.FileName = previousPath;
                            if (previouslyCancelled)
                                _saveWaitCancelled = true;
                        }
                    }
                    catch
                    {
                        _saveWaitCancelled = previouslyCancelled;
                        throw;
                    }
                }

            }, allowWhenSaveCancelled: true);
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
                catch
                {
                    settings.FileName = previousPath;
                    throw;
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
                catch
                {
                    settings.FileName = previousPath;
                    throw;
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
            saveWorkBookButton.IsEnabled = isEnabledValue;
            reopenFileButton.IsEnabled = isEnabledValue;
            viewImageListButton.IsEnabled = isEnabledValue;
            addImageListButton.IsEnabled = isEnabledValue;
            deleteImageListButton.IsEnabled = isEnabledValue;
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
                if (!closedXML.HasWorkbook || !settings.TryGetSelectedWorkSheet(out var selectedSheet))
                    return;
                OpenImageGridForCurrentSheet();
            });
        }

        private void OpenImageGridForCurrentSheet(bool activate = true,
            Rect? previousBounds = null, WindowState previousState = WindowState.Normal)
        {
            if (imageGridWindow != null)
            {
                ShowCurrentSheetInImageGrid();
                if (imageGridWindow.Visibility == Visibility.Hidden)
                    imageGridWindow.Show();
                if (activate)
                    imageGridWindow.Activate();
                return;
            }

            string sheetName = closedXML.CurrentWorksheetName;
            var source = ImageGridSource.GetSourceFromImageEntries(closedXML.GetImageEntries());
            if (source.Photos == null)
                throw new InvalidOperationException("画像一覧を読み込めませんでした。");
            var grid = new ImageGridWindow(source);
            if (previousBounds.HasValue && !previousBounds.Value.IsEmpty &&
                previousBounds.Value.Width > 0 && previousBounds.Value.Height > 0)
            {
                grid.Left = previousBounds.Value.Left;
                grid.Top = previousBounds.Value.Top;
                grid.Width = previousBounds.Value.Width;
                grid.Height = previousBounds.Value.Height;
            }
            grid.WindowState = previousState;
            grid.ShowSheet(sheetName, source.Photos);
            grid.SetInlineMode(settings.ImageListDisplayModeIndex == 1);
            grid.EditDescriptionRequested += photo => EditImageDescription(grid, photo);
            grid.InlineDescriptionCommitRequested += CommitInlineDescription;
            grid.InlineCloseRequested += CloseImageGridAfterCommit;
            grid.Closed += (igsender, ige) =>
            {
                if (ReferenceEquals(imageGridWindow, grid))
                    imageGridWindow = null;
            };
            imageGridWindow = grid;
            grid.Show();
            if (activate) grid.Activate();
        }

        // タイトルと画像だけを更新。編集ドラフトの確定は呼び出し元のキューで行う。
        private void ShowCurrentSheetInImageGrid()
        {
            if (imageGridWindow == null) return;
            string sheetName = closedXML.CurrentWorksheetName;
            if (imageGridWindow.ActiveSheetName == sheetName) return;
            var photos = ImageGridSource.GetPhotosFromImageEntries(closedXML.GetImageEntries());
            if (photos == null)
                throw new InvalidOperationException("切替先の画像一覧を読み込めませんでした。");
            imageGridWindow.ShowSheet(sheetName, photos);
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

        // ブックの破棄より前に未確定の説明を反映する。失敗した一覧は閉じない。
        private void CloseImageGridWindows()
        {
            var windows = OpenImageGridWindows;
            bool hadDrafts = windows.Any(grid => grid.Photos.Any(photo => photo.HasDirtyDescription));
            if (windows.Any(grid => !CommitGridDescriptionsCore(grid)))
                throw new InvalidOperationException("画像一覧に未反映の説明があります。内容を確認して再試行してください。");
            if (hadDrafts && closedXML != null && closedXML.ModFlag && !closedXML.HasPendingFileLock)
                throw new InvalidOperationException("説明はブック未保存です。保存してからブックを切り替えてください。");
            if (closedXML != null && closedXML.HasPendingFileLock)
                throw new InvalidOperationException("説明の保存待機中です。ブックの切り替えは保存後に実行してください。");
            foreach (var grid in windows)
                grid.AllowCloseAfterCommit();
            imageGridWindow = null;
        }

        private void ReOpenFile()
        {
            int previousPosition = closedXML.CurrentWorksheetPosition;
            settings.WorkSheets.TryGetValue(previousPosition, out string previousName);
            CloseImageGridWindows();
            if (!closedXML.OpenWorkBook())
                return;

            int index = string.IsNullOrEmpty(previousName) ? -1 :
                settings.WorkSheets.Values.ToList().IndexOf(previousName);
            if (index < 0)
                index = Math.Max(0, Math.Min(previousPosition - 1, settings.WorkSheets.Count - 1));

            settings.WorkSheetsIndex = index;
            closedXML.ChangeSelectionWorkSheet(settings.WorkSheets.ElementAt(index).Key);
        }

        private void UpdateViewImageWindowSource()
        {
            if (imageGridWindow == null || imageGridWindow.Visibility == Visibility.Hidden)
                return;
            // 自動更新を予約した時点のシートと表示中のシートが違えば混在させない。
            if (imageGridWindow.ActiveSheetName != closedXML.CurrentWorksheetName)
            {
                ShowCurrentSheetInImageGrid();
                return;
            }
            imageGridWindow.SetPhotos(
                ImageGridSource.GetPhotosFromImageEntries(closedXML.GetImageEntries()));
        }

        private void RefreshImageGridSaveStatuses()
        {
            if (closedXML == null) return;
            foreach (var grid in OpenImageGridWindows)
            {
                foreach (var photo in grid.Photos)
                {
                    if (photo.HasDirtyDescription || photo.IsCommitPending) continue;
                    if (photo.StatusText != "保存待ち" && photo.StatusText != "ブック未保存")
                        continue;
                    if (closedXML.HasPendingFileLock && !_saveWaitCancelled) continue;
                    photo.IsWorkbookUnsaved = closedXML.ModFlag;
                    photo.StatusText = closedXML.ModFlag ? "ブック未保存" : "保存済み";
                    if (photo.StatusText == "保存済み") grid.ShowSavedBriefly();
                }
            }
        }

        private async void ImageListDisplayModeSelect_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (imageListDisplayModeSelect.SelectedIndex < 0) return;
            bool executed = false;
            await EnqueueWorkbookOperation(() =>
            {
                executed = true;
                int mode = imageListDisplayModeSelect.SelectedIndex;
                if (mode == appliedImageListDisplayModeIndex) return;
                if (OpenImageGridWindows.Any(grid => !CommitGridDescriptionsCore(grid)) ||
                    (closedXML != null && closedXML.HasPendingFileLock))
                {
                    settings.ImageListDisplayModeIndex = appliedImageListDisplayModeIndex;
                    return;
                }
                foreach (var grid in OpenImageGridWindows)
                    grid.SetInlineMode(mode == 1);
                appliedImageListDisplayModeIndex = mode;
            });
            if (!executed && settings.ImageListDisplayModeIndex != appliedImageListDisplayModeIndex)
                settings.ImageListDisplayModeIndex = appliedImageListDisplayModeIndex;
        }

        // Workbook更新と画像一覧からの表示更新はすべてブック操作キュー上で実行。
        private bool CommitPhotoCore(Photo photo, string value)
        {
            if (photo.Entry == null)
            {
                photo.StatusText = "反映できません";
                photo.StatusDetail = "対象画像が見つかりません。";
                return false;
            }
            try
            {
                string committed = closedXML.UpdateImageDescription(photo.Entry, value);
                // セル書き込み後に保存だけ失敗したケースを、次の確定時に再試行する。
                if (closedXML.AutoSave && closedXML.ModFlag && !closedXML.HasPendingFileLock)
                    closedXML.ManualSave();
                photo.Entry.Description = committed;
                photo.Description = committed;
                photo.IsWorkbookUnsaved = closedXML.ModFlag || closedXML.HasPendingFileLock;
                photo.StatusDetail = string.Empty;
                photo.StatusText = closedXML.HasPendingFileLock ? "保存待ち" :
                    closedXML.ModFlag ? "ブック未保存" : "保存済み";
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                photo.StatusDetail = ex.Message;
                // 保存だけ失敗しセルはブック内で更新された場合、Escで古い値へ
                // 見かけ上だけ戻さない。未保存のブックを保持し手動保存を促す。
                try
                {
                    if (closedXML.ReadImageDescription(photo.Entry) == value)
                    {
                        photo.Entry.Description = value;
                        photo.Description = value;
                        photo.IsWorkbookUnsaved = true;
                        photo.StatusText = "ブック未保存";
                        return true;
                    }
                }
                catch (Exception readError) { Debug.WriteLine(readError); }
                photo.StatusText = "保存失敗（再試行できます）";
                return false;
            }
        }

        private bool CommitGridDescriptionsCore(ImageGridWindow grid)
        {
            if (grid.IsClosedForEditing) return true;
            bool ok = true;
            foreach (var photo in grid.Photos.Where(p => p.HasDirtyDescription).ToArray())
                if (!CommitPhotoCore(photo, photo.DraftDescription))
                    ok = false;
            if (ok && grid.Photos.Any(p => p.StatusText == "保存済み"))
                grid.ShowSavedBriefly();
            return ok;
        }

        private async void CommitInlineDescription(ImageGridWindow grid, Photo photo)
        {
            if (grid.IsClosedForEditing || !grid.Photos.Contains(photo) ||
                !photo.HasDirtyDescription) return;
            if (photo.IsCommitPending)
            {
                photo.CommitAgain = true;
                return;
            }
            photo.IsCommitPending = true;
            photo.StatusText = "反映待ち";
            string snapshot = photo.DraftDescription;
            bool executed = false;
            bool success = false;
            await EnqueueWorkbookOperation(() =>
            {
                if (grid.IsClosedForEditing || !grid.Photos.Contains(photo)) return;
                executed = true;
                // 古い入力を、後から確定された新しい内容で上書きしない。
                if (photo.DraftDescription != snapshot)
                {
                    photo.CommitAgain = true;
                    return;
                }
                photo.StatusText = "保存中";
                success = CommitPhotoCore(photo, snapshot);
            });
            photo.IsCommitPending = false;
            if (grid.IsClosedForEditing || !grid.Photos.Contains(photo)) return;
            if (!executed)
            {
                photo.StatusText = "未反映（再試行できます）";
                photo.StatusDetail = "保存待機の中止などによりブック操作が実行されませんでした。";
                return;
            }
            if (success && !photo.HasDirtyDescription)
            {
                photo.IsWorkbookUnsaved = closedXML.ModFlag || closedXML.HasPendingFileLock;
                photo.StatusText = closedXML.HasPendingFileLock ? "保存待ち" :
                    closedXML.ModFlag ? "ブック未保存" : "保存済み";
                if (photo.StatusText == "保存済み") grid.ShowSavedBriefly();
            }
            if (photo.CommitAgain && photo.HasDirtyDescription)
            {
                photo.CommitAgain = false;
                CommitInlineDescription(grid, photo);
            }
            else
                photo.CommitAgain = false;
        }

        private async void CloseImageGridAfterCommit(ImageGridWindow grid)
        {
            bool executed = false;
            bool success = false;
            await EnqueueWorkbookOperation(() =>
            {
                executed = true;
                success = CommitGridDescriptionsCore(grid);
            });
            if (!grid.IsClosedForEditing && executed && success && !_saveWaitCancelled)
                grid.AllowCloseAfterCommit();
            else
            {
                if (!executed)
                {
                    foreach (var photo in grid.Photos.Where(p => p.HasDirtyDescription))
                    {
                        photo.StatusText = "未反映（再試行できます）";
                        photo.StatusDetail = "保存待機などのため、まだブックへ反映されていません。";
                    }
                }
                grid.CancelCloseRequest();
            }
        }

        private void EditImageDescription(ImageGridWindow grid, Photo photo)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (photo.Entry == null || grid.IsClosedForEditing ||
                    !grid.Photos.Contains(photo)) return;
                var editor = new ImageDescriptionWindow(photo.Description) { Owner = grid };
                if (editor.ShowDialog() != true) return;
                string value = closedXML.UpdateImageDescription(photo.Entry, editor.Description);
                photo.Entry.Description = value;
                photo.Description = value;
                photo.IsWorkbookUnsaved = closedXML.ModFlag || closedXML.HasPendingFileLock;
                photo.StatusText = photo.IsWorkbookUnsaved ? "ブック未保存" : "保存済み";
                if (!photo.IsWorkbookUnsaved) grid.ShowSavedBriefly();
            });
        }

        private void deleteWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (settings.WorkSheets.Count < 2)
                {
                    WpfFolderBrowser.CustomMessageBox.Show(this,
                        "ワークシートは１つ以上存在する必要があります。", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Warning,
                        MessageBoxResult.Cancel, MessageBoxOptions.None);
                    return;
                }
                if (WpfFolderBrowser.CustomMessageBox.Show(this, "ワークシートを削除しますか？",
                    "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
                    MessageBoxResult.Cancel, MessageBoxOptions.None) != MessageBoxResult.OK)
                    return;

                int deletedPosition = closedXML.CurrentWorksheetPosition;
                if (imageGridWindow != null && !CommitGridDescriptionsCore(imageGridWindow))
                    return;
                if (closedXML.HasPendingFileLock) return;
                if (!closedXML.DeleteWorkSheet())
                    return;

                // シート破棄後は旧画像ストリームを参照し続けない。
                if (imageGridWindow != null)
                {
                    try { ShowCurrentSheetInImageGrid(); }
                    catch
                    {
                        imageGridWindow.AllowCloseAfterCommit();
                        imageGridWindow = null;
                        throw;
                    }
                }

                settings.WorkSheetsIndex = deletedPosition == 1 ? 0 : deletedPosition - 2;
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
                if (!settings.TryGetSelectedWorkSheet(out var selectedSheet))
                    return;
                string sheetName = selectedSheet.Value;
                ImageGridWindow imageGridWindow = new ImageGridWindow(ImageGridSource.GetSourceFromMemoryStream(closedXML.GetImageList(), true)) { Title = string.Format("シート：[{0}]の削除する画像を選択(※注意　削除後、シートは自動保存されます)", sheetName) };
                if (imageGridWindow.ShowDialog(out bool[] chResult) && chResult.Contains(true))
                {
                    // 差替え対象の旧画像ストリームは解放されるため、一覧を安全に作り直す。
                    bool reopenList = this.imageGridWindow != null;
                    Rect? bounds = reopenList ? this.imageGridWindow.RestoreBounds : (Rect?)null;
                    WindowState state = reopenList ? this.imageGridWindow.WindowState : WindowState.Normal;
                    CloseImageGridWindows();
                    closedXML.ReplaceDeletedImagesSheet(chResult);
                    if (reopenList)
                        OpenImageGridForCurrentSheet(activate: false,
                            previousBounds: bounds, previousState: state);
                    // 保存がロックされてもインメモリのブックを保持し、操作キューが再試行する。
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
                    if (!settings.TryGetSelectedWorkSheet(out var selectedSheet))
                        return;
                    bool completed = closedXML.SortWorkSheet(result);
                    if (!completed)
                        return;
                    int nowIndex = settings.WorkSheets.Values.ToList().IndexOf(selectedSheet.Value);
                    if (nowIndex >= 0)
                        settings.WorkSheetsIndex = nowIndex;
                }

            });
        }

        private void copyWorkSheetButton_Click(object sender, RoutedEventArgs e)
        {
            EnqueueWorkbookOperation(() =>
            {
                if (!settings.TryGetSelectedWorkSheet(out var selectedSheet))
                    return;
                bool result = false;
                string name;
                if (settings.AutoSetWorkSheetName == true)
                {
                    string lastSheetName = selectedSheet.Value;
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

        private async void saveWorkBookButton_Click(object sender, RoutedEventArgs e)
        {
            bool saved = false;
            bool descriptionsCommitted = true;
            await EnqueueWorkbookOperation(() =>
            {
                _saveWaitCancelled = false;
                // 反映できない説明が残っても、ブック自体の保存・ロック再試行を妨げない。
                // 編集中の文字列は一覧に保持し、ブックへ保存済みとは表示しない。
                descriptionsCommitted = OpenImageGridWindows.All(CommitGridDescriptionsCore);
                try
                {
                    closedXML.ManualSave();
                    saved = true;
                }
                catch
                {
                    _saveWaitCancelled = true;
                    throw;
                }
            }, allowWhenSaveCancelled: true);
            if (!saved || _closingRequested || !IsLoaded || closedXML == null ||
                closedXML.ModFlag || _saveWaitCancelled)
                return;

            new OverlayDialog(descriptionsCommitted
                ? "上書き保存完了" : "保存済／説明未反映") { Owner = this }.Show();
        }

        private bool ShowNonSaveMessage()
        {
            var dialogResult = WpfFolderBrowser.CustomMessageBox.Show(this, "ワークブックが未保存です。\r\n保存しますか？", "確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel, MessageBoxOptions.None);
            if (dialogResult == MessageBoxResult.Yes)
            {
                closedXML.ManualSave();
                if (closedXML.HasPendingFileLock)
                    return false; // 呼び出し元が非同期の保存待機へ進む
                if (closedXML.ModFlag)
                {
                    MessageBox.Show(this,
                        "保存は完了していません。手動保存または別名保存をしてください。",
                        "未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
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
                if (closedXML.ModFlag || closedXML.HasPendingFileLock)
                {
                    MessageBox.Show(this,
                        "保存は完了していません。手動保存または別名保存をしてください。",
                        "未保存", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
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
                string draft = settings.EnableImageDescriptions ? nextImageDescriptionTextBox.Text : null;
                FileOrFolderWindow fileOrFolderWindow = new FileOrFolderWindow(settings, closedXML, draft) { Owner = this };
                bool imageReaded = fileOrFolderWindow.ShowDialog();
                if (imageReaded)
                {
                    if (draft != null && nextImageDescriptionTextBox.Text == draft)
                        nextImageDescriptionTextBox.Clear();
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
                            if (isNoFileMode)
                                ChangeNoFileMode(true);
                            if (!closedXML.HasSavedFile)
                                return; // 未保存の新規ブックを残し、一括処理は中断する
                            lastCount = closedXML.AddImageFromFolder(dir);
                            if (lastCount > 0)
                            {
                                if (!closedXML.SaveAsNowBook())
                                    return; // 保存失敗したブックを破棄しない
                                folderCount++;
                            }
                            else
                            {
                                if (closedXML.ModFlag)
                                {
                                    MessageBox.Show(this,
                                        "画像の追加または保存に失敗しました。未保存のブックを維持して一括処理を中断します。",
                                        "一括処理", MessageBoxButton.OK, MessageBoxImage.Warning);
                                    return;
                                }
                                closedXML.UnloadWorkBook();
                                ChangeNoFileMode(false);
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
                    if (isNoFileMode && closedXML.HasWorkbook)
                        ChangeNoFileMode(true);
                }
                else
                {
                    // 強制終了は予約済み操作の完了待ちを迂回する。
                    Close();
                }

            });
        }
    }
    internal enum ExitDecision { Cancel, SaveAndClose, Discard }

    // メッセージボックスの「はい/いいえ」では破棄対象が分かりにくいため、選択肢を明記する。
    internal sealed class UnsavedExitDialog : Window
    {
        public ExitDecision Choice { get; private set; } = ExitDecision.Cancel;

        public UnsavedExitDialog(string status)
        {
            Title = "アプリケーションの終了";
            Width = 540;
            Height = 185;
            MinWidth = 470;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Topmost = true; // 保存待機ウィンドウが表示中でも終了方法を選択できる

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "未保存または処理中の内容があります。終了方法を選んでください。",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = status ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 12)
            });
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var save = new Button
            {
                Content = "保存して終了", MinWidth = 105,
                Margin = new Thickness(0, 0, 8, 0)
            };
            save.Click += (s, e) => { Choice = ExitDecision.SaveAndClose; DialogResult = true; };
            actions.Children.Add(save);
            var discard = new Button
            {
                Content = "保存せず終了", MinWidth = 115,
                Margin = new Thickness(0, 0, 8, 0)
            };
            discard.Click += (s, e) => { Choice = ExitDecision.Discard; DialogResult = true; };
            actions.Children.Add(discard);
            actions.Children.Add(new Button
            {
                Content = "キャンセル", MinWidth = 85, IsCancel = true, IsDefault = true
            });
            panel.Children.Add(actions);
            Content = panel;
        }

        public ExitDecision Ask()
        {
            ShowDialog();
            return Choice;
        }
    }

}
