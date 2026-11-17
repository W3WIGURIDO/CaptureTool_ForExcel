using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;

namespace CaptureTool
{
    public class BridgeClosedXML
    {
        private Settings settings;
        private XLWorkbook workbook;
        private IXLWorksheet worksheet;
        public bool ModFlag { get; set; } = false;
        public bool AutoSave { get; set; } = true;

        private void SetWorkSheetsDictionary()
        {
            settings.WorkSheets = workbook.Worksheets.OrderBy(ws => { return ws.Position; }).ToDictionary(ws => { return ws.Position; }, ws => { return ws.Name; });
        }

        public BridgeClosedXML(Settings settings)
        {
            this.settings = settings;
            if (File.Exists(settings.FileName))
            {
                try
                {
                    workbook = new XLWorkbook(settings.FileName);
                    worksheet = workbook.Worksheet(1);
                    LoadWorkSheet(worksheet);
                    SetWorkSheetsDictionary();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                    Environment.Exit(1);
                }
            }
            else
            {
                CreateNewBook();
            }
            AutoSave = settings.EnableWorkBookAutoSave == true;
        }


        public void AddImage(Bitmap bitmap)
        {
            MemoryStream ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);   //using System.Drawing.Imaging;
            AddImage(ms, bitmap.Height);
            AutoSaveOrModFlagUpdate();
        }

        public void AddImage(MemoryStream ms, int height)
        {
            // 行高はシート内で異なる可能性があるので、実際の行高を合計する。
            // 画像を置く前に次回挿入位置まで検証し、範囲外の画像は追加しない。
            const int maxRow = 1048576;
            int startRow = settings.Row;
            if (startRow < 1 || startRow > maxRow || height <= 0)
                throw new InvalidOperationException("画像の挿入行または画像サイズが不正です。");

            double coveredPixels = 0;
            int row = startRow;
            while (row <= maxRow && coveredPixels < height)
            {
                coveredPixels += worksheet.Row(row).Height / 0.75; // Excelのptをpxへ
                row++;
            }

            long nextRow = (long)row + settings.ImageGapRows;
            if (coveredPixels < height || nextRow > maxRow)
                throw new InvalidOperationException("画像または行間隔がExcelの最大行数を超えます。");

            ms.Position = 0;
            ClosedXML.Excel.Drawings.IXLPicture insertedPic = worksheet.AddPicture(ms);
            insertedPic.MoveTo(worksheet.Cell(startRow, 1));
            settings.Row = (int)nextRow;
            worksheet.Cell(1, 1).Value = settings.Row;
        }

        // 開いているシートがまだ空で、挿入位置を個別に変更していない場合だけ反映。
        public void ApplyStartRowToEmptySheet(int previousStartRow)
        {
            if (worksheet != null && worksheet.Pictures.Count == 0 &&
                (settings.Row == previousStartRow || settings.Row == 1) &&
                settings.Row != settings.StartRow)
            {
                settings.Row = settings.StartRow;
                worksheet.Cell(1, 1).Value = settings.Row;
                worksheet.Cell(settings.Row, 1).Select();
                AutoSaveOrModFlagUpdate();
            }
        }

        public void ChangeSelectionWorkSheet(int position)
        {
            worksheet = workbook.Worksheet(position);
            LoadWorkSheet(worksheet);
        }

        private void LoadWorkSheet(IXLWorksheet ws)
        {
            // A1には従来からシートごとの次回挿入行が保存されている。
            if (int.TryParse(ws.Cell(1, 1).GetValue<string>(), out int savedRowCount) &&
                savedRowCount >= 1 && savedRowCount <= 1048576)
            {
                settings.Row = savedRowCount == 1 && ws.Pictures.Count == 0
                    ? settings.StartRow : savedRowCount;
            }
            else
            {
                settings.Row = settings.StartRow;
            }
            ws.Cell(settings.Row, 1).Select();
        }

        public void AddWorkSheet(string name)
        {
            try
            {
                workbook.AddWorksheet(name, workbook.Worksheets.Count + 1);
                AutoSaveOrModFlagUpdate();
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void RenameWorkSheet(string name)
        {
            try
            {
                worksheet.Name = name;
                AutoSaveOrModFlagUpdate();
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void SaveAsNowBook()
        {
            try
            {
                workbook.SaveAs(settings.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public bool OpenWorkBook()
        {
            if (File.Exists(settings.FileName))
            {
                try
                {
                    workbook = new XLWorkbook(settings.FileName);
                    worksheet = workbook.Worksheet(1);
                    LoadWorkSheet(worksheet);
                    SetWorkSheetsDictionary();
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                    return false;
                }
            }
            return false;
        }

        public bool CreateNewBook()
        {
            try
            {
                workbook = new XLWorkbook();
                worksheet = workbook.Worksheets.Add("エビデンス");
                workbook.SaveAs(settings.FileName);
                settings.Row = settings.StartRow;
                worksheet.Cell(settings.Row, 1).Select();
                SetWorkSheetsDictionary();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                Environment.Exit(1);
                return false;
            }
        }

        public MemoryStream[] GetImageList()
        {
            return worksheet.Pictures.Select(ixlpic =>
            {
                MemoryStream ms = ixlpic.ImageStream;
                return ms;
            }).ToArray();
        }

        public void DeleteWorkSheet()
        {
            try
            {
                workbook.Worksheets.Delete(worksheet.Position);
                SetWorkSheetsDictionary();
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        /// <summary>
        /// 画像削除処理（その２）
        /// ClosedXMLの仕様上画像の削除を行ってもシェイプオブジェクトが残るため、画像削除済みのシートを新たに作成し差し替える。
        /// 処理の都合上自動保存必須
        /// </summary>
        /// <param name="deleteSettings"></param>
        public void ReplaceDeletedImagesSheet(bool[] deleteSettings)
        {
            int count = 0;
            List<ClosedXML.Excel.Drawings.IXLPicture> addList = new List<ClosedXML.Excel.Drawings.IXLPicture>();
            deleteSettings.Select(ds =>
            {
                if (!ds)
                {
                    if (worksheet.Pictures.Count > count)
                    {
                        addList.Add(worksheet.Pictures.ElementAt(count));
                    }
                }
                return count++;
            }).ToArray();
            IXLWorksheet prevSheet = worksheet;
            int prevPosition = prevSheet.Position;
            string prevName = prevSheet.Name;
            while (true)
            {
                string tmpName = DateTime.Now.GetHashCode().ToString();
                if (!workbook.Worksheets.Contains(tmpName))
                {
                    worksheet.Name = tmpName;
                    break;
                }
            }
            worksheet = workbook.AddWorksheet(prevName, prevPosition + 1);
            settings.Row = settings.StartRow;
            addList.Select(al =>
            {
                AddImage(al.ImageStream, al.OriginalHeight);
                return al;
            }).ToArray();
            prevSheet.Delete();
            ManualSave();
        }

        public bool[] DeleteImages(bool[] deleteSettings)
        {
            try
            {
                int count = 0;
                List<ClosedXML.Excel.Drawings.IXLPicture> deleteList = new List<ClosedXML.Excel.Drawings.IXLPicture>();
                deleteSettings.Select(ds =>
                {
                    if (ds)
                    {
                        if (worksheet.Pictures.Count > count)
                        {
                            deleteList.Add(worksheet.Pictures.ElementAt(count));
                        }
                    }
                    return count++;
                }).ToArray();
                bool[] dlResult = deleteList.Select(dl =>
                  {
                      if (worksheet.Pictures.Contains(dl))
                      {
                          dl.Delete();
                          dl.Dispose();
                          return true;
                      }
                      return false;
                  }).ToArray();
                AutoSaveOrModFlagUpdate();
                return dlResult;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return null;
            }
        }

        public void SortWorkSheet(Dictionary<int, string> sortInfo)
        {
            try
            {
                Dictionary<string, IXLWorksheet> wsDic = workbook.Worksheets.ToDictionary(ws => { return ws.Name; }, ws => { return ws; });
                sortInfo.Select(si =>
                {
                    if (wsDic.ContainsKey(si.Value))
                    {
                        wsDic[si.Value].Position = si.Key;
                        return si.Key;
                    }
                    else
                    {
                        return 0;
                    }
                }).ToArray();
                SetWorkSheetsDictionary();
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void CopyWorkSheet(string newName)
        {
            try
            {
                worksheet.CopyTo(newName, worksheet.Position + 1);
                SetWorkSheetsDictionary();
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void AutoSaveOrModFlagUpdate()
        {
            if (AutoSave)
            {
                ManualSave();
            }
            else
            {
                ModFlag = true;
            }
        }

        public void ManualSave()
        {
            workbook.Save();
            ModFlag = false;
        }


        public int AddImageFromFolder(string dirName)
        {                // FileNameで選択されたフォルダを取得する
            int count = 0;
            if (Directory.Exists(dirName))
            {
                IEnumerable<string> files = Directory.EnumerateFiles(dirName).Where(s =>
                    s.EndsWith(".jpg", StringComparison.CurrentCultureIgnoreCase) ||
                    s.EndsWith(".jpeg", StringComparison.CurrentCultureIgnoreCase) ||
                    s.EndsWith(".png", StringComparison.CurrentCultureIgnoreCase) ||
                    s.EndsWith(".bmp", StringComparison.CurrentCultureIgnoreCase) ||
                    s.EndsWith(".gif", StringComparison.CurrentCultureIgnoreCase)
                ).OrderBy(d => d);

                foreach (string str in files)
                {
                    try
                    {
                        Bitmap image = new Bitmap(str);
                        AddImage(image);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.StackTrace);
                    }
                }
            }
            return count;
        }

        public void AddImageFromFile(string fileName)
        {
            try
            {
                Bitmap bitmap = new Bitmap(fileName);
                AddImage(bitmap);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.StackTrace);
            }
        }

        public void UnloadWorkBook()
        {
            workbook.Dispose();
        }
    }
}
