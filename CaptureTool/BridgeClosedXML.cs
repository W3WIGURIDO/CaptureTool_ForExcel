using ClosedXML.Excel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;

namespace CaptureTool
{
    public class BridgeClosedXML
    {
        private Settings settings;
        private XLWorkbook workbook;
        private IXLWorksheet worksheet;
        private const int MaxRow = 1048576;
        // シートオブジェクトの存続期間中だけ利用。名前や表示順はキーにしない。
        private readonly Dictionary<IXLWorksheet, int> bottomRowCache = new Dictionary<IXLWorksheet, int>();
        private readonly Dictionary<IXLWorksheet, int> oneShotRows = new Dictionary<IXLWorksheet, int>();
        public bool ModFlag { get; set; } = false;
        public bool AutoSave { get; set; } = true;
        private bool hasSavedFile;
        public bool HasSavedFile => hasSavedFile;
        public bool HasWorkbook => workbook != null;

        private enum PendingSaveKind { None, Regular, SaveAs }
        private PendingSaveKind pendingSaveKind;
        private string pendingSavePath;
        public bool HasPendingFileLock => pendingSaveKind != PendingSaveKind.None;
        public string PendingSavePath => pendingSavePath ?? settings.FileName;

        // 他の失敗（容量不足、アクセス権等）は共有違反と混同しない。
        public static bool IsSharingViolation(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                var io = current as IOException;
                if (io != null &&
                    (io.HResult == unchecked((int)0x80070020) ||
                     io.HResult == unchecked((int)0x80070021)))
                    return true;
            }
            return false;
        }

        private void SetPendingSave(PendingSaveKind kind, string path)
        {
            pendingSaveKind = kind;
            pendingSavePath = path;
            ModFlag = true;
        }

        private void ClearPendingSave()
        {
            pendingSaveKind = PendingSaveKind.None;
            pendingSavePath = null;
        }

        // MainWindow のブック操作キューからのみ呼ぶ。共有違反なら false、その他は例外。
        public bool RetryPendingSave()
        {
            if (!HasPendingFileLock)
                return true;
            try
            {
                if (pendingSaveKind == PendingSaveKind.SaveAs)
                    workbook.SaveAs(pendingSavePath);
                else if (hasSavedFile)
                    workbook.Save();
                else
                    workbook.SaveAs(pendingSavePath);

                if (pendingSaveKind == PendingSaveKind.SaveAs || !hasSavedFile)
                    settings.FileName = pendingSavePath;
                hasSavedFile = true;
                ModFlag = false;
                ClearPendingSave();
                return true;
            }
            catch (Exception ex) when (IsSharingViolation(ex))
            {
                ModFlag = true;
                return false;
            }
        }

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
                    bottomRowCache.Clear();
                    oneShotRows.Clear();
                    workbook = new XLWorkbook(settings.FileName);
                    worksheet = workbook.Worksheet(1);
                    LoadWorkSheet(worksheet);
                    SetWorkSheetsDictionary();
                    hasSavedFile = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message);
                    throw;
                }
            }
            else
            {
                if (!CreateNewBook())
                    throw new InvalidOperationException("ワークブックの作成に失敗しました。");
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

        // 画像が占める最後の行。ピクセルと各行の実際の高さを比較する。
        private int GetBottomRowForHeight(IXLWorksheet ws, int startRow, double pixelHeight)
        {
            if (startRow < 1 || startRow > MaxRow || pixelHeight <= 0)
                throw new InvalidOperationException("画像の位置または高さが不正です。");
            double covered = 0;
            int row = startRow;
            while (row <= MaxRow && covered + 0.000001 < pixelHeight)
            {
                covered += ws.Row(row).Height / 0.75;
                row++;
            }
            if (covered + 0.000001 < pixelHeight)
                throw new InvalidOperationException("画像がExcelの最大行数を超えます。");
            return row - 1;
        }

        private static object ReadProperty(object target, string name)
        {
            return target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }

        private static IXLCell PictureTopLeftCell(ClosedXML.Excel.Drawings.IXLPicture picture, out double offsetY)
        {
            object marker = ReadProperty(picture, "TopLeftMarker");
            object offset = ReadProperty(marker, "Offset");
            object y = ReadProperty(offset, "Y") ?? ReadProperty(marker, "OffsetY");
            offsetY = y == null ? 0 : Convert.ToDouble(y);
            // ClosedXMLのバージョンによる公開プロパティ名の差を吸収。
            return ReadProperty(picture, "TopLeftCell") as IXLCell ??
                   ReadProperty(marker, "Cell") as IXLCell;
        }

        private int CalculateBottomRow(IXLWorksheet ws)
        {
            int bottom = 0;
            foreach (var picture in ws.Pictures)
            {
                double offsetY;
                IXLCell topLeft = PictureTopLeftCell(picture, out offsetY);
                if (topLeft == null)
                    throw new InvalidOperationException("画像の配置セルを取得できません。画像位置を計算できるClosedXMLのAPIを確認してください。");
                object displayHeight = ReadProperty(picture, "Height");
                double height = displayHeight == null ? picture.OriginalHeight : Convert.ToDouble(displayHeight);
                int imageBottom = GetBottomRowForHeight(ws, topLeft.Address.RowNumber,
                    height + Math.Max(0, offsetY));
                bottom = Math.Max(bottom, imageBottom);
            }
            return bottom;
        }

        private int GetBottomRow(IXLWorksheet ws)
        {
            if (bottomRowCache.TryGetValue(ws, out int cached))
                return cached;
            int bottom = CalculateBottomRow(ws);
            bottomRowCache[ws] = bottom;
            return bottom;
        }

        private int GetComputedNextRow(IXLWorksheet ws)
        {
            int bottom = GetBottomRow(ws);
            long next = bottom == 0 ? settings.StartRow : (long)bottom + 1 + settings.ImageGapRows;
            if (next < 1 || next > MaxRow)
                throw new InvalidOperationException("次回挿入行がExcelの最大行数を超えます。");
            return (int)next;
        }

        // 手入力はシート別に保持し、次の成功した画像挿入で消費する。
        public void SetOneShotRow(int row)
        {
            if (row < 1 || row > MaxRow)
                throw new ArgumentOutOfRangeException(nameof(row));
            oneShotRows[worksheet] = row;
            settings.Row = row;
        }

        public void AddImage(MemoryStream ms, int height)
        {
            int startRow = oneShotRows.TryGetValue(worksheet, out int enteredRow)
                ? enteredRow : settings.Row;
            int imageBottom = GetBottomRowForHeight(worksheet, startRow, height);
            int existingBottom = settings.IsPicturePositionMode ? GetBottomRow(worksheet) : 0;
            long next = settings.IsPicturePositionMode
                ? (long)Math.Max(existingBottom, imageBottom) + 1 + settings.ImageGapRows
                : (long)imageBottom + 1 + settings.ImageGapRows;
            if (next > MaxRow)
                throw new InvalidOperationException("画像または行間隔がExcelの最大行数を超えます。");

            ms.Position = 0;
            var insertedPic = worksheet.AddPicture(ms);
            insertedPic.MoveTo(worksheet.Cell(startRow, 1));
            // 挿入に成功するまで、手入力の一回限り指定は消費しない。
            oneShotRows.Remove(worksheet);
            bottomRowCache[worksheet] = Math.Max(existingBottom, imageBottom);
            settings.Row = (int)next;
            if (!settings.IsPicturePositionMode)
                worksheet.Cell(1, 1).Value = settings.Row;
        }

        public void ApplyStartRowToEmptySheet(int previousStartRow)
        {
            if (worksheet != null && worksheet.Pictures.Count == 0 &&
                !oneShotRows.ContainsKey(worksheet) &&
                (settings.Row == previousStartRow || settings.Row == 1) &&
                settings.Row != settings.StartRow)
            {
                settings.Row = settings.StartRow;
                if (!settings.IsPicturePositionMode)
                {
                    worksheet.Cell(1, 1).Value = settings.Row;
                    AutoSaveOrModFlagUpdate();
                }
                worksheet.Cell(settings.Row, 1).Select();
            }
        }

        // 新方式へ切り替える場合はA1の値を参照しない。旧方式への切替時は
        // 古いA1を信用せず、全シートの実際の画像位置から同期する。
        public bool SwitchRowManagementMode(int mode)
        {
            if (mode == settings.RowManagementModeIndex)
                return true;
            bottomRowCache.Clear();
            if (mode == 1)
            {
                var values = new List<KeyValuePair<IXLWorksheet, int>>();
                var conflicts = new List<string>();
                foreach (var sheet in workbook.Worksheets)
                {
                    int next = GetComputedNextRow(sheet);
                    values.Add(new KeyValuePair<IXLWorksheet, int>(sheet, next));
                    string existing = sheet.Cell(1, 1).GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(existing))
                        conflicts.Add(sheet.Name);
                }
                if (conflicts.Count > 0)
                {
                    string names = string.Join(", ", conflicts.Take(5));
                    var answer = MessageBox.Show(MainWindow.ActiveWindow,
                        "A1セルの既存値を上書きして旧方式へ移行します。対象シート：" + names +
                        (conflicts.Count > 5 ? " ほか" : "") + "\n実行しますか？",
                        "A1セルの上書き確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) return false;
                }
                foreach (var value in values)
                    value.Key.Cell(1, 1).Value = value.Value;
                AutoSaveOrModFlagUpdate();
            }
            settings.RowManagementModeIndex = mode;
            oneShotRows.Clear();
            LoadWorkSheet(worksheet);
            return true;
        }

        public void RefreshCurrentRow()
        {
            bottomRowCache.Clear();
            LoadWorkSheet(worksheet);
        }

        public int CurrentWorksheetPosition => worksheet == null ? 0 : worksheet.Position;

        public void ChangeSelectionWorkSheet(int position)
        {
            var nextSheet = workbook.Worksheet(position);
            if (!settings.EnableRowPositionCache)
                bottomRowCache.Remove(nextSheet);
            worksheet = nextSheet;
            LoadWorkSheet(worksheet);
        }

        private void LoadWorkSheet(IXLWorksheet ws)
        {
            if (settings.IsPicturePositionMode)
                settings.Row = GetComputedNextRow(ws);
            else if (int.TryParse(ws.Cell(1, 1).GetValue<string>(), out int savedRow) &&
                     savedRow >= 1 && savedRow <= MaxRow)
                settings.Row = savedRow;
            else
                settings.Row = settings.StartRow;
            if (oneShotRows.TryGetValue(ws, out int pending))
                settings.Row = pending;
            ws.Cell(settings.Row, 1).Select();
        }

        public bool AddWorkSheet(string name)
        {
            try
            {
                workbook.AddWorksheet(name, workbook.Worksheets.Count + 1);
                ModFlag = true;
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return false;
            }

            try
            {
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("シートは追加されましたが、保存できていません。\n" + ex.Message);
            }
            return true;
        }

        public bool RenameWorkSheet(string name)
        {
            try
            {
                worksheet.Name = name;
                ModFlag = true;
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return false;
            }

            try
            {
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("シート名は変更されましたが、保存できていません。\n" + ex.Message);
            }
            return true;
        }

        public bool SaveAsNowBook()
        {
            string target = settings.FileName;
            try
            {
                workbook.SaveAs(target);
                hasSavedFile = true;
                ModFlag = false;
                ClearPendingSave();
                return true;
            }
            catch (Exception ex) when (IsSharingViolation(ex))
            {
                SetPendingSave(PendingSaveKind.SaveAs, target);
                return false;
            }
            catch (Exception ex)
            {
                ModFlag = true;
                MessageBox.Show(ex.Message);
                return false;
            }
        }

        public bool OpenWorkBook()
        {
            if (!File.Exists(settings.FileName))
                return false;

            XLWorkbook nextWorkbook = null;
            XLWorkbook previousWorkbook = workbook;
            IXLWorksheet previousWorksheet = worksheet;
            bool previousHasSavedFile = hasSavedFile;
            try
            {
                nextWorkbook = new XLWorkbook(settings.FileName);
                workbook = nextWorkbook;
                worksheet = workbook.Worksheet(1);
                bottomRowCache.Clear();
                oneShotRows.Clear();
                LoadWorkSheet(worksheet);
                SetWorkSheetsDictionary();
                previousWorkbook?.Dispose();
                hasSavedFile = true;
                ModFlag = false;
                ClearPendingSave();
                return true;
            }
            catch (Exception ex)
            {
                if (nextWorkbook != null)
                    nextWorkbook.Dispose();
                workbook = previousWorkbook;
                worksheet = previousWorksheet;
                hasSavedFile = previousHasSavedFile;
                bottomRowCache.Clear();
                oneShotRows.Clear();
                if (workbook != null)
                    SetWorkSheetsDictionary();
                MessageBox.Show(ex.Message);
                return false;
            }
        }

        public bool CreateNewBook()
        {
            XLWorkbook nextWorkbook = null;
            IXLWorksheet nextWorksheet;
            Dictionary<int, string> nextSheets;
            try
            {
                // 新規ブック自体は保存先ファイルが使えなくても作成する。
                nextWorkbook = new XLWorkbook();
                nextWorksheet = nextWorkbook.Worksheets.Add("エビデンス");
                nextWorksheet.Cell(settings.StartRow, 1).Select();
                nextSheets = nextWorkbook.Worksheets.OrderBy(ws => ws.Position)
                    .ToDictionary(ws => ws.Position, ws => ws.Name);
            }
            catch (Exception ex)
            {
                nextWorkbook?.Dispose();
                MessageBox.Show(ex.Message);
                return false;
            }

            XLWorkbook previousWorkbook = workbook;
            workbook = nextWorkbook;
            worksheet = nextWorksheet;
            bottomRowCache.Clear();
            oneShotRows.Clear();
            hasSavedFile = false;
            ModFlag = true;
            ClearPendingSave();

            // 新ブック採用後の画面通知で例外が出ても、旧ブックを指し直さない。
            try
            {
                settings.Row = settings.StartRow;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show("新規ブックの行表示を更新できませんでした。\n" + ex.Message);
            }
            try
            {
                settings.WorkSheets = nextSheets;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show("新規ブックのシート一覧表示を更新できませんでした。\n" + ex.Message);
            }

            try
            {
                previousWorkbook?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show("以前のブックの解放に失敗しました: " + ex.Message);
            }

            try
            {
                ManualSave(); // 初回は SaveAs。失敗しても新規ブックを維持する。
            }
            catch (Exception ex)
            {
                MessageBox.Show("新規ブックは作成されましたが、ファイルへ保存できませんでした。\n" +
                                "未保存のまま保持しています。別の保存先への保存も可能です。\n" + ex.Message);
            }
            return true;
        }

        public MemoryStream[] GetImageList()
        {
            return worksheet.Pictures.Select(ixlpic =>
            {
                MemoryStream ms = ixlpic.ImageStream;
                return ms;
            }).ToArray();
        }

        public bool DeleteWorkSheet()
        {
            try
            {
                if (workbook.Worksheets.Count < 2)
                    throw new InvalidOperationException("ワークシートは１つ以上必要です。");

                // 削除後も worksheet が破棄済みシートを指さないようにする。
                // UI の選択規則に合わせ、先頭を削除したら次、その他は前を選択する。
                IXLWorksheet deletingSheet = worksheet;
                int deletingPosition = deletingSheet.Position;
                int nextPosition = deletingPosition == 1 ? 2 : deletingPosition - 1;
                IXLWorksheet nextSheet = workbook.Worksheet(nextPosition);

                bottomRowCache.Remove(deletingSheet);
                oneShotRows.Remove(deletingSheet);
                workbook.Worksheets.Delete(deletingPosition);
                worksheet = nextSheet;
                ModFlag = true;
                LoadWorkSheet(worksheet);
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return false;
            }

            try
            {
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("シートは削除されましたが、保存できていません。\n" + ex.Message);
            }
            return true;
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
            bottomRowCache.Remove(prevSheet);
            oneShotRows.Remove(prevSheet);
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
                bottomRowCache.Remove(worksheet);
                if (settings.IsPicturePositionMode)
                    LoadWorkSheet(worksheet);
                AutoSaveOrModFlagUpdate();
                return dlResult;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return null;
            }
        }

        public bool SortWorkSheet(Dictionary<int, string> sortInfo)
        {
            bool changed = false;
            try
            {
                var sheetsByName = workbook.Worksheets.ToDictionary(ws => ws.Name, ws => ws);
                int count = sheetsByName.Count;
                if (sortInfo == null || sortInfo.Count != count ||
                    sortInfo.Values.Distinct().Count() != count ||
                    sortInfo.Keys.Any(position => position < 1 || position > count) ||
                    sortInfo.Values.Any(name => !sheetsByName.ContainsKey(name)))
                    throw new InvalidOperationException("ワークシートの並び替え情報が不正です。");

                foreach (var entry in sortInfo)
                {
                    IXLWorksheet sheet = sheetsByName[entry.Value];
                    if (sheet.Position != entry.Key)
                    {
                        // 並び替え中の例外でも変更が未保存として残るようにする。
                        changed = true;
                        ModFlag = true;
                        sheet.Position = entry.Key;
                    }
                }
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                if (changed)
                {
                    ModFlag = true;
                    try { SetWorkSheetsDictionary(); }
                    catch (Exception syncError) { Debug.WriteLine(syncError); }
                }
                MessageBox.Show(ex.Message);
                return false;
            }

            try
            {
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("並び替えは適用されましたが、保存できていません。\n" + ex.Message);
            }
            return true;
        }

        public bool CopyWorkSheet(string newName)
        {
            try
            {
                worksheet.CopyTo(newName, worksheet.Position + 1);
                ModFlag = true;
                SetWorkSheetsDictionary();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                return false;
            }

            try
            {
                AutoSaveOrModFlagUpdate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("コピーは適用されましたが、保存できていません。\n" + ex.Message);
            }
            return true;
        }

        private void AutoSaveOrModFlagUpdate()
        {
            if (AutoSave && !HasPendingFileLock)
                ManualSave();
            else
                ModFlag = true;
        }

        public void ManualSave()
        {
            ModFlag = true;
            if (HasPendingFileLock)
            {
                RetryPendingSave();
                return;
            }

            string target = settings.FileName;
            try
            {
                if (hasSavedFile)
                    workbook.Save();
                else
                    workbook.SaveAs(target);
                hasSavedFile = true;
                ModFlag = false;
                ClearPendingSave();
            }
            catch (Exception ex) when (IsSharingViolation(ex))
            {
                SetPendingSave(PendingSaveKind.Regular, target);
            }
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
            workbook?.Dispose();
            workbook = null;
            worksheet = null;
            hasSavedFile = false;
            ModFlag = false;
            ClearPendingSave();
            bottomRowCache.Clear();
            oneShotRows.Clear();
        }
    }
}
