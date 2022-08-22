﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Drawing;
using System.Windows;
using System.Drawing.Imaging;
using System.IO;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace CaptureTool
{
    public static class MainProcess
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, int flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_SHOWWINDOW = 0x0040;
        public const uint TOPMOST_FLAGS = (SWP_NOSIZE | SWP_NOMOVE);
        public const uint NOTOPMOST_FLAGS = (SWP_SHOWWINDOW | SWP_NOSIZE | SWP_NOMOVE);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        const int ASYNCWINDOWPOS = 0x4000;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private extern static bool PrintWindow(IntPtr hwnd, IntPtr hDC, uint nFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("dwmapi.dll", PreserveSig = false)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll", PreserveSig = false)]
        public static extern int DwmIsCompositionEnabled(ref int pfEnabled);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, DWMWINDOWATTRIBUTE attr, out RECT lpRect, int attrSize);

        public enum DWMWINDOWATTRIBUTE : uint
        {
            NCRenderingEnabled = 1,
            NCRenderingPolicy,
            TransitionsForceDisabled,
            AllowNCpaint,
            CaptionButtonBounds,
            NonClientRtlLayout,
            ForceIconicRepresentation,
            Flip3DPolicy,
            ExtendedFrameBounds,
            HasIconBitmap,
            DisallowPeek,
            ExcludedFromPeek,
            Cloak,
            Cloaked,
            FreezeRepresentation,
            PlaceHolder1,
            PlaceHolder2,
            PlaceHolder3,
            AccentPolicy = 19
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        public extern static bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public extern static IntPtr GetWindowDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public extern static IntPtr GetDC(IntPtr hwnd);

        [DllImport("gdi32.dll")]
        public extern static int GetDeviceCaps(IntPtr hdc, int index);

        [DllImport("user32.dll")]
        public extern static int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        public static int SM_CXSCREEN = 0;
        public static int SM_CYSCREEN = 1;
        public static int SM_CXFULLSCREEN = 16;
        public static int SM_CYFULLSCREEN = 17;
        public static int SM_XVIRTUALSCREEN = 76;
        public static int SM_YVIRTUALSCREEN = 77;
        public static int SM_CXVIRTUALSCREEN = 78;
        public static int SM_CYVIRTUALSCREEN = 79;

        [DllImport("user32.dll")]
        public extern static int GetSystemMetrics(int smIndex);

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(out CURSORINFO pci);

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        public struct POINT
        {
            public int X;
            public int Y;
        }

        public const uint CURSOR_SHOWING = 0x00000001;

        [DllImport("user32.dll")]
        public static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO pIconInfo);

        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("gdi32.dll")]
        public static extern int BitBlt(IntPtr hDestDC, int x, int y, int nWidth, int nHeight, IntPtr hsrcDC, int xSrc, int ySrc, int dwRop);

        public const int SRCCOPY = 13369376;


        public static OverlayWindow prevOverlayWindow;
        private const string FormatText = "Text";

        public static bool CaptureScreen(BridgeClosedXML bridgeClosedXML, int overlayTime = 3000, bool enableOverlay = true, HorizontalAlignment overlayHorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment overlayVerticalAlignment = VerticalAlignment.Top, bool screenFlag = true, bool aero = true, double imageGridWidth = 200, double imageGridHeight = 150, bool enableCursor = false, int captureMode = 0, bool enableSetArrow = false, System.Drawing.Imaging.PixelFormat pixelFormat = System.Drawing.Imaging.PixelFormat.Format32bppArgb)
        {
            //const string WordWindowTitle = "<WindowTitle>";
            //const string WordDate = "<Date>";
            //const string WordTime = "<Time>";
            //const string DesktopText = "Desktop";
            try
            {
                IntPtr windowHandle = GetForegroundWindow();
                //Process process = null;
                //void GetProcessFromHandle()
                //{
                //    if (process == null)
                //    {
                //        try
                //        {
                //            GetWindowThreadProcessId(windowHandle, out int processID);
                //            process = Process.GetProcessById(processID);
                //        }
                //        catch (Exception pex)
                //        {
                //            Console.WriteLine(pex.Message);
                //        }
                //    }
                //}

                //if (fileName.Contains(WordWindowTitle))
                //{
                //    if (screenFlag)
                //    {
                //        fileName = fileName.Replace(WordWindowTitle, DesktopText);
                //    }
                //    else
                //    {
                //        GetProcessFromHandle();
                //        string title = process.MainWindowTitle;
                //        if (string.IsNullOrEmpty(title))
                //        {
                //            title = DesktopText;
                //        }
                //        title = System.Text.RegularExpressions.Regex.Replace(title, "[\\\\/:*?\"<>|]", string.Empty);
                //        fileName = fileName.Replace(WordWindowTitle, title);
                //        dirName = dirName.Replace(WordWindowTitle, title);
                //    }
                //}
                //if (fileName.Contains(WordDate))
                //{
                //    string nowYMD = DateTime.Now.ToString("yyyyMMdd");
                //    fileName = fileName.Replace(WordDate, nowYMD);
                //    dirName = dirName.Replace(WordDate, nowYMD);
                //}
                //if (fileName.Contains(WordTime))
                //{
                //    string nowHMS = DateTime.Now.ToString("HHmmss");
                //    fileName = fileName.Replace(WordTime, nowHMS);
                //    dirName = dirName.Replace(WordTime, nowHMS);
                //}
                //CreateDirectory(dirName);

                if (prevOverlayWindow != null && prevOverlayWindow.ClosingReady)
                {
                    prevOverlayWindow.Close();
                }

                Bitmap bitmap = CaptureControl(windowHandle, captureMode, false, screenFlag, aero, enableCursor, enableSetArrow, pixelFormat);
                bridgeClosedXML.AddImage(bitmap);
                //ImageCodecInfo codecInfo = null;
                //foreach(ImageCodecInfo tmpInfo in ImageCodecInfo.GetImageEncoders())
                //{
                //    if (tmpInfo.FormatID == ImageFormat.Png.Guid)
                //    {

                //    }
                //}
                //if (File.Exists(fileName))
                //{
                //    string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
                //    string extension = Path.GetExtension(fileName);
                //    fileName = dirName + "\\" + fileNameWithoutExtension + "_" + DateTime.Now.ToString("yyyyMMddHHmmss") + extension;
                //}
                //ImageFormat imageFormat;
                //if (imageFormatName.ToUpper().CompareTo("PNG") == 0)
                //{
                //    imageFormat = ImageFormat.Png;
                //}
                //else if (imageFormatName.ToUpper().CompareTo("JPG") == 0)
                //{
                //    imageFormat = ImageFormat.Jpeg;
                //}
                //else
                //{
                //    imageFormat = ImageFormat.Png;
                //}
                //bitmap.Save(fileName, imageFormat);
                ImageSource imageSource = Extend.ConvertBitmapToBitmapImage(bitmap);
                if (enableOverlay)
                {
                    OverlayWindow overlayWindow = new OverlayWindow()
                    {
                        ImageSource = imageSource,
                        OverlayTime = overlayTime,
                        OverlayHorizontalAlignment = overlayHorizontalAlignment,
                        OverlayVerticalAlignment = overlayVerticalAlignment,
                        ImageGridWidth = imageGridWidth,
                        ImageGridHeight = imageGridHeight
                    };
                    overlayWindow.Show();
                    prevOverlayWindow = overlayWindow;
                }
            }
            catch (Exception ex)
            {
                WpfFolderBrowser.CustomMessageBox.Show(MainWindow.ActiveWindow, ex.Message, "例外", MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK, MessageBoxOptions.None);
                return false;
            }
            return true;
        }

        public static ImageSource GetImageSourceFromWindow(IntPtr handle, int mode, bool enableCursor, bool enableSetArrow, System.Drawing.Imaging.PixelFormat pixelFormat)
        {
            Bitmap bitmap = CaptureControl(handle, mode, true, false, true, enableCursor, enableSetArrow, pixelFormat);
            return Extend.ConvertBitmapToBitmapImage(bitmap);
        }

        private static Bitmap CaptureControl(IntPtr handle, int mode, bool extend, bool screenFlag, bool aero, bool enableCursor, bool enableSetArrow, System.Drawing.Imaging.PixelFormat pixelFormat)
        {
            //bool flag = GetWindowRect(handle, out RECT rect);
            int width;
            int height;
            RECT rect;
            if (screenFlag)
            {
                if (mode == 0)
                {
                    GetWindowRect(handle, out rect);
                    width = rect.right - rect.left;
                    height = rect.bottom - rect.top;
                    if (width <= 0)
                    {
                        width = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                    }
                    if (height <= 0)
                    {
                        height = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                    }
                }
                else
                {
                    //float dpiScale = (new System.Windows.Forms.Form().CreateGraphics().DpiX) / 96;
                    //System.Windows.Forms.Form tform = new System.Windows.Forms.Form();
                    //tform.Show();
                    //double tfDPI = tform.DeviceDpi;

                    //1
                    //SetProcessDPIAware();
                    //var dc = GetWindowDC(IntPtr.Zero);
                    //int dx = GetDeviceCaps(dc, 88);
                    //int dy = GetDeviceCaps(dc, 90);
                    //Graphics tgrp = Graphics.FromHdc(dc);
                    //float gdx = tgrp.DpiX;
                    //float gdy = tgrp.DpiY;
                    //ReleaseDC(IntPtr.Zero, dc);
                    //1

                    //2
                    //double widthDPI = PresentationSource.FromVisual(MainWindow.GetMainWindow()).CompositionTarget.TransformFromDevice.M11;
                    //double heightDPI = PresentationSource.FromVisual(MainWindow.GetMainWindow()).CompositionTarget.TransformFromDevice.M12;
                    //width = (int)(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width * widthDPI);
                    //height = (int)(System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height * heightDPI);
                    //2

                    width = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                    height = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;

                    //int smx = GetSystemMetrics(SM_CXSCREEN);
                    //int smy = GetSystemMetrics(SM_CYSCREEN);
                    //int sfx = GetSystemMetrics(SM_CXFULLSCREEN);
                    //int sfy = GetSystemMetrics(SM_CYFULLSCREEN);
                    //int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
                    //int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
                    //int cvx = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                    //int cvy = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                    //width = (int)System.Windows.SystemParameters.VirtualScreenWidth;
                    //height = (int)System.Windows.SystemParameters.VirtualScreenHeight;
                    rect = new RECT() { left = 0, right = width, top = 0, bottom = height };
                }
            }
            else
            {
                if (mode == 0)
                {
                    aero = false;
                }
                int rectSize;
                unsafe
                {
                    rectSize = sizeof(RECT);
                }
                if (aero)
                {
                    DwmGetWindowAttribute(handle, DWMWINDOWATTRIBUTE.ExtendedFrameBounds, out rect, rectSize);
                }
                else
                {
                    GetWindowRect(handle, out rect);
                }
                if (extend)
                {
                    rect.left = rect.left - 1;
                    rect.right = rect.right + 1;
                    rect.top = rect.top - 1;
                    rect.bottom = rect.bottom + 1;
                }
                width = rect.right - rect.left;
                height = rect.bottom - rect.top;
                if (width <= 0)
                {
                    width = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                }
                if (height <= 0)
                {
                    height = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                }
            }

            Bitmap img = new Bitmap(width, height);
            //Bitmap img = new Bitmap(width, height, pixelFormat);
            Graphics memg = Graphics.FromImage(img);
            if (mode == 0)
            {
                IntPtr dc = memg.GetHdc();
                PrintWindow(handle, dc, 0);
                memg.ReleaseHdc(dc);
                if (screenFlag)
                {
                    int pwidth = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width;
                    int pheight = System.Windows.Forms.Screen.PrimaryScreen.Bounds.Height;
                    System.Drawing.Rectangle rectangle = System.Drawing.Rectangle.FromLTRB(0, 0, pwidth, pheight);
                    Bitmap img2 = new Bitmap(pwidth, pheight);
                    //Bitmap img = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    Graphics memg2 = Graphics.FromImage(img2);
                    memg.Dispose();
                    memg2.CopyFromScreen(0, 0, 0, 0, rectangle.Size, CopyPixelOperation.SourceCopy);
                    memg2.DrawImage(img, new PointF(rect.left, rect.top));
                    memg = memg2;
                    img = img2;
                }
            }
            else if (mode == 1)
            {
                System.Drawing.Rectangle rectangle = System.Drawing.Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);
                memg.CopyFromScreen(rect.left, rect.top, 0, 0, rectangle.Size, CopyPixelOperation.SourceCopy);
            }
            else
            {
                RECT rect2 = new RECT();
                IntPtr targetDC;
                if (screenFlag)
                {
                    IntPtr disDC = GetDC(IntPtr.Zero);
                    targetDC = disDC;
                }
                else
                {
                    GetWindowRect(handle, out rect2);
                    IntPtr winDC = GetWindowDC(handle);
                    targetDC = winDC;
                }
                IntPtr hDC = memg.GetHdc();
                BitBlt(hDC, 0, 0, img.Width, img.Height, targetDC, rect.left - rect2.left, rect.top - rect2.top, SRCCOPY);
                memg.ReleaseHdc();
            }


            //マウスカーソル関連
            if (enableCursor)
            {
                if (enableSetArrow)
                {
                    WriteCursorToGrap(memg, rect.left, rect.top, enableSetArrow);
                }
                else
                {
                    WriteCursorToGrap2(memg, rect.left, rect.top, enableSetArrow);
                }
            }
            memg.Dispose();

            if (pixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppArgb)
            {
                Bitmap encoded = img.Clone(new Rectangle(0, 0, img.Width, img.Height), pixelFormat);
                return encoded;
            }
            else
            {
                return img;
            }
        }

        public static void WriteCursorToGrap(Graphics g, int left, int top, bool enableSetArrow)
        {
            System.Windows.Forms.Cursor cursor;
            if (enableSetArrow)
            {
                cursor = System.Windows.Forms.Cursors.Arrow;
            }
            else
            {
                cursor = new System.Windows.Forms.Cursor(System.Windows.Forms.Cursor.Current.Handle);
            }
            System.Drawing.Point cPoint = System.Windows.Forms.Cursor.Position;
            System.Drawing.Point hSpot = cursor.HotSpot;
            System.Drawing.Point DrPosition = new System.Drawing.Point((cPoint.X - hSpot.X - left), (cPoint.Y - hSpot.Y - top));
            cursor.Draw(g, new Rectangle(DrPosition, cursor.Size));
        }


        public static void WriteCursorToGrap2(Graphics g, int left, int top, bool enableSetArrow)
        {
            var cInfo = new CURSORINFO
            {
                cbSize = Marshal.SizeOf(typeof(CURSORINFO))
            };
            if (!GetCursorInfo(out cInfo) || cInfo.flags != CURSOR_SHOWING)
            {
                return;
            }
            IntPtr cIcon = CopyIcon(cInfo.hCursor);
            if (cIcon == IntPtr.Zero)
            {
                return;
            }

            var oIcon = System.Drawing.Icon.FromHandle(cIcon);
            var oBitmap = oIcon.ToBitmap();
#if DEBUG
            //oBitmap.Save(@"C:\Users\fujimori.satoshi\Pictures\Capture\Test\cursor\cursor.png");
#endif
            if (!GetIconInfo(cIcon, out ICONINFO iconInfo))
            {
                return;
            }
            System.Drawing.Point DrPosition = new System.Drawing.Point((cInfo.ptScreenPos.X - iconInfo.xHotspot - left), (cInfo.ptScreenPos.Y - iconInfo.yHotspot - top));
            Bitmap hbmMask;
            Bitmap hbmColor;
            if (iconInfo.hbmMask != IntPtr.Zero)
            {
                hbmMask = System.Drawing.Image.FromHbitmap(iconInfo.hbmMask);
#if DEBUG
                //hbmMask.Save(@"C:\Users\fujimori.satoshi\Pictures\Capture\Test\cursor\mask.png");
#endif
                Bitmap tempB = new Bitmap(hbmMask.Width, hbmMask.Width);
                Bitmap topMask = null;
                Bitmap underMask = null;
                if (hbmMask.Height == hbmMask.Width * 2 && oBitmap.Width == hbmMask.Width && oBitmap.Height == hbmMask.Width)
                {
                    underMask = hbmMask.Clone(new Rectangle(0, hbmMask.Width, hbmMask.Width, hbmMask.Width), hbmMask.PixelFormat);
                }
                else if (iconInfo.hbmColor != IntPtr.Zero)
                {
                    //topMask = oBitmap.Clone(new Rectangle(0, 0, oBitmap.Width, oBitmap.Height), oBitmap.PixelFormat);
                    topMask = hbmMask.Clone(new Rectangle(0, 0, hbmMask.Width, hbmMask.Width), hbmMask.PixelFormat);
                    hbmColor = System.Drawing.Image.FromHbitmap(iconInfo.hbmColor);
                    underMask = hbmColor;
#if DEBUG
                    //hbmColor.Save(@"C:\Users\fujimori.satoshi\Pictures\Capture\Test\cursor\color.png");
#endif
                }
                else
                {
                    g.DrawImage(oBitmap, DrPosition.X, DrPosition.Y);
                    return;
                }
                for (int tx = 0; tx < hbmMask.Width; tx++)
                {
                    for (int ty = 0; ty < hbmMask.Width; ty++)
                    {
                        System.Drawing.Color oc = oBitmap.GetPixel(tx, ty);
                        System.Drawing.Color tc;
                        if (topMask == null)
                        {
                            tc = oc;
                        }
                        else
                        {
                            tc = topMask.GetPixel(tx, ty);
                            //tc = System.Drawing.Color.FromArgb(oc.A, tc.R ^ oc.R, tc.G ^ oc.G, tc.B ^ oc.B);
                        }
                        System.Drawing.Color uc = underMask.GetPixel(tx, ty);
                        System.Drawing.Color xorColor = System.Drawing.Color.FromArgb(oc.A, tc.R ^ uc.R, tc.G ^ uc.G, tc.B ^ uc.B);
                        //if (topMask != null && tc.ToArgb() == System.Drawing.Color.White.ToArgb())
                        //{
                        //    xorColor = System.Drawing.Color.Transparent;
                        //}
                        tempB.SetPixel(tx, ty, xorColor);
                    }
                }
#if DEBUG
                //tempB.Save(@"C:\Users\fujimori.satoshi\Pictures\Capture\Test\cursor\temp.png");
#endif
                g.DrawImage(tempB, DrPosition.X, DrPosition.Y);
                return;
            }
            g.DrawIcon(oIcon, DrPosition.X, DrPosition.Y);

            /*
            Bitmap gBitmap = new Bitmap(oBitmap.Width, oBitmap.Height);
            Graphics graphics = Graphics.FromImage(gBitmap);
            graphics.DrawImage(oBitmap, 0, 0);
            graphics.Dispose();
            gBitmap.Save(@"C:\tmp\testCursor.png", ImageFormat.Png);
            var cBitmap = new Bitmap(oBitmap);
            cBitmap.Save(@"C:\tmp\testCursor.png", ImageFormat.Png);
            oIcon.ToBitmap().Save(@"C:\temp\testCursor.png", ImageFormat.Png);
            //Bitmap oCursor = System.Drawing.Image.FromHbitmap(cInfo.hCursor);
            //oCursor.Save("C:\temp\testCursor.png", ImageFormat.Png);
            */
        }

        public static System.Windows.Media.Imaging.BitmapSource GetBitmapSourceFromIconHandle(IntPtr hIcon)
        {
            System.Windows.Media.Imaging.BitmapSource bitmapSource = null;
            try
            {
                bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            return bitmapSource;
        }

        public static Bitmap ConvertBitmapSourceToBitmap(System.Windows.Media.Imaging.BitmapSource bitmapSource)
        {
            var bitmap = new Bitmap(bitmapSource.PixelWidth, bitmapSource.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bitmapData = bitmap.LockBits(new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            bitmapSource.CopyPixels(System.Windows.Int32Rect.Empty, bitmapData.Scan0, bitmapData.Height * bitmapData.Stride, bitmapData.Stride);
            bitmap.UnlockBits(bitmapData);
            return bitmap;
        }

        public static void CreateDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }

        public static MatchCollection FileNameNumberSearch(string fileName)
        {
            Regex regex = new Regex("[0-9]+");
            return regex.Matches(fileName);
        }

        public static void CreateFileNameNumberCountButtons(string fileName, StackPanel owner, Settings settings)
        {
            MatchCollection matchCollection = FileNameNumberSearch(fileName);
            if (matchCollection.Count > 0)
            {
                owner.Height = 22;
            }
            else
            {
                owner.Height = 0;
            }
            Grid[] grids = new Grid[matchCollection.Count];
            int count = 0;
            owner.Children.Clear();
            foreach (Match match in matchCollection)
            {
                Match tmpMatch = match;
                TextBlock textBlock = new TextBlock() { Text = count + ":" };
                StackPanel stackPanel = new StackPanel() { Margin = new Thickness(0, 0, 2, 0) };
                Button upButton = new Button() { Content = new TextBlock() { Text = "▲", RenderTransformOrigin = new System.Windows.Point(0.5, 0.5), RenderTransform = new ScaleTransform(3.5, 1.3) }, FontSize = 5, MinWidth = 20 };
                upButton.Click += (sender, e) =>
                {
                    if (int.TryParse(match.Value, out int parsed))
                    {
                        string numberFormat = "{0:D" + tmpMatch.Length + "}";
                        string formatedNumber = string.Format(numberFormat, parsed + 1);
                        settings.FileName = settings.FileName.ReplaceAt(tmpMatch.Index, tmpMatch.Length, formatedNumber);
                    }
                };
                Button downButton = new Button() { Content = new TextBlock() { Text = "▼", RenderTransformOrigin = new System.Windows.Point(0.5, 1), RenderTransform = new ScaleTransform(3.5, 1.3) }, FontSize = 5, MinWidth = 20 };
                downButton.Click += (sender, e) =>
                {
                    if (int.TryParse(match.Value, out int parsed))
                    {
                        string numberFormat = "{0:D" + tmpMatch.Length + "}";
                        string formatedNumber = string.Format(numberFormat, parsed - 1);
                        settings.FileName = settings.FileName.ReplaceAt(tmpMatch.Index, tmpMatch.Length, formatedNumber);
                    }
                };
                stackPanel.Children.Add(upButton);
                stackPanel.Children.Add(downButton);
                owner.Children.Add(textBlock);
                owner.Children.Add(stackPanel);
                count++;
            }
        }

        public static string ReplaceAt(this string str, int index, int length, string replace)
        {
            return str.Remove(index, Math.Min(length, str.Length - index)).Insert(index, replace);
        }

        public static string GetClipBoardText()
        {
            string result = null;
            while (result == null)
            {
                try
                {
                    result = Clipboard.GetText(TextDataFormat.Text);
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                }
            }
            return result;
        }

        public static int GetContinueFileName(Settings settings)
        {
            string numberFormat = "{0:D" + settings.NumberDigits + "}";
            for (int index = 0; ; index++)
            {
                string formatedNumber = string.Format(numberFormat, index);
                string tmpSampleFileName = settings.FileName + formatedNumber + "." + settings.SaveFormats[(SaveFormat)settings.SaveFormatIndex];
                if (!File.Exists(settings.Directory + "\\" + tmpSampleFileName))
                {
                    return index;
                }
            }
        }

        public static string TextLastCountUp(string text)
        {
            Regex regex = new Regex("[0-9]+");
            MatchCollection matchCollection = regex.Matches(text);
            if (matchCollection.Count > 0)
            {
                Match lastMatch = matchCollection[matchCollection.Count - 1];
                if (int.TryParse(lastMatch.Value, out int result))
                {
                    string countUp = (result + 1).ToString();
                    return text.ReplaceAt(lastMatch.Index, lastMatch.Length, countUp);
                }
            }
            return text;
        }
    }
}
