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
using System.Text.RegularExpressions;
using System.Windows;

namespace CaptureTool
{
    public class BridgeClosedXML
    {
        private Settings settings;
        private XLWorkbook workbook;
        private IXLWorksheet worksheet;
        private const int MaxRow = 1048576;
        // 年号や長い連番の途中を試験番号と誤認しない。
        private static readonly Regex TestNumberInBookName = new Regex(
            @"(?<![0-9A-Za-z])(?<![0-9]-)[0-9]{1,3}(?:-[0-9]{1,3}){2,}(?![0-9]|-[0-9])",
            RegexOptions.CultureInvariant);

        private static string GetFirstWorksheetName(string fileName)
        {
            string baseName = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
            Match candidate = TestNumberInBookName.Match(baseName ?? string.Empty);
            // 使える番号がない場合は従来の初期名を維持する。
            return candidate.Success && candidate.Value.Length <= 31
                ? candidate.Value : "エビデンス";
        }
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


        public void AddImage(Bitmap bitmap, string description = null)
        {
            MemoryStream ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            AddImage(ms, bitmap.Height, description);
            AutoSaveOrModFlagUpdate();
        }

        // 一覧は画像データと配置情報を同じ順序で取得する。ブック切替後の古い一覧は利用しない。
        public sealed class ImageEntry
        {
            public MemoryStream ImageStream { get; set; }
            public string SheetName { get; set; }
            public string PictureName { get; set; }
            public int Index { get; set; }
            public int StartRow { get; set; }
            public int StartColumn { get; set; }
            public string Description { get; set; }
        }

        private static string GetDescription(IXLWorksheet ws, int startRow, bool legacyMode)
        {
            if (startRow < 2 || (legacyMode && startRow == 2))
                return string.Empty; // 旧方式のA1は行管理専用
            return ws.Cell(startRow - 1, 1).GetValue<string>();
        }

        public ImageEntry[] GetImageEntries()
        {
            return worksheet.Pictures.Select((picture, index) =>
            {
                double offset;
                IXLCell top = PictureTopLeftCell(picture, out offset);
                if (top == null)
                    throw new InvalidOperationException("画像の配置セルを読み取れません。");
                return new ImageEntry
                {
                    ImageStream = picture.ImageStream,
                    SheetName = worksheet.Name,
                    PictureName = ReadProperty(picture, "Name") as string,
                    Index = index,
                    StartRow = top.Address.RowNumber,
                    StartColumn = top.Address.ColumnNumber,
                    Description = top.Address.ColumnNumber == 1
                        ? GetDescription(worksheet, top.Address.RowNumber, !settings.IsPicturePositionMode)
                        : string.Empty
                };
            }).ToArray();
        }

        private void ValidateDescriptionCell(IXLWorksheet ws, int startRow, bool requireEmpty)
        {
            if (startRow < 2)
                throw new InvalidOperationException("説明を付ける画像は2行目以降に配置してください。");
            if (!settings.IsPicturePositionMode && startRow == 2)
                throw new InvalidOperationException("旧方式のA1は行管理に使用中です。画像を3行目以降に配置してください。");

            var cell = ws.Cell(startRow - 1, 1);
            if (cell.IsMerged())
                throw new InvalidOperationException("画像直上のセルが結合されています。説明を配置できません。");
            if (requireEmpty && !cell.IsEmpty())
                throw new InvalidOperationException("画像直上のセルには既に値があります。上書きせず挿入を中止しました。");

            // 通常の末尾追加ならキャッシュされた最終行だけで判定できる。
            if (startRow - 1 > GetBottomRow(ws)) return;
            // 手入力した挿入行にも対応。既存画像が説明行を覆う場合は配置しない。
            foreach (var pic in ws.Pictures)
            {
                double offsetY;
                var top = PictureTopLeftCell(pic, out offsetY);
                if (top == null)
                    throw new InvalidOperationException("既存画像の配置セルを読み取れません。");
                int picRow = top.Address.RowNumber;
                if (picRow == startRow)
                    throw new InvalidOperationException("同じ開始行に画像があります。画像ごとの説明を区別できません。");
                if (picRow > startRow - 1) continue;
                object displayHeight = ReadProperty(pic, "Height");
                double height = displayHeight == null ? pic.OriginalHeight : Convert.ToDouble(displayHeight);
                if (GetBottomRowForHeight(ws, picRow, height + Math.Max(0, offsetY)) >= startRow - 1)
                    throw new InvalidOperationException("説明を置く行に既存画像が重なっています。");
            }
        }

        private static bool HasDescriptionLineBreak(string text)
        {
            return !string.IsNullOrEmpty(text) &&
                   (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0);
        }

        // Excelの列幅はおおむね半角文字換算。日本語は約2文字分として見積もる。
        private static double RequiredDescriptionRowHeight(IXLWorksheet ws, string text)
        {
            int width = Math.Max(10, (int)Math.Floor(ws.Column(1).Width));
            int lines = 0;
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                int units = line.Sum(ch => ch > 0x7F ? 2 : 1);
                lines += Math.Max(1, (units + width - 1) / width);
                if (lines >= 25) return 400; // Excelの行高上限(409.5pt)より小さくする
            }
            return Math.Min(400, 16 * lines);
        }

        private static bool NeedsDescriptionFormatting(IXLWorksheet ws, int startRow, string description)
        {
            bool wrap = HasDescriptionLineBreak(description);
            var cell = ws.Cell(startRow - 1, 1);
            return cell.Style.Alignment.WrapText != wrap ||
                   (wrap && ws.Row(startRow - 1).Height + 0.001 <
                       RequiredDescriptionRowHeight(ws, description));
        }

        // 値を変えずに既存説明の見た目だけ更新する際にも使用する。
        private bool FormatDescriptionCell(IXLWorksheet ws, int startRow, string description)
        {
            if (!NeedsDescriptionFormatting(ws, startRow, description))
                return false;
            bool wrap = HasDescriptionLineBreak(description);
            var cell = ws.Cell(startRow - 1, 1);
            cell.Style.Alignment.WrapText = wrap;
            if (wrap)
            {
                var row = ws.Row(startRow - 1);
                row.Height = Math.Max(row.Height, RequiredDescriptionRowHeight(ws, description));
            }
            // 利用者が手動で設定した行高か判別できないので縮めない。
            bottomRowCache.Remove(ws);
            return true;
        }

        private void WriteDescription(IXLWorksheet ws, int startRow, string description)
        {
            if (description != null && description.Length > 32767)
                throw new ArgumentException("説明は32767文字以内にしてください。", nameof(description));
            var cell = ws.Cell(startRow - 1, 1);
            cell.Value = description ?? string.Empty;
            FormatDescriptionCell(ws, startRow, description);
            bottomRowCache.Remove(ws);
        }

        private int ReformatExistingDescriptionsCore(IXLWorksheet ws)
        {
            int changed = 0;
            // 同じ開始行の画像が複数あっても、説明セルは1回だけ書式変更する。
            var processedRows = new HashSet<int>();
            foreach (var picture in ws.Pictures)
            {
                double offset;
                var top = PictureTopLeftCell(picture, out offset);
                if (top == null)
                    throw new InvalidOperationException("画像の配置セルを読み取れません。");
                int row = top.Address.RowNumber;
                if (top.Address.ColumnNumber != 1 || row < 2 ||
                    (!settings.IsPicturePositionMode && row == 2) ||
                    !processedRows.Add(row) || ws.Cell(row - 1, 1).IsMerged())
                    continue; // 利用者が作成した結合セルには手を触れない
                string value = GetDescription(ws, row, !settings.IsPicturePositionMode);
                if (string.IsNullOrEmpty(value)) continue; // 無関係の空セルは変更しない
                if (!NeedsDescriptionFormatting(ws, row, value)) continue;
                ModFlag = true;
                if (FormatDescriptionCell(ws, row, value)) changed++;
            }
            return changed;
        }

        public int ReformatCurrentSheetDescriptions()
        {
            int changed = ReformatExistingDescriptionsCore(worksheet);
            if (changed > 0)
            {
                if (settings.IsPicturePositionMode)
                    LoadWorkSheet(worksheet);
                AutoSaveOrModFlagUpdate(); // 一覧中のすべての書式をまとめて1回保存
            }
            return changed;
        }

        // 既存シートのA列幅はUIの明示的な確認後にのみ変更する。
        public bool ApplyDescriptionColumnWidthToCurrentSheet()
        {
            bool changed = Math.Abs(worksheet.Column(1).Width -
                                    settings.DescriptionColumnWidth) > 0.001;
            if (changed)
            {
                ModFlag = true;
                worksheet.Column(1).Width = settings.DescriptionColumnWidth;
                bottomRowCache.Remove(worksheet);
            }
            int formatted = ReformatExistingDescriptionsCore(worksheet);
            if (!changed && formatted == 0) return false;
            if (settings.IsPicturePositionMode)
                LoadWorkSheet(worksheet);
            AutoSaveOrModFlagUpdate();
            return true;
        }

        // 一覧の画像が同じブック・同じ配置に残っている場合にだけ書き換える。
        public string UpdateImageDescription(ImageEntry entry, string description)
        {
            if (entry == null || !workbook.Worksheets.Contains(entry.SheetName))
                throw new InvalidOperationException("対象のシートが変更されました。画像一覧を開き直してください。");
            var ws = workbook.Worksheet(entry.SheetName);
            var pic = ws.Pictures.ElementAtOrDefault(entry.Index);
            double offset;
            var top = pic == null ? null : PictureTopLeftCell(pic, out offset);
            if (top == null || top.Address.RowNumber != entry.StartRow ||
                top.Address.ColumnNumber != entry.StartColumn || entry.StartColumn != 1 ||
                (entry.PictureName != null && entry.PictureName != (ReadProperty(pic, "Name") as string)))
                throw new InvalidOperationException("対象画像が変更されました。画像一覧を開き直してください。");
            ValidateDescriptionCellForEdit(ws, pic, entry.StartRow);
            string before = GetDescription(ws, entry.StartRow, !settings.IsPicturePositionMode);
            string current = description ?? string.Empty;
            if (before == current && !NeedsDescriptionFormatting(ws, entry.StartRow, current))
                return before;
            ModFlag = true;
            WriteDescription(ws, entry.StartRow, description);
            if (ReferenceEquals(worksheet, ws) && settings.IsPicturePositionMode)
                LoadWorkSheet(ws);
            AutoSaveOrModFlagUpdate();
            return description ?? string.Empty;
        }

        // 保存エラー時、セルへの反映だけ成功していないかを確認する。
        public string ReadImageDescription(ImageEntry entry)
        {
            if (entry == null || workbook == null || !workbook.Worksheets.Contains(entry.SheetName))
                throw new InvalidOperationException("対象のシートが変更されました。");
            var ws = workbook.Worksheet(entry.SheetName);
            var pic = ws.Pictures.ElementAtOrDefault(entry.Index);
            double offset;
            var top = pic == null ? null : PictureTopLeftCell(pic, out offset);
            if (top == null || top.Address.RowNumber != entry.StartRow ||
                top.Address.ColumnNumber != entry.StartColumn || entry.StartColumn != 1 ||
                (entry.PictureName != null && entry.PictureName != (ReadProperty(pic, "Name") as string)))
                throw new InvalidOperationException("対象画像が変更されました。");
            return GetDescription(ws, entry.StartRow, !settings.IsPicturePositionMode);
        }

        private void ValidateDescriptionCellForEdit(IXLWorksheet ws,
            ClosedXML.Excel.Drawings.IXLPicture current, int startRow)
        {
            // 自分自身の画像を除いて配置衝突を調べる。
            if (startRow < 2 || (!settings.IsPicturePositionMode && startRow == 2))
                throw new InvalidOperationException("画像直上のセルは説明に使用できません。");
            if (ws.Cell(startRow - 1, 1).IsMerged())
                throw new InvalidOperationException("画像直上のセルが結合されています。");
            foreach (var pic in ws.Pictures)
            {
                if (ReferenceEquals(pic, current)) continue;
                double offset;
                var top = PictureTopLeftCell(pic, out offset);
                if (top == null)
                    throw new InvalidOperationException("画像の配置セルを読み取れません。");
                int row = top.Address.RowNumber;
                if (row == startRow)
                    throw new InvalidOperationException("同じ行の画像が複数あります。");
                if (row >= startRow) continue;
                object h = ReadProperty(pic, "Height");
                if (GetBottomRowForHeight(ws, row,
                    (h == null ? pic.OriginalHeight : Convert.ToDouble(h)) + Math.Max(0, offset)) >= startRow - 1)
                    throw new InvalidOperationException("説明の行に別の画像が重なっています。");
            }
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

        public void AddImage(MemoryStream ms, int height, string description = null, bool restoring = false)
        {
            int startRow = oneShotRows.TryGetValue(worksheet, out int enteredRow)
                ? enteredRow : settings.Row;
            if (description != null && description.Length > 32767)
                throw new ArgumentException("説明は32767文字以内にしてください。", nameof(description));
            bool reserveDescription = !restoring && settings.EnableImageDescriptions ||
                                      !string.IsNullOrEmpty(description);
            if (reserveDescription)
                ValidateDescriptionCell(worksheet, startRow, true);
            int imageBottom = GetBottomRowForHeight(worksheet, startRow, height);
            if (reserveDescription && startRow <= GetBottomRow(worksheet))
            {
                foreach (var pic in worksheet.Pictures)
                {
                    double offset;
                    var top = PictureTopLeftCell(pic, out offset);
                    if (top == null)
                        throw new InvalidOperationException("既存画像の配置セルを読み取れません。");
                    int row = top.Address.RowNumber;
                    if (row > startRow && row - 1 <= imageBottom)
                        throw new InvalidOperationException(
                            "挿入画像が既存画像の説明行に重なります。別の挿入行を指定してください。");
                }
            }
            int existingBottom = settings.IsPicturePositionMode ? GetBottomRow(worksheet) : 0;
            long next = settings.IsPicturePositionMode
                ? (long)Math.Max(existingBottom, imageBottom) + 1 + settings.ImageGapRows
                : (long)imageBottom + 1 + settings.ImageGapRows;
            if (next > MaxRow)
                throw new InvalidOperationException("画像または行間隔がExcelの最大行数を超えます。");

            ms.Position = 0;
            ModFlag = true;
            var insertedPic = worksheet.AddPicture(ms);
            insertedPic.MoveTo(worksheet.Cell(startRow, 1));
            if (!string.IsNullOrEmpty(description))
                WriteDescription(worksheet, startRow, description);
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
                    if (!string.IsNullOrEmpty(existing) && sheet.Pictures.Any(pic =>
                    {
                        double offset;
                        var top = PictureTopLeftCell(pic, out offset);
                        return top != null && top.Address.RowNumber == 2;
                    }))
                        throw new InvalidOperationException(
                            "2行目の画像の直上A1に値があります。旧方式にするとA1が上書きされるため切り替えられません。");
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
        public string CurrentWorksheetName => worksheet?.Name;
        public double CurrentDescriptionColumnWidth => worksheet?.Column(1).Width ?? 0;

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
                var addedSheet = workbook.AddWorksheet(name, workbook.Worksheets.Count + 1);
                ModFlag = true;
                addedSheet.Column(1).Width = settings.DescriptionColumnWidth;
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
                nextWorksheet = nextWorkbook.Worksheets.Add(
                    GetFirstWorksheetName(settings.FileName));
                nextWorksheet.Column(1).Width = settings.DescriptionColumnWidth;
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
            var pictures = worksheet.Pictures.ToArray();
            if (deleteSettings == null || deleteSettings.Length != pictures.Length)
                throw new InvalidOperationException("画像一覧が変更されました。削除する画像を選び直してください。");

            var entries = GetImageEntries();
            if (entries.GroupBy(entry => entry.StartRow)
                .Any(group => group.Count() > 1 &&
                     group.Any(entry => !string.IsNullOrEmpty(entry.Description))))
                throw new InvalidOperationException(
                    "同じ開始行に複数の画像があります。説明を取り違えるおそれがあるため、削除を中止しました。");
            var retained = pictures.Select((pic, i) => new { Picture = pic, Entry = entries[i], Delete = deleteSettings[i] })
                .Where(item => !item.Delete).ToArray();
            IXLWorksheet previous = worksheet;
            int position = previous.Position;
            string name = previous.Name;
            int previousRow = settings.Row;
            bool hadOneShot = oneShotRows.TryGetValue(previous, out int pendingRow);
            IXLWorksheet replacement = null;
            // シート差替え前に説明と画像を対で控える。元の開始行を維持し、
            // 削除後も説明が別の画像に付け替わらないようにする。
            string temporaryName;
            do { temporaryName = Guid.NewGuid().ToString("N").Substring(0, 25); }
            while (workbook.Worksheets.Contains(temporaryName));

            try
            {
                ModFlag = true;
                previous.Name = temporaryName;
                replacement = workbook.AddWorksheet(name, position + 1);
                replacement.Column(1).Width = previous.Column(1).Width;
                // 元ブックで行高が変更されていても、画像の占有行と説明行を保つ。
                foreach (var item in retained)
                {
                    int row = item.Entry.StartRow;
                    double offset;
                    PictureTopLeftCell(item.Picture, out offset);
                    object h = ReadProperty(item.Picture, "Height");
                    double displayHeight = h == null ? item.Picture.OriginalHeight : Convert.ToDouble(h);
                    int bottom = GetBottomRowForHeight(previous, row, displayHeight + Math.Max(0, offset));
                    for (int r = row; r <= bottom; r++)
                        replacement.Row(r).Height = previous.Row(r).Height;
                    if (row > 1 && !string.IsNullOrEmpty(item.Entry.Description))
                        replacement.Row(row - 1).Height = previous.Row(row - 1).Height;
                }
                worksheet = replacement;
                oneShotRows.Remove(previous);
                settings.Row = settings.StartRow;
                foreach (var item in retained)
                {
                    SetOneShotRow(item.Entry.StartRow);
                    // 旧シートの削除後も新画像のストリームを有効に保つ。
                    var imageCopy = new MemoryStream();
                    var source = item.Picture.ImageStream;
                    source.Position = 0;
                    source.CopyTo(imageCopy);
                    imageCopy.Position = 0;
                    AddImage(imageCopy, item.Picture.OriginalHeight,
                        item.Entry.Description, restoring: true);
                    if (item.Entry.StartRow > 1 && !string.IsNullOrEmpty(item.Entry.Description))
                        replacement.Row(item.Entry.StartRow - 1).Height =
                            Math.Max(replacement.Row(item.Entry.StartRow - 1).Height,
                                previous.Row(item.Entry.StartRow - 1).Height);
                }
                if (retained.Length == 0 && !settings.IsPicturePositionMode)
                    replacement.Cell(1, 1).Value = settings.StartRow;
            }
            catch
            {
                // 元のシートは成功が確定するまで削除しない。
                if (replacement != null)
                    replacement.Delete();
                previous.Name = name;
                worksheet = previous;
                settings.Row = previousRow;
                if (hadOneShot) oneShotRows[previous] = pendingRow;
                bottomRowCache.Clear();
                SetWorkSheetsDictionary();
                throw;
            }

            bottomRowCache.Remove(previous);
            oneShotRows.Remove(previous);
            previous.Delete();
            SetWorkSheetsDictionary();
            LoadWorkSheet(worksheet);
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


        public int AddImageFromFolder(string dirName, string firstDescription = null)
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
                        using (Bitmap image = new Bitmap(str))
                            AddImage(image, count == 0 ? firstDescription : null);
                        count++;
                    }
                    catch (InvalidOperationException ex)
                    {
                        MessageBox.Show(ex.Message, "画像の配置に失敗しました");
                        break; // 設定・セルの衝突は次のファイルでも解消しない
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex);
                    }
                }
            }
            return count;
        }

        public bool AddImageFromFile(string fileName, string description = null)
        {
            try
            {
                using (Bitmap bitmap = new Bitmap(fileName))
                    AddImage(bitmap, description);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                MessageBox.Show(ex.Message, "画像の追加に失敗しました");
                return false;
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
