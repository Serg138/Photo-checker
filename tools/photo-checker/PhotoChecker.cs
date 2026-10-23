using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhotoChecker
{
    [DataContract]
    public sealed class PhotoRecord
    {
        [DataMember] public string Name;
        [DataMember] public long Length;
        [DataMember] public long Modified;
        [DataMember] public long Created;
        [DataMember] public int Width;
        [DataMember] public int Height;
        [DataMember] public string Pixels;
        [DataMember] public string PHash;
        [DataMember] public string DHash;
    }

    [DataContract]
    public sealed class PairRecord
    {
        [DataMember] public string Key;
        [DataMember] public double Ssim;
        [DataMember] public double Difference;
    }

    [DataContract]
    public sealed class Cache
    {
        [DataMember] public int Version = 1;
        [DataMember] public string Folder;
        [DataMember] public List<PhotoRecord> Files = new List<PhotoRecord>();
        [DataMember] public List<PairRecord> Pairs = new List<PairRecord>();
    }

    public sealed class Match
    {
        public string File;
        public string Other;
        public bool Exact;
        public double Ssim;
        public string State;
    }

    public sealed class ScanProgress
    {
        public int Done;
        public int Total;
        public string Text;
    }

    public sealed class ScanResult
    {
        public string Folder;
        public bool Similar;
        public int Total;
        public int NewCount;
        public int ChangedCount;
        public int Reused;
        public int Analyzed;
        public int PairChecks;
        public int PairReused;
        public bool FirstRun;
        public string ReportPath;
        public readonly List<Match> Matches = new List<Match>();
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
    }

    public static class ImageTools
    {
        private static readonly double[,] Cosines = MakeCosines();

        private static double[,] MakeCosines()
        {
            double[,] result = new double[8, 32];
            for (int u = 0; u < 8; u++)
                for (int x = 0; x < 32; x++)
                    result[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 64.0);
            return result;
        }

        public static RotateFlipType ExifTransform(int value)
        {
            switch (value)
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.Rotate180FlipX;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        public static Bitmap Load(string path)
        {
            // The file is opened read-only and disposed immediately after decoding.
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Image source = Image.FromStream(stream, false, true))
            {
                if (source.PropertyIdList.Contains(0x112))
                {
                    byte[] orientation = source.GetPropertyItem(0x112).Value;
                    if (orientation.Length >= 2)
                        source.RotateFlip(ExifTransform(BitConverter.ToUInt16(orientation, 0)));
                }
                Bitmap decoded = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                try
                {
                    using (Graphics graphics = Graphics.FromImage(decoded))
                    {
                        graphics.Clear(Color.White);
                        graphics.DrawImageUnscaled(source, 0, 0);
                    }
                    return decoded;
                }
                catch { decoded.Dispose(); throw; }
            }
        }

        public static string Digest(Bitmap image)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] header = Encoding.ASCII.GetBytes("PhotoChecker-RGB24-v1|" +
                    image.Width.ToString(CultureInfo.InvariantCulture) + "|" +
                    image.Height.ToString(CultureInfo.InvariantCulture) + "\n");
                sha.TransformBlock(header, 0, header.Length, header, 0);
                BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    // Hash only actual pixels, excluding row padding and all file metadata.
                    byte[] row = new byte[image.Width * 3];
                    for (int y = 0; y < image.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        sha.TransformBlock(row, 0, row.Length, row, 0);
                    }
                }
                finally { image.UnlockBits(data); }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return Hex(sha.Hash);
            }
        }

        public static string Hex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        }

        public static Bitmap Resize(Bitmap image, int width, int height)
        {
            Bitmap small = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(small))
            using (ImageAttributes attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0,
                    image.Width, image.Height, GraphicsUnit.Pixel, attributes);
            }
            return small;
        }

        private static double Luma(Color color)
        {
            return 0.299 * color.R + 0.587 * color.G + 0.114 * color.B;
        }

        public static void Perceptual(Bitmap image, PhotoRecord record)
        {
            using (Bitmap small = Resize(image, 32, 32))
            {
                double[,] values = new double[32, 32];
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 32; x++) values[y, x] = Luma(small.GetPixel(x, y));
                List<double> coefficients = new List<double>();
                for (int v = 0; v < 8; v++)
                    for (int u = 0; u < 8; u++)
                    {
                        if (u == 0 && v == 0) continue;
                        double sum = 0;
                        for (int y = 0; y < 32; y++)
                            for (int x = 0; x < 32; x++)
                                sum += values[y, x] * Cosines[u, x] * Cosines[v, y];
                        sum *= (u == 0 ? 1 / Math.Sqrt(2) : 1) * (v == 0 ? 1 / Math.Sqrt(2) : 1);
                        coefficients.Add(sum);
                    }
                double median = coefficients.OrderBy(value => value).ElementAt(31);
                ulong bits = 0;
                for (int i = 0; i < coefficients.Count; i++)
                    if (coefficients[i] > median) bits |= 1UL << i;
                record.PHash = bits.ToString("x16", CultureInfo.InvariantCulture);
            }
            using (Bitmap small = Resize(image, 9, 8))
            {
                ulong bits = 0;
                int bit = 0;
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++, bit++)
                        if (Luma(small.GetPixel(x, y)) > Luma(small.GetPixel(x + 1, y))) bits |= 1UL << bit;
                record.DHash = bits.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        public static int Distance(ulong first, ulong second)
        {
            ulong value = first ^ second;
            int count = 0;
            while (value != 0) { value &= value - 1; count++; }
            return count;
        }

        public static byte[] ComparisonPixels(string path)
        {
            using (Bitmap original = Load(path))
            using (Bitmap small = Resize(original, 128, 128))
            {
                byte[] pixels = new byte[128 * 128 * 3];
                BitmapData data = small.LockBits(new Rectangle(0, 0, 128, 128), ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);
                try
                {
                    for (int y = 0; y < 128; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * 128 * 3, 128 * 3);
                }
                finally { small.UnlockBits(data); }
                return pixels;
            }
        }

        public static PairRecord Compare(byte[] first, byte[] second, string key)
        {
            double absolute = 0;
            double ssim = 0;
            for (int i = 0; i < first.Length; i++) absolute += Math.Abs(first[i] - second[i]);
            const double c1 = 6.5025;
            const double c2 = 58.5225;
            for (int by = 0; by < 128; by += 8)
                for (int bx = 0; bx < 128; bx += 8)
                {
                    double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                    for (int y = by; y < by + 8; y++)
                        for (int x = bx; x < bx + 8; x++)
                        {
                            int i = (y * 128 + x) * 3;
                            double a = first[i] * 0.114 + first[i + 1] * 0.587 + first[i + 2] * 0.299;
                            double b = second[i] * 0.114 + second[i + 1] * 0.587 + second[i + 2] * 0.299;
                            sx += a; sy += b; sxx += a * a; syy += b * b; sxy += a * b;
                        }
                    double mx = sx / 64, my = sy / 64;
                    double vx = Math.Max(0, sxx / 64 - mx * mx);
                    double vy = Math.Max(0, syy / 64 - my * my);
                    double covariance = sxy / 64 - mx * my;
                    ssim += ((2 * mx * my + c1) * (2 * covariance + c2)) /
                        ((mx * mx + my * my + c1) * (vx + vy + c2));
                }
            return new PairRecord { Key = key, Ssim = ssim / 256,
                Difference = absolute / (first.Length * 255.0) };
        }
    }

    // A BK-tree avoids comparing the perceptual hash of each photo with every other photo.
    public sealed class HashTree
    {
        private sealed class Node
        {
            public ulong Hash;
            public readonly List<PhotoRecord> Values = new List<PhotoRecord>();
            public readonly Dictionary<int, Node> Children = new Dictionary<int, Node>();
        }
        private Node root;

        public void Add(PhotoRecord record)
        {
            ulong hash = Convert.ToUInt64(record.PHash, 16);
            if (root == null) { root = new Node { Hash = hash }; root.Values.Add(record); return; }
            Node node = root;
            while (true)
            {
                int distance = ImageTools.Distance(hash, node.Hash);
                if (distance == 0) { node.Values.Add(record); return; }
                Node next;
                if (!node.Children.TryGetValue(distance, out next))
                {
                    next = new Node { Hash = hash };
                    next.Values.Add(record);
                    node.Children.Add(distance, next);
                    return;
                }
                node = next;
            }
        }

        public IEnumerable<PhotoRecord> Find(ulong hash, int radius)
        {
            if (root == null) yield break;
            Stack<Node> pending = new Stack<Node>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                Node node = pending.Pop();
                int distance = ImageTools.Distance(hash, node.Hash);
                if (distance <= radius)
                    foreach (PhotoRecord record in node.Values) yield return record;
                foreach (KeyValuePair<int, Node> child in node.Children)
                    if (child.Key >= distance - radius && child.Key <= distance + radius) pending.Push(child.Value);
            }
        }
    }

    public static class Scanner
    {
        private static readonly HashSet<string> Supported = new HashSet<string>(
            new[] { ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff" },
            StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Unsupported = new HashSet<string>(
            new[] { ".heic", ".heif", ".webp", ".avif" }, StringComparer.OrdinalIgnoreCase);

        public static string FolderId(string folder)
        {
            using (SHA256 sha = SHA256.Create())
                return ImageTools.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).Substring(0, 24);
        }

        private static Cache ReadCache(string path, string folder, List<string> warnings)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                {
                    Cache cache = (Cache)new DataContractJsonSerializer(typeof(Cache)).ReadObject(stream);
                    if (cache.Version != 1 || !String.Equals(cache.Folder, folder, StringComparison.OrdinalIgnoreCase))
                        return null;
                    if (cache.Files == null) cache.Files = new List<PhotoRecord>();
                    if (cache.Pairs == null) cache.Pairs = new List<PairRecord>();
                    return cache;
                }
            }
            catch (Exception exception)
            {
                warnings.Add("База отпечатков не прочитана; фотографии обработаны заново: " + exception.Message);
                return null;
            }
        }

        private static void SaveCache(string path, Cache cache)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                new DataContractJsonSerializer(typeof(Cache)).WriteObject(stream, cache);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static string StateFor(string name, HashSet<string> fresh, HashSet<string> changed)
        {
            if (fresh.Contains(name)) return "новый";
            if (changed.Contains(name)) return "изменён";
            return "из базы";
        }

        private static void Report(IProgress<ScanProgress> progress, int done, int total, string text)
        {
            if (progress != null) progress.Report(new ScanProgress { Done = done, Total = total, Text = text });
        }

        public static ScanResult Run(string folder, string dataRoot, bool similar,
            IProgress<ScanProgress> progress, CancellationToken cancellation)
        {
            folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (folder.Length == 2 && folder[1] == ':') folder += Path.DirectorySeparatorChar;
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Папка не существует: " + folder);
            ScanResult result = new ScanResult { Folder = folder, Similar = similar };
            string stateFolder = Path.Combine(dataRoot, FolderId(folder));
            string cachePath = Path.Combine(stateFolder, "cache.json");
            Cache old = ReadCache(cachePath, folder, result.Warnings);
            result.FirstRun = old == null;
            Dictionary<string, PhotoRecord> previous = new Dictionary<string, PhotoRecord>(StringComparer.OrdinalIgnoreCase);
            if (old != null)
                foreach (PhotoRecord record in old.Files)
                    if (!String.IsNullOrEmpty(record.Name)) previous[record.Name] = record;
            FileInfo[] files = new DirectoryInfo(folder).GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => Supported.Contains(file.Extension)).OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            result.Total = files.Length;
            foreach (FileInfo unsupported in new DirectoryInfo(folder).GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => Unsupported.Contains(file.Extension)))
                result.Errors.Add(unsupported.Name + ": формат " + unsupported.Extension + " пока не поддерживается.");
            Cache next = new Cache { Folder = folder };
            HashSet<string> fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Stopwatch progressWatch = Stopwatch.StartNew();
            for (int i = 0; i < files.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                FileInfo file = files[i];
                PhotoRecord previousRecord;
                bool exists = previous.TryGetValue(file.Name, out previousRecord);
                bool unchanged = exists && previousRecord.Length == file.Length && previousRecord.Modified == file.LastWriteTimeUtc.Ticks
                    && previousRecord.Created == file.CreationTimeUtc.Ticks && !String.IsNullOrEmpty(previousRecord.Pixels);
                if (!exists) { fresh.Add(file.Name); result.NewCount++; }
                else if (!unchanged) { changed.Add(file.Name); result.ChangedCount++; }
                try
                {
                    PhotoRecord record;
                    if (unchanged && (!similar || !String.IsNullOrEmpty(previousRecord.PHash)))
                    {
                        record = previousRecord;
                        result.Reused++;
                    }
                    else
                    {
                        record = new PhotoRecord { Name = file.Name, Length = file.Length,
                            Modified = file.LastWriteTimeUtc.Ticks, Created = file.CreationTimeUtc.Ticks };
                        using (Bitmap image = ImageTools.Load(file.FullName))
                        {
                            record.Width = image.Width;
                            record.Height = image.Height;
                            record.Pixels = ImageTools.Digest(image);
                            // In exact mode no resize / thumbnail operation is called at all.
                            if (similar) ImageTools.Perceptual(image, record);
                        }
                        file.Refresh();
                        if (!file.Exists || file.Length != record.Length || file.LastWriteTimeUtc.Ticks != record.Modified
                            || file.CreationTimeUtc.Ticks != record.Created)
                            throw new IOException("Файл изменился во время чтения. Повторите проверку после завершения копирования.");
                        result.Analyzed++;
                    }
                    next.Files.Add(record);
                }
                catch (Exception exception)
                {
                    if (exception is OperationCanceledException) throw;
                    result.Errors.Add(file.Name + ": " + exception.Message);
                }
                if (progressWatch.ElapsedMilliseconds >= 100 || i == files.Length - 1)
                {
                    Report(progress, i + 1, files.Length, "Файлы: " + (i + 1) + " / " + files.Length + "   •   " + file.Name);
                    progressWatch.Restart();
                }
            }
            cancellation.ThrowIfCancellationRequested();
            List<PhotoRecord> representatives = new List<PhotoRecord>();
            foreach (IGrouping<string, PhotoRecord> group in next.Files.GroupBy(record => record.Pixels))
            {
                PhotoRecord[] members = group.OrderBy(record => fresh.Contains(record.Name) || changed.Contains(record.Name) ? 1 : 0)
                    .ThenBy(record => record.Name, StringComparer.OrdinalIgnoreCase).ToArray();
                PhotoRecord representative = members[0];
                representatives.Add(representative);
                for (int i = 1; i < members.Length; i++)
                    result.Matches.Add(new Match { File = Path.Combine(folder, members[i].Name),
                        Other = Path.Combine(folder, representative.Name), Exact = true, Ssim = 1,
                        State = StateFor(members[i].Name, fresh, changed) + " / " + StateFor(representative.Name, fresh, changed) });
            }

            if (similar)
            {
                Dictionary<string, PairRecord> cachedPairs = new Dictionary<string, PairRecord>();
                if (old != null)
                    foreach (PairRecord pair in old.Pairs) if (!String.IsNullOrEmpty(pair.Key)) cachedPairs[pair.Key] = pair;
                HashTree tree = new HashTree();
                Dictionary<string, byte[]> thumbnails = new Dictionary<string, byte[]>();
                for (int i = 0; i < representatives.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    PhotoRecord record = representatives[i];
                    foreach (PhotoRecord candidate in tree.Find(Convert.ToUInt64(record.PHash, 16), 10))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        double aspect = (double)record.Width / record.Height;
                        double otherAspect = (double)candidate.Width / candidate.Height;
                        if (Math.Abs(aspect / otherAspect - 1) > 0.01) continue;
                        if (ImageTools.Distance(Convert.ToUInt64(record.DHash, 16), Convert.ToUInt64(candidate.DHash, 16)) > 14) continue;
                        string key = String.CompareOrdinal(record.Pixels, candidate.Pixels) < 0
                            ? record.Pixels + ":" + candidate.Pixels : candidate.Pixels + ":" + record.Pixels;
                        PairRecord pair;
                        if (cachedPairs.TryGetValue(key, out pair)) result.PairReused++;
                        else
                        {
                            try
                            {
                                byte[] first, second;
                                if (thumbnails.Count >= 64) thumbnails.Clear();
                                if (!thumbnails.TryGetValue(record.Name, out first))
                                {
                                    first = ImageTools.ComparisonPixels(Path.Combine(folder, record.Name));
                                    thumbnails[record.Name] = first;
                                }
                                if (!thumbnails.TryGetValue(candidate.Name, out second))
                                {
                                    second = ImageTools.ComparisonPixels(Path.Combine(folder, candidate.Name));
                                    thumbnails[candidate.Name] = second;
                                }
                                pair = ImageTools.Compare(first, second, key);
                                result.PairChecks++;
                            }
                            catch (Exception exception)
                            {
                                result.Errors.Add(record.Name + " / " + candidate.Name + ": " + exception.Message);
                                continue;
                            }
                        }
                        next.Pairs.Add(pair);
                        if (pair.Ssim >= 0.94 && pair.Difference <= 0.055)
                            result.Matches.Add(new Match { File = Path.Combine(folder, record.Name), Other = Path.Combine(folder, candidate.Name),
                                Exact = false, Ssim = pair.Ssim,
                                State = StateFor(record.Name, fresh, changed) + " / " + StateFor(candidate.Name, fresh, changed) });
                    }
                    tree.Add(record);
                    if (progressWatch.ElapsedMilliseconds >= 100 || i == representatives.Count - 1)
                    {
                        Report(progress, i + 1, representatives.Count, "Поиск похожих: " + (i + 1) + " / " + representatives.Count);
                        progressWatch.Restart();
                    }
                }
            }
            else if (old != null)
            {
                HashSet<string> validPixels = new HashSet<string>(next.Files.Select(record => record.Pixels));
                next.Pairs = old.Pairs.Where(pair => pair.Key != null && pair.Key.Split(':').Length == 2 &&
                    validPixels.Contains(pair.Key.Split(':')[0]) && validPixels.Contains(pair.Key.Split(':')[1])).ToList();
            }

            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stateFolder);
            SaveCache(cachePath, next);
            result.Matches.Sort((a, b) => {
                int kind = b.Exact.CompareTo(a.Exact);
                return kind != 0 ? kind : StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File);
            });
            result.ReportPath = Path.Combine(stateFolder, "Последняя проверка.txt");
            File.WriteAllText(result.ReportPath, MakeReport(result), new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(stateFolder, "Совпадения.csv"), MakeCsv(result), new UTF8Encoding(true));
            return result;
        }

        public static string Summary(ScanResult result)
        {
            return "Фотографий: " + result.Total + ". Новых: " + result.NewCount + ". Изменённых: " + result.ChangedCount +
                ". Из базы: " + result.Reused + ". Совпадений: " + result.Matches.Count + ". Ошибок: " + result.Errors.Count + ".";
        }

        public static string MakeReport(ScanResult result)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("ПРОВЕРКА ФОТОГРАФИЙ");
            text.AppendLine(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            text.AppendLine("Папка: " + result.Folder);
            text.AppendLine("Режим: " + (result.Similar ? "точные и похожие изображения" : "точное совпадение пикселей, без миниатюр"));
            text.AppendLine(Summary(result));
            text.AppendLine("Подробных сравнений похожих пар: " + result.PairChecks + "; взято из базы: " + result.PairReused + ".");
            text.AppendLine();
            if (result.Matches.Count == 0) text.AppendLine(result.Errors.Count == 0 ? "Совпадений не найдено." : "Среди успешно прочитанных фото совпадений не найдено. Проверка неполная: см. ошибки ниже.");
            foreach (Match match in result.Matches)
            {
                text.AppendLine(match.Exact ? "СОВПАДАЮТ ПИКСЕЛИ" : "ПОХОЖИЕ — НУЖНА ВИЗУАЛЬНАЯ ПРОВЕРКА (SSIM " + match.Ssim.ToString("F4", CultureInfo.InvariantCulture) + ")");
                text.AppendLine("  " + match.File);
                text.AppendLine("  " + match.Other);
                text.AppendLine("  " + match.State);
                text.AppendLine();
            }
            if (result.Errors.Count > 0)
            {
                text.AppendLine("ОШИБКИ И ПРОПУЩЕННЫЕ ФАЙЛЫ:");
                foreach (string error in result.Errors) text.AppendLine("  " + error);
            }
            if (result.Warnings.Count > 0)
            {
                text.AppendLine("ПРИМЕЧАНИЯ:");
                foreach (string warning in result.Warnings) text.AppendLine("  " + warning);
            }
            text.AppendLine();
            text.AppendLine("Фото не изменялись. Подпапки не проверялись. В группе точных копий каждый файл сопоставлен с одним представителем.");
            text.AppendLine("Результат включает сохранённые совпадения, а не только новые. Миниатюры на диск не записываются.");
            return text.ToString();
        }

        private static string CsvField(string value) { return "\"" + value.Replace("\"", "\"\"") + "\""; }
        private static string MakeCsv(ScanResult result)
        {
            StringBuilder text = new StringBuilder("Тип;Файл;Совпадает с;Статус;SSIM\r\n");
            foreach (Match match in result.Matches)
                text.AppendLine(CsvField(match.Exact ? "Совпадают пиксели" : "Похожи — проверить") + ";" + CsvField(match.File) + ";" +
                    CsvField(match.Other) + ";" + CsvField(match.State) + ";" + match.Ssim.ToString("F4", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    public sealed class MainWindow : Form
    {
        private readonly TextBox folder = new TextBox();
        private readonly CheckBox similar = new CheckBox();
        private readonly Label mode = new Label();
        private readonly Label status = new Label();
        private readonly Button browse = new Button();
        private readonly Button run = new Button();
        private readonly Button cancel = new Button();
        private readonly Button report = new Button();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly DataGridView grid = new DataGridView();
        private readonly string dataRoot;
        private readonly string settingsPath;
        private CancellationTokenSource cancellation;
        private ScanResult lastResult;

        public MainWindow(string home)
        {
            dataRoot = Path.Combine(home, "PhotoCheckerData");
            settingsPath = Path.Combine(dataRoot, "last-folder.txt");
            Text = "Проверка фотографий";
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Color.FromArgb(29, 40, 56);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1160, 740);
            MinimumSize = new Size(940, 650);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;

            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 11 };
            for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            layout.Controls.Add(new Label { Text = "Проверка фотографий", Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 20F, FontStyle.Bold), AutoSize = true, Margin = new Padding(3, 0, 3, 8) }, 0, 0);
            layout.Controls.Add(new Label { Text = "Добавьте фото в Photos и нажмите «Проверить». Все найденные совпадения появятся ниже.",
                Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 0, 3, 12), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);

            TableLayoutPanel folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            folderRow.Controls.Add(new Label { Text = "Папка", Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 0, 12, 0), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            folder.Dock = DockStyle.Fill;
            folder.Margin = new Padding(0, 5, 10, 0);
            folder.Text = Path.Combine(home, "Photos");
            try { if (File.Exists(settingsPath)) { string saved = File.ReadAllText(settingsPath, Encoding.UTF8).Trim(); if (Directory.Exists(saved)) folder.Text = saved; } }
            catch { /* A settings file is optional. */ }
            folderRow.Controls.Add(folder, 1, 0);
            browse.Text = "Выбрать…";
            browse.AutoSize = true;
            browse.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            browse.Padding = new Padding(10, 3, 10, 3);
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(0, 2, 0, 3);
            browse.Click += delegate {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "Выберите папку с фотографиями", ShowNewFolderButton = false })
                {
                    if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
                }
            };
            folderRow.Controls.Add(browse, 2, 0);
            layout.Controls.Add(folderRow, 0, 2);
            similar.Text = "Искать также похожие фото с другим сжатием или разрешением";
            similar.Dock = DockStyle.Fill;
            similar.AutoSize = true;
            similar.Margin = new Padding(3, 3, 3, 8);
            similar.CheckedChanged += delegate { UpdateMode(); };
            layout.Controls.Add(similar, 0, 3);
            mode.Dock = DockStyle.Fill;
            mode.AutoSize = true;
            mode.Margin = new Padding(3, 0, 3, 14);
            mode.ForeColor = Color.FromArgb(80, 95, 113);
            layout.Controls.Add(mode, 0, 4);
            UpdateMode();

            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 10), WrapContents = false };
            run.Text = "Проверить";
            run.Size = new Size(145, 36);
            run.FlatStyle = FlatStyle.Flat;
            run.BackColor = Color.FromArgb(34, 94, 180);
            run.ForeColor = Color.White;
            run.Click += async delegate { await StartScan(); };
            cancel.Text = "Отменить";
            cancel.Size = new Size(125, 36);
            cancel.Enabled = false;
            cancel.Click += delegate { if (cancellation != null) { cancellation.Cancel(); cancel.Enabled = false; status.Text = "Отмена проверки…"; } };
            report.Text = "Открыть отчёт";
            report.Size = new Size(160, 36);
            report.Enabled = false;
            report.Click += delegate { if (lastResult != null) OpenPath(lastResult.ReportPath); };
            foreach (Button button in new[] { run, cancel, report })
            {
                button.AutoSize = true;
                button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                button.Padding = new Padding(14, 5, 14, 5);
            }
            actions.Controls.AddRange(new Control[] { run, cancel, report });
            layout.Controls.Add(actions, 0, 5);
            progress.Dock = DockStyle.Fill;
            progress.Height = 10;
            progress.Margin = new Padding(0, 3, 0, 6);
            layout.Controls.Add(progress, 0, 6);
            status.Text = "Готово к запуску. При первой проверке будут обработаны все фотографии.";
            status.Dock = DockStyle.Fill;
            status.AutoSize = true;
            status.Margin = new Padding(3, 8, 3, 12);
            status.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(status, 0, 7);

            grid.Dock = DockStyle.Fill;
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.RowHeadersVisible = false;
            grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.RowTemplate.Height = Font.Height + 14;
            grid.ColumnHeadersHeight = Font.Height + 16;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 234, 253);
            grid.DefaultCellStyle.SelectionForeColor = ForeColor;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Результат", FillWeight = 24, SortMode = DataGridViewColumnSortMode.Automatic });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Файл", FillWeight = 29 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Совпадает / похож на", FillWeight = 29 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Статус файлов", FillWeight = 18 });
            layout.Controls.Add(grid, 0, 8);

            FlowLayoutPanel fileActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(0, 10, 0, 6) };
            Button first = new Button { Text = "Показать файл", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 4, 12, 4) };
            Button second = new Button { Text = "Показать совпадение", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 4, 12, 4) };
            first.Click += delegate { RevealSelected(false); };
            second.Click += delegate { RevealSelected(true); };
            fileActions.Controls.AddRange(new Control[] { first, second });
            layout.Controls.Add(fileActions, 0, 9);
            layout.Controls.Add(new Label { Text = "Фото не изменяются. Подпапки не проверяются. Миниатюры на диск не сохраняются.",
                Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.FromArgb(90, 103, 120), Font = new Font("Segoe UI", 9F), TextAlign = ContentAlignment.BottomLeft }, 0, 10);
            AcceptButton = run;
            FormClosing += delegate(object sender, FormClosingEventArgs args) {
                if (cancellation != null)
                {
                    cancellation.Cancel();
                    args.Cancel = true;
                    status.Text = "Отменяется проверка. После её остановки окно можно закрыть.";
                }
            };
        }

        private void UpdateMode()
        {
            mode.Text = similar.Checked
                ? "Сначала точные совпадения, затем pHash + сравнение структуры и цвета. Рабочие миниатюры — только в памяти."
                : "Точное сравнение пикселей в полном разрешении. Миниатюры не создаются. Имена и метаданные файлов не учитываются.";
        }

        private async Task StartScan()
        {
            string selected;
            try
            {
                selected = Path.GetFullPath(folder.Text.Trim());
                if (!Directory.Exists(selected)) throw new DirectoryNotFoundException("Папка не найдена: " + selected);
                Directory.CreateDirectory(dataRoot);
                File.WriteAllText(settingsPath, selected, new UTF8Encoding(true));
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Не удалось начать проверку", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            bool findSimilar = similar.Checked;
            cancellation = new CancellationTokenSource();
            run.Enabled = browse.Enabled = folder.Enabled = similar.Enabled = report.Enabled = false;
            cancel.Enabled = true;
            grid.Rows.Clear();
            progress.Value = 0;
            status.ForeColor = ForeColor;
            status.Text = "Подготовка списка файлов…";
            lastResult = null;
            Stopwatch duration = Stopwatch.StartNew();
            try
            {
                Progress<ScanProgress> updates = new Progress<ScanProgress>(value => {
                    progress.Maximum = Math.Max(1, value.Total);
                    progress.Value = Math.Min(progress.Maximum, value.Done);
                    if (cancellation != null && !cancellation.IsCancellationRequested) status.Text = value.Text;
                });
                lastResult = await Task.Run(() => Scanner.Run(selected, dataRoot, findSimilar, updates, cancellation.Token));
                foreach (Match match in lastResult.Matches)
                {
                    int index = grid.Rows.Add(match.Exact ? "Совпадают пиксели" : "Похожи — проверить", Path.GetFileName(match.File), Path.GetFileName(match.Other), match.State);
                    grid.Rows[index].Tag = match;
                    grid.Rows[index].Cells[1].ToolTipText = match.File;
                    grid.Rows[index].Cells[2].ToolTipText = match.Other;
                }
                progress.Maximum = 100;
                progress.Value = 100;
                status.Text = Scanner.Summary(lastResult) + "\n" + (lastResult.Errors.Count > 0
                    ? "Проверка неполная: подробности ошибок в отчёте."
                    : lastResult.Warnings.Count > 0 ? "База восстановлена. Все фото проверены заново; подробности в отчёте."
                    : lastResult.Matches.Count == 0 ? "Совпадений не найдено." : "Выберите строку, чтобы показать файлы в Проводнике.") +
                    "  Время: " + duration.Elapsed.TotalSeconds.ToString("F1") + " с.";
                status.ForeColor = lastResult.Errors.Count > 0 ? Color.FromArgb(160, 85, 0) : ForeColor;
                report.Enabled = true;
            }
            catch (OperationCanceledException) { status.Text = "Проверка отменена. База и фотографии не изменены."; }
            catch (Exception exception) { status.Text = "Проверка не завершена: " + exception.Message; }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                run.Enabled = browse.Enabled = folder.Enabled = similar.Enabled = true;
                cancel.Enabled = false;
            }
        }

        private void RevealSelected(bool other)
        {
            if (grid.CurrentRow == null) return;
            Match match = grid.CurrentRow.Tag as Match;
            if (match == null) return;
            string path = other ? match.Other : match.File;
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Файл больше не найден: " + path);
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
            }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Не удалось показать файл"); }
        }

        private void OpenPath(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception exception) { MessageBox.Show(this, exception.Message, "Не удалось открыть отчёт"); }
        }

        public void PopulatePreview()
        {
            status.Text = "Фотографий: 1250. Новых: 3. Изменённых: 0. Из базы: 1247. Совпадений: 2. Ошибок: 0.";
            grid.Rows.Add("Совпадают пиксели", "IMG-20261023-WA0012.jpg", "IMG-20260914-WA0031.jpg", "новый / из базы");
            grid.Rows.Add("Совпадают пиксели", "IMG-20261023-WA0015.jpg", "IMG-20260802-WA0004.jpg", "новый / из базы");
        }
    }

    public static class SelfTests
    {
        private static void Assert(bool condition, string description, List<string> log)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + description);
            log.Add("PASS: " + description);
        }

        private static Bitmap Fixture(int width, int height)
        {
            Bitmap image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(image))
            using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 0, width, height), Color.CornflowerBlue, Color.Khaki, 32F))
            {
                graphics.FillRectangle(brush, 0, 0, width, height);
                graphics.FillEllipse(Brushes.DarkRed, width / 7, height / 5, width / 3, height / 2);
                graphics.FillRectangle(Brushes.ForestGreen, width / 2, height / 2, width / 3, height / 4);
                graphics.DrawLine(Pens.Black, 0, height - 1, width - 1, 0);
            }
            return image;
        }

        private static void Jpeg(Bitmap image, string path, long quality)
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(value => value.MimeType == "image/jpeg");
            using (EncoderParameters parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                image.Save(path, codec, parameters);
            }
        }

        private static void JpegComment(string source, string target)
        {
            byte[] original = File.ReadAllBytes(source);
            byte[] comment = Encoding.ASCII.GetBytes("Different WhatsApp filename and metadata; identical encoded image.");
            using (FileStream output = File.Create(target))
            {
                output.Write(original, 0, 2);
                output.WriteByte(0xff); output.WriteByte(0xfe);
                output.WriteByte((byte)((comment.Length + 2) >> 8)); output.WriteByte((byte)((comment.Length + 2) & 255));
                output.Write(comment, 0, comment.Length);
                output.Write(original, 2, original.Length - 2);
            }
        }

        public static int Run(string output)
        {
            Directory.CreateDirectory(output);
            List<string> log = new List<string>();
            try
            {
                string fixtures = Path.Combine(output, "fixtures-" + Guid.NewGuid().ToString("N"));
                string photos = Path.Combine(fixtures, "Photos");
                string data = Path.Combine(fixtures, "state");
                Directory.CreateDirectory(photos);
                string first = Path.Combine(photos, "исходное фото.jpg");
                string metadata = Path.Combine(photos, "другое имя.jpg");
                using (Bitmap image = Fixture(640, 480)) Jpeg(image, first, 93);
                JpegComment(first, metadata);
                Assert(!File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(metadata)), "JPEG test files have different bytes", log);
                using (Bitmap a = ImageTools.Load(first))
                using (Bitmap b = ImageTools.Load(metadata))
                    Assert(ImageTools.Digest(a) == ImageTools.Digest(b), "same JPEG pixels match despite metadata differences", log);

                using (Bitmap image = Fixture(321, 239))
                {
                    string png = Path.Combine(fixtures, "same.png");
                    string bmp = Path.Combine(fixtures, "same.bmp");
                    image.Save(png, ImageFormat.Png); image.Save(bmp, ImageFormat.Bmp);
                    using (Bitmap a = ImageTools.Load(png))
                    using (Bitmap b = ImageTools.Load(bmp))
                        Assert(ImageTools.Digest(a) == ImageTools.Digest(b), "different encodings and row padding normalize to same pixels", log);
                    string digest = ImageTools.Digest(image);
                    image.SetPixel(100, 100, Color.Magenta);
                    Assert(digest != ImageTools.Digest(image), "a single changed pixel is not an exact duplicate", log);
                }
                using (Bitmap image = Fixture(300, 200))
                {
                    image.SetPixel(0, 0, Color.Magenta);
                    image.RotateFlip(ImageTools.ExifTransform(6));
                    Assert(image.Width == 200 && image.Height == 300 && image.GetPixel(199, 0).ToArgb() == Color.Magenta.ToArgb(), "EXIF 6 rotates clockwise correctly", log);
                }
                byte[] firstBytes = File.ReadAllBytes(first);
                long firstWrite = File.GetLastWriteTimeUtc(first).Ticks;
                ScanResult initial = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(initial.FirstRun && initial.NewCount == 2 && initial.Analyzed == 2 && initial.Matches.Count == 1, "first run analyzes all files and finds pixel duplicate", log);
                ScanResult repeated = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(repeated.NewCount == 0 && repeated.Reused == 2 && repeated.Analyzed == 0 && repeated.Matches.Count == 1, "repeat run reuses fingerprints and keeps previous matches", log);
                File.Copy(first, Path.Combine(photos, "new-old-timestamp.jpg"));
                File.SetLastWriteTimeUtc(Path.Combine(photos, "new-old-timestamp.jpg"), new DateTime(2001, 1, 1));
                using (Bitmap other = new Bitmap(270, 190)) { using (Graphics graphics = Graphics.FromImage(other)) graphics.Clear(Color.Purple); other.Save(Path.Combine(photos, "different.png")); }
                ScanResult added = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(added.NewCount == 2 && added.Analyzed == 2 && added.Reused == 2 && added.Matches.Count == 2, "all new files are checked regardless of their timestamps", log);
                using (Bitmap other = Fixture(271, 191)) other.Save(Path.Combine(photos, "different.png"));
                ScanResult changed = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(changed.ChangedCount == 1 && changed.Analyzed == 1, "changed existing file is reanalyzed", log);
                File.WriteAllText(Path.Combine(photos, "broken.jpg"), "not an image");
                File.WriteAllText(Path.Combine(photos, "unsupported.heic"), "not supported");
                ScanResult errors = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(errors.Errors.Count == 2 && errors.Matches.Count == 2, "corrupt and unsupported images are reported without hiding good matches", log);
                File.Move(Path.Combine(photos, "broken.jpg"), Path.Combine(fixtures, "broken.jpg.disabled"));
                File.Move(Path.Combine(photos, "unsupported.heic"), Path.Combine(fixtures, "unsupported.heic.disabled"));
                File.Move(metadata, Path.Combine(fixtures, "removed.jpg"));
                ScanResult removed = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(removed.Total == 3 && removed.Matches.Count == 1, "removed files leave the index and results", log);
                string cachePath = Path.Combine(data, Scanner.FolderId(photos), "cache.json");
                byte[] savedCache = File.ReadAllBytes(cachePath);
                bool wasCancelled = false;
                using (CancellationTokenSource source = new CancellationTokenSource())
                {
                    source.Cancel();
                    try { Scanner.Run(photos, data, false, null, source.Token); } catch (OperationCanceledException) { wasCancelled = true; }
                }
                Assert(wasCancelled && savedCache.SequenceEqual(File.ReadAllBytes(cachePath)), "cancellation preserves previous cache", log);
                Assert(firstBytes.SequenceEqual(File.ReadAllBytes(first)) && firstWrite == File.GetLastWriteTimeUtc(first).Ticks, "scanning leaves source bytes and modification time unchanged", log);

                string perceptualFolder = Path.Combine(fixtures, "perceptual");
                Directory.CreateDirectory(perceptualFolder);
                using (Bitmap image = Fixture(800, 600))
                {
                    Jpeg(image, Path.Combine(perceptualFolder, "quality93.jpg"), 93);
                    Jpeg(image, Path.Combine(perceptualFolder, "quality70.jpg"), 70);
                    using (Bitmap small = ImageTools.Resize(image, 400, 300)) Jpeg(small, Path.Combine(perceptualFolder, "resized.jpg"), 85);
                }
                using (Bitmap different = new Bitmap(800, 600))
                {
                    using (Graphics graphics = Graphics.FromImage(different)) { graphics.Clear(Color.Black); graphics.FillEllipse(Brushes.Yellow, 500, 10, 200, 200); }
                    Jpeg(different, Path.Combine(perceptualFolder, "other-scene.jpg"), 90);
                }
                ScanResult strict = Scanner.Run(perceptualFolder, data, false, null, CancellationToken.None);
                Assert(strict.Matches.Count == 0, "exact mode distinguishes recompression and resizing", log);
                ScanResult fuzzy = Scanner.Run(perceptualFolder, data, true, null, CancellationToken.None);
                Assert(fuzzy.Matches.Count >= 2 && fuzzy.Matches.All(match => !match.File.EndsWith("other-scene.jpg") && !match.Other.EndsWith("other-scene.jpg")), "perceptual mode finds recompression / resizing and rejects different scene", log);
                ScanResult fuzzyRepeat = Scanner.Run(perceptualFolder, data, true, null, CancellationToken.None);
                Assert(fuzzyRepeat.Analyzed == 0 && fuzzyRepeat.PairChecks == 0 && fuzzyRepeat.PairReused > 0 && fuzzyRepeat.Matches.Count == fuzzy.Matches.Count, "perceptual repeat reuses file and pair fingerprints", log);
                Assert(!Directory.GetFiles(data, "*.png", SearchOption.AllDirectories).Any() && !Directory.GetFiles(data, "*.jpg", SearchOption.AllDirectories).Any(), "no thumbnails are saved to disk", log);
                File.WriteAllText(cachePath, "invalid cache");
                ScanResult recovered = Scanner.Run(photos, data, false, null, CancellationToken.None);
                Assert(recovered.Analyzed == 3 && recovered.Errors.Count == 0 && recovered.Warnings.Count == 1 && recovered.Matches.Count == 1, "damaged cache is rebuilt with an explicit warning", log);
                HashTree tree = new HashTree();
                Random random = new Random(512);
                List<PhotoRecord> hashes = new List<PhotoRecord>();
                for (int i = 0; i < 200; i++)
                {
                    byte[] bytes = new byte[8]; random.NextBytes(bytes);
                    PhotoRecord record = new PhotoRecord { Name = i.ToString(), PHash = BitConverter.ToUInt64(bytes, 0).ToString("x16") };
                    hashes.Add(record); tree.Add(record);
                }
                bool treeCorrect = true;
                foreach (PhotoRecord record in hashes.Take(20))
                {
                    ulong hash = Convert.ToUInt64(record.PHash, 16);
                    string[] expected = hashes.Where(value => ImageTools.Distance(hash, Convert.ToUInt64(value.PHash, 16)) <= 10).Select(value => value.Name).OrderBy(value => value).ToArray();
                    string[] actual = tree.Find(hash, 10).Select(value => value.Name).OrderBy(value => value).ToArray();
                    treeCorrect &= expected.SequenceEqual(actual);
                }
                Assert(treeCorrect, "BK-tree candidates agree with exhaustive Hamming search", log);
                log.Add("ALL TESTS PASSED");
                File.WriteAllLines(Path.Combine(output, "self-test.txt"), log, new UTF8Encoding(true));
                return 0;
            }
            catch (Exception exception)
            {
                log.Add(exception.ToString());
                File.WriteAllLines(Path.Combine(output, "self-test.txt"), log, new UTF8Encoding(true));
                return 1;
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] arguments)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string home = AppDomain.CurrentDomain.BaseDirectory;
            if (arguments.Length == 2 && arguments[0] == "--self-test") return SelfTests.Run(Path.GetFullPath(arguments[1]));
            if (arguments.Length == 2 && arguments[0] == "--ui-test")
            {
                using (MainWindow form = new MainWindow(home))
                {
                    // Render only this application's own transparent test window, never the desktop.
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    form.PopulatePreview();
                    form.PerformLayout();
                    Application.DoEvents();
                    using (Graphics graphics = form.CreateGraphics())
                        File.WriteAllText(Path.ChangeExtension(arguments[1], ".txt"),
                            "DPI: " + graphics.DpiX + " x " + graphics.DpiY + "; font: " + form.Font.Size +
                            "; size: " + form.Size + "; work area: " + Screen.FromControl(form).WorkingArea);
                    using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                        bitmap.Save(arguments[1], ImageFormat.Png);
                    }
                }
                return 0;
            }
            Application.Run(new MainWindow(home));
            return 0;
        }
    }
}
