using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
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
    /// ImageGridWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class ImageGridWindow : Window
    {
        private ImageGridSource imageGridSource;
        private bool isOK = false;
        private bool allowClose;
        private bool closeRequested;
        private bool isClosedForEditing;
        private Photo[] pendingPhotos;
        public bool IsClosedForEditing => isClosedForEditing;
        private readonly System.Windows.Threading.DispatcherTimer savedStatusTimer =
            new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        public event Action<Photo> EditDescriptionRequested;
        public event Action<ImageGridWindow, Photo> InlineDescriptionCommitRequested;
        public event Action<ImageGridWindow> InlineCloseRequested;
        public ImageGridWindow(ImageGridSource imageGridSource)
        {
            InitializeComponent();
            DataContext = imageGridSource;
            this.imageGridSource = imageGridSource;
            savedStatusTimer.Tick += (sender, args) =>
            {
                savedStatusTimer.Stop();
                foreach (var photo in imageGridSource.Photos ?? new Photo[0])
                    if (!photo.HasDirtyDescription && !photo.IsCommitPending &&
                        photo.StatusText == "保存済み")
                        photo.StatusText = string.Empty;
            };
            Closing += ImageGridWindow_Closing;
            Closed += (sender, args) => { isClosedForEditing = true; savedStatusTimer.Stop(); };
        }

        public void SetInlineMode(bool enabled)
        {
            // 削除選択画面には説明編集を出さない。
            imageGridSource.InlineMode = enabled && !imageGridSource.EnableCheckBox;
        }

        public Photo[] Photos => imageGridSource.Photos ?? new Photo[0];

        private static bool SameImage(Photo left, Photo right)
        {
            var a = left.Entry;
            var b = right.Entry;
            return a != null && b != null &&
                   a.SheetName == b.SheetName && a.Index == b.Index &&
                   a.StartRow == b.StartRow && a.StartColumn == b.StartColumn &&
                   a.PictureName == b.PictureName;
        }

        public void SetPhotos(Photo[] photos)
        {
            if (photos == null || isClosedForEditing) return;
            // 配列を差し替えるとWPFがTextBoxを作り直す。入力中は最新の更新だけ保留する。
            if (Keyboard.FocusedElement is TextBox activeEditor &&
                activeEditor.DataContext is Photo activePhoto && Photos.Contains(activePhoto))
            {
                pendingPhotos = photos;
                return;
            }
            pendingPhotos = null;
            // 自動更新では写真とTextBoxを可能な限り再利用し、未確定の内容を保持する。
            var existing = Photos.Where(p => p.Entry != null)
                .GroupBy(p => p.Entry.Index)
                .ToDictionary(group => group.Key,
                    group => group.FirstOrDefault(p => !p.HasDirtyDescription && !p.IsCommitPending)
                             ?? group.First());
            for (int i = 0; i < photos.Length; i++)
            {
                Photo photo = photos[i];
                if (photo.Entry == null || !existing.TryGetValue(photo.Entry.Index, out Photo old) ||
                    !SameImage(old, photo))
                    continue;
                if (!old.HasDirtyDescription && !old.IsCommitPending)
                    old.Description = photo.Description;
                old.Entry = photo.Entry;
                photos[i] = old;
            }
            // アンカーが変わった画像の入力中テキストを捨てない。
            var unmatched = Photos.Where(p => (p.HasDirtyDescription || p.IsCommitPending) &&
                !photos.Any(item => ReferenceEquals(item, p))).ToArray();
            imageGridSource.Photos = photos.Concat(unmatched).ToArray();
            foreach (var photo in unmatched)
            {
                photo.StatusText = "画像が変更されました";
                photo.StatusDetail = "編集中の説明を保持しています。対象画像を確認してください。";
            }
        }

        private void ImageGridWindow_Closing(object sender, CancelEventArgs e)
        {
            if (allowClose || imageGridSource.EnableCheckBox ||
                !Photos.Any(p => p.HasDirtyDescription || p.IsCommitPending))
                return;
            e.Cancel = true;
            if (closeRequested) return;
            closeRequested = true;
            InlineCloseRequested?.Invoke(this);
        }

        public void AllowCloseAfterCommit()
        {
            if (isClosedForEditing) return;
            allowClose = true;
            Close();
        }

        public void CancelCloseRequest() => closeRequested = false;

        public void ShowSavedBriefly()
        {
            savedStatusTimer.Stop();
            savedStatusTimer.Start();
        }

        private void InlineDescription_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox editor && editor.DataContext is Photo photo)
                InlineDescriptionCommitRequested?.Invoke(this, photo);
            if (pendingPhotos != null)
            {
                Photo[] latest = pendingPhotos;
                pendingPhotos = null;
                Dispatcher.BeginInvoke(new Action(() => SetPhotos(latest)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void InlineDescription_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!(sender is TextBox editor) || !(editor.DataContext is Photo photo))
                return;
            if (e.Key == Key.Escape)
            {
                photo.CancelDraft();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                InlineDescriptionCommitRequested?.Invoke(this, photo);
                e.Handled = true;
            }
            else if (e.Key == Key.Tab && imageGridSource.InlineMode &&
                     (Keyboard.Modifiers == ModifierKeys.None ||
                      Keyboard.Modifiers == ModifierKeys.Shift))
            {
                // DataTemplate内のListViewItemを経由せず、表示順の説明欄へ移る。
                // 先頭・末尾では処理せず、WPF標準のTab移動に任せる。
                Photo[] photos = Photos;
                int index = Array.IndexOf(photos, photo);
                bool backward = Keyboard.Modifiers == ModifierKeys.Shift;
                int nextIndex = index + (backward ? -1 : 1);
                if (index < 0 || nextIndex < 0 || nextIndex >= photos.Length)
                    return;

                Photo next = photos[nextIndex];
                e.Handled = true;
                if (!FocusDescriptionEditor(next))
                {
                    // 後から仮想化を導入しても、ScrollIntoView後の生成を待って移動する。
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (isClosedForEditing || !imageGridSource.InlineMode ||
                            !ReferenceEquals(Keyboard.FocusedElement, editor))
                            return;
                        if (!FocusDescriptionEditor(next))
                            editor.MoveFocus(new TraversalRequest(backward
                                ? FocusNavigationDirection.Previous
                                : FocusNavigationDirection.Next));
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            }
        }

        private bool FocusDescriptionEditor(Photo photo)
        {
            photoListView.ScrollIntoView(photo);
            photoListView.UpdateLayout();
            var item = photoListView.ItemContainerGenerator.ContainerFromItem(photo) as ListViewItem;
            var target = FindDescriptionEditor(item);
            return target != null && target.IsVisible && target.IsEnabled &&
                   target.Focus() && ReferenceEquals(Keyboard.FocusedElement, target);
        }

        private static TextBox FindDescriptionEditor(DependencyObject parent)
        {
            if (parent == null) return null;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox editor) return editor;
                TextBox nested = FindDescriptionEditor(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void ListViewItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // タイル上の編集ボタンを押した場合は画像拡大を開かない。
            DependencyObject element = e.OriginalSource as DependencyObject;
            while (element != null && !(element is ListViewItem))
            {
                if (element is Button || element is TextBox) return;
                element = element is Visual ? VisualTreeHelper.GetParent(element) : null;
            }
            if (sender is ListViewItem viewItem && viewItem.Content is Photo photo && !imageGridSource.EnableCheckBox)
                ShowImagePopup(photo);
        }

        private void EditDescriptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is Photo photo)
                EditDescriptionRequested?.Invoke(photo);
        }

        public void RenameSheet(string sheetName)
        {
            foreach (var photo in imageGridSource.Photos ?? new Photo[0])
                if (photo.Entry != null) photo.Entry.SheetName = sheetName;
        }

        private void ShowImagePopup(Photo photo)
        {
            imagePopup.Source = photo.ImageSource;
            imagePopupGrid.Visibility = Visibility.Visible;
        }

        private void imagePopupGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            imagePopupGrid.Visibility = Visibility.Hidden;
        }

        public new bool ShowDialog()
        {
            base.ShowDialog();
            return isOK;
        }

        public bool ShowDialog(out bool[] result)
        {
            ShowDialog();
            result = imageGridSource.Photos.Select(ph => { return ph.CheckBoxIsChecked == true; }).ToArray();
            return isOK;
        }

        private void deleteButton_Click(object sender, RoutedEventArgs e)
        {
            isOK = true;
            Close();
        }

        private void cancelButton_Click(object sender, RoutedEventArgs e)
        {
            isOK = false;
            Close();
        }
    }
    public class Photo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public ImageSource ImageSource { get; set; }
        public BridgeClosedXML.ImageEntry Entry { get; set; }
        private string _description = string.Empty;
        private string _draftDescription = string.Empty;
        private string _statusText = string.Empty;
        private string _statusDetail = string.Empty;
        public bool IsCommitPending { get; set; }
        public bool CommitAgain { get; set; }
        public bool IsWorkbookUnsaved { get; set; }
        public bool HasDirtyDescription =>
            !string.Equals(_draftDescription ?? string.Empty, _description ?? string.Empty, StringComparison.Ordinal);

        public string Description
        {
            get => _description;
            set
            {
                bool wasClean = !HasDirtyDescription;
                _description = value ?? string.Empty;
                RaisePropertyChanged();
                if (wasClean) DraftDescription = _description;
            }
        }
        public string DraftDescription
        {
            get => _draftDescription;
            set
            {
                if (_draftDescription == value) return;
                _draftDescription = value ?? string.Empty;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(HasDirtyDescription));
                StatusText = HasDirtyDescription ? "編集中" : string.Empty;
            }
        }
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; RaisePropertyChanged(); }
        }
        public string StatusDetail
        {
            get => _statusDetail;
            set { _statusDetail = value; RaisePropertyChanged(); }
        }
        public void CancelDraft()
        {
            DraftDescription = Description;
            StatusDetail = string.Empty;
            StatusText = IsWorkbookUnsaved ? "ブック未保存" : string.Empty;
        }
        public string Caption { get; set; }
        public Visibility CaptionVisibility { get; set; }
        public Visibility CheckBoxVisibility { get; set; }

        private bool? _CheckBoxIsChecked = false;
        public bool? CheckBoxIsChecked
        {
            get => _CheckBoxIsChecked;
            set
            {
                _CheckBoxIsChecked = value;
                RaisePropertyChanged();
            }
        }
    }

    public class ImageGridSource : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private Photo[] _Photos;
        public Photo[] Photos
        {
            get => _Photos;
            set
            {
                if (value != null)
                {
                    _Photos = value;
                    RaisePropertyChanged();
                }
            }
        }

        private bool _EnableCheckBox;
        public bool EnableCheckBox
        {
            get => _EnableCheckBox;
            set
            {
                _EnableCheckBox = value;
                RaisePropertyChanged();
            }
        }

        private bool _inlineMode;
        public bool InlineMode
        {
            get => _inlineMode;
            set
            {
                if (_inlineMode == value) return;
                _inlineMode = value;
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(InlineVisibility));
                RaisePropertyChanged(nameof(LegacyVisibility));
            }
        }
        public Visibility InlineVisibility => InlineMode ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LegacyVisibility => InlineMode ? Visibility.Collapsed : Visibility.Visible;

        private Visibility _SelectionVisibility;
        public Visibility SelectionVisibility
        {
            get => _SelectionVisibility;
            set
            {
                _SelectionVisibility = value;
                RaisePropertyChanged();
            }
        }

        public static Photo[] GetPhotosFromMemoryStream(MemoryStream[] memoryStreams)
        {
            return GetPhotosFromMemoryStream(memoryStreams, false);
        }
        public static Photo[] GetPhotosFromMemoryStream(MemoryStream[] memoryStreams, bool enableCheckBox)
        {
            int count = 1;
            try
            {
                Photo[] photos = memoryStreams.Select(ms =>
                {
                    ms.Seek(0, SeekOrigin.Begin);   // ストリームの位置をリセット
                    BitmapImage bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();            // BitmapImage の初期化の開始を通知します。
                    bitmapImage.StreamSource = ms;      // BitmapImage のストリーム ソースを設定します。
                    bitmapImage.EndInit();              // BitmapImage の初期化の終了を通知します。
                    string caption = count.ToString();
                    count++;
                    Photo photo = new Photo() { Caption = caption, ImageSource = bitmapImage };
                    if (enableCheckBox)
                    {
                        photo.CaptionVisibility = Visibility.Collapsed;
                        photo.CheckBoxVisibility = Visibility.Visible;
                    }
                    else
                    {
                        photo.CaptionVisibility = Visibility.Visible;
                        photo.CheckBoxVisibility = Visibility.Collapsed;
                    }
                    return photo;
                }).ToArray();
                return photos;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return null;
            }
        }

        public static ImageGridSource GetSourceFromImageEntries(BridgeClosedXML.ImageEntry[] entries)
        {
            return new ImageGridSource
            {
                Photos = GetPhotosFromImageEntries(entries),
                EnableCheckBox = false,
                SelectionVisibility = Visibility.Collapsed
            };
        }

        public static Photo[] GetPhotosFromImageEntries(BridgeClosedXML.ImageEntry[] entries)
        {
            var photos = GetPhotosFromMemoryStream(entries.Select(entry => entry.ImageStream).ToArray());
            if (photos == null) return null;
            for (int i = 0; i < photos.Length; i++)
            {
                photos[i].Entry = entries[i];
                photos[i].Description = entries[i].Description;
            }
            return photos;
        }

        public static ImageGridSource GetSourceFromMemoryStream(MemoryStream[] memoryStreams)
        {
            return GetSourceFromMemoryStream(memoryStreams, false);
        }
        public static ImageGridSource GetSourceFromMemoryStream(MemoryStream[] memoryStreams, bool enableCheckBox)
        {
            Photo[] photos = GetPhotosFromMemoryStream(memoryStreams, enableCheckBox);
            Visibility selectionVisibility;
            if (enableCheckBox)
            {
                selectionVisibility = Visibility.Visible;
            }
            else
            {
                selectionVisibility = Visibility.Collapsed;
            }
            return new ImageGridSource() { Photos = photos, EnableCheckBox = enableCheckBox, SelectionVisibility = selectionVisibility };
        }
    }
    // Excelの説明は複数行のセル文字列。既存の単一行StringInputWindowは変更しない。
    internal sealed class ImageDescriptionWindow : Window
    {
        private readonly TextBox editor;
        public string Description => editor.Text;

        public ImageDescriptionWindow(string initialText)
        {
            Title = "画像の説明を追加・編集";
            Width = 520;
            Height = 280;
            MinWidth = 360;
            MinHeight = 200;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var layout = new DockPanel { Margin = new Thickness(10) };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(buttons, Dock.Bottom);
            buttons.Children.Add(new Button
            {
                Content = "保存", Width = 85, Margin = new Thickness(0, 8, 6, 0),
                IsDefault = true
            });
            var save = (Button)buttons.Children[0];
            save.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "キャンセル", Width = 85, Margin = new Thickness(0, 8, 0, 0), IsCancel = true };
            buttons.Children.Add(cancel);
            layout.Children.Add(buttons);
            editor = new TextBox
            {
                Text = initialText ?? string.Empty, AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, MaxLength = 32767,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            layout.Children.Add(editor);
            Content = layout;
            Loaded += (s, e) => editor.Focus();
        }
    }

}
