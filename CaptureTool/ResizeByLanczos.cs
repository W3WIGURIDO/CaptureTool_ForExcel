using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace CaptureTool
{
    class ResizeByLanczos
    {
        //窓関数
        private static double Sinc(double d)
        {
            return Math.Sin(Math.PI * d) / (Math.PI * d);
        }
        /// <summary>
        /// ランチョス補完法での重み計算
        /// </summary>
        /// <param name="d">距離</param>
        /// <param name="n">最大参照距離</param>
        /// <returns></returns>
        private static double GetLanczosWeight(double d, int n)
        {
            if (d == 0) return 1.0;
            else if (d > n) return 0.0;
            else return Sinc(d) * Sinc(d / n);
        }


        /// <summary>
        /// 画像の拡大縮小、ランチョス法で補完、PixelFormats.Bgra32専用)
        /// 通常版
        /// </summary>
        /// <param name="source">PixelFormats.Bgra32のBitmap</param>
        /// <param name="width">変換後の横ピクセル数を指定</param>
        /// <param name="height">変換後の縦ピクセル数を指定</param>
        /// <param name="n">最大参照距離、3か4がいい</param>
        /// <returns></returns>
        public static BitmapSource LanczosBgra32(BitmapSource source, int width, int height, int n)
        {
            //1ピクセルあたりのバイト数、Byte / Pixel
            int pByte = (source.Format.BitsPerPixel + 7) / 8;

            //元画像の画素値の配列作成
            int sourceWidth = source.PixelWidth;
            int sourceHeight = source.PixelHeight;
            int sourceStride = sourceWidth * pByte;//1行あたりのbyte数
            byte[] sourcePixels = new byte[sourceHeight * sourceStride];
            source.CopyPixels(sourcePixels, sourceStride, 0);

            //変換後の画像の画素値の配列用
            double widthScale = (double)sourceWidth / width;//横倍率
            double heightScale = (double)sourceHeight / height;
            int stride = width * pByte;
            byte[] pixels = new byte[height * stride];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    //参照点
                    double rx = (x + 0.5) * widthScale;
                    double ry = (y + 0.5) * heightScale;
                    //参照点四捨五入で基準
                    int xKijun = (int)(rx + 0.5);
                    int yKijun = (int)(ry + 0.5);
                    //修正した重み取得
                    var ws = GetFixWeights(rx, ry, n);

                    double bSum = 0, gSum = 0, rSum = 0, aSum = 0;
                    double alphaFix = 0;
                    //参照範囲は基準から上(xは左)へn、下(xは右)へn-1の範囲
                    for (int yy = -n; yy < n; yy++)
                    {
                        int yc = yKijun + yy;
                        //マイナス座標や画像サイズを超えていたら、収まるように修正
                        yc = yc < 0 ? 0 : yc > sourceHeight - 1 ? sourceHeight - 1 : yc;
                        for (int xx = -n; xx < n; xx++)
                        {
                            int xc = xKijun + xx;
                            xc = xc < 0 ? 0 : xc > sourceWidth - 1 ? sourceWidth - 1 : xc;
                            int pp = (yc * sourceStride) + (xc * pByte);
                            double weight = ws[xx + n, yy + n];
                            //完全透明ピクセル(a=0)だった場合はRGBは計算しないで
                            //重みだけ足し算して後で使う
                            if (sourcePixels[pp + 3] == 0)
                            {
                                alphaFix += weight;
                                continue;
                            }
                            bSum += sourcePixels[pp] * weight;
                            gSum += sourcePixels[pp + 1] * weight;
                            rSum += sourcePixels[pp + 2] * weight;
                            aSum += sourcePixels[pp + 3] * weight;
                        }
                    }

                    //                 C#、WPF、バイリニア法での画像の拡大縮小変換、半透明画像(32bit画像)対応版 - 午後わてんのブログ
                    //https://gogowaten.hatenablog.com/entry/2021/04/17/151803#32bit%E3%81%A824bit%E3%81%AF%E9%81%95%E3%81%A3%E3%81%9F
                    //完全透明ピクセルによるRGB値の修正
                    //参照範囲がすべて完全透明だった場合は0のままでいいので計算しない
                    if (alphaFix == 1) continue;
                    //完全透明ピクセルが混じっていた場合は、その分を差し引いてRGB修正する
                    double rgbFix = 1 / (1 - alphaFix);
                    bSum *= rgbFix;
                    gSum *= rgbFix;
                    rSum *= rgbFix;

                    //0～255の範囲を超えることがあるので、修正
                    bSum = bSum < 0 ? 0 : bSum > 255 ? 255 : bSum;
                    gSum = gSum < 0 ? 0 : gSum > 255 ? 255 : gSum;
                    rSum = rSum < 0 ? 0 : rSum > 255 ? 255 : rSum;
                    aSum = aSum < 0 ? 0 : aSum > 255 ? 255 : aSum;

                    int ap = (y * stride) + (x * pByte);
                    pixels[ap] = (byte)(bSum + 0.5);
                    pixels[ap + 1] = (byte)(gSum + 0.5);
                    pixels[ap + 2] = (byte)(rSum + 0.5);
                    pixels[ap + 3] = (byte)(aSum + 0.5);
                }
            };

            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, source.Format, null, pixels, stride);
            return bitmap;

        }

        //修正した重み取得
        private static double[,] GetFixWeights(double rx, double ry, int n)
        {
            int nn = n * 2;//全体の参照距離
                           //基準になる距離計算
            double sx = rx - (int)rx;
            double sy = ry - (int)ry;
            double dx = (sx < 0.5) ? 0.5 - sx : 0.5 - sx + 1;
            double dy = (sy < 0.5) ? 0.5 - sy : 0.5 - sy + 1;

            //各ピクセルの重みと、重み合計を計算
            double[] xw = new double[nn];
            double[] yw = new double[nn];
            double xSum = 0, ySum = 0;
            for (int i = -n; i < n; i++)
            {
                double x = GetLanczosWeight(Math.Abs(dx + i), n);
                xSum += x;
                xw[i + n] = x;
                double y = GetLanczosWeight(Math.Abs(dy + i), n);
                ySum += y;
                yw[i + n] = y;
            }

            //重み合計で割り算して修正、全体で100%(1.0)にする
            for (int i = 0; i < nn; i++)
            {
                xw[i] /= xSum;
                yw[i] /= ySum;
            }

            // x * y
            double[,] ws = new double[nn, nn];
            for (int y = 0; y < nn; y++)
            {
                for (int x = 0; x < nn; x++)
                {
                    ws[x, y] = xw[x] * yw[y];
                }
            }
            return ws;
        }
    }
}
