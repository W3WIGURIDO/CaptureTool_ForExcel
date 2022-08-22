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
        public ImageGridWindow(ImageGridSource imageGridSource)
        {
            InitializeComponent();
            DataContext = imageGridSource;
            this.imageGridSource = imageGridSource;
        }

        public void SetPhotos(Photo[] photos)
        {
            imageGridSource.Photos = photos;
        }

        private void ListViewItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListViewItem viewItem && viewItem.Content is Photo photo && !imageGridSource.EnableCheckBox)
            {
                ShowImagePopup(photo);
            }
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
    }
    public class Photo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void RaisePropertyChanged([CallerMemberName] string propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public ImageSource ImageSource { get; set; }
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
}
