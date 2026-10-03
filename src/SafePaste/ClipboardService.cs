using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using SafePaste.Interop;

namespace SafePaste
{
    /// <summary>Чтение и запись буфера обмена: текст и картинки. На диск ничего не сохраняется.</summary>
    internal static class ClipboardService
    {
        private const int RetryCount = 10;
        private const int RetryDelayMs = 80;
        // Номер изменения буфера после последней записи самого SafePaste: её не нужно принимать за новую копию.
        private static uint ownSequence;

        /// <summary>Последнее изменение буфера сделал сам SafePaste (результат, копия выделенного, очистка).</summary>
        internal static bool IsOwnChange()
        {
            return ownSequence != 0 && Native.GetClipboardSequenceNumber() == ownSequence;
        }

        /// <summary>
        /// Программа, положившая текст, просит его не отслеживать: так делают менеджеры паролей
        /// (формат ExcludeClipboardContentFromMonitorProcessing или CanIncludeInClipboardHistory = 0).
        /// </summary>
        internal static bool IsExcludedFromMonitoring()
        {
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null)
                {
                    return false;
                }
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing"))
                {
                    return true;
                }
                if (!data.GetDataPresent("CanIncludeInClipboardHistory"))
                {
                    return false;
                }
                MemoryStream flag = data.GetData("CanIncludeInClipboardHistory") as MemoryStream;
                byte[] bytes = flag == null ? null : flag.ToArray();
                return bytes != null && bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) == 0;
            }
            catch (Exception)
            {
                // Буфер занят: такую копию проще пропустить, чем показать.
                return true;
            }
        }

        internal static bool TryGetText(out string text, out string error)
        {
            bool empty;
            return TryGetText(out text, out error, out empty);
        }

        /// <summary>empty = true, если буфер просто пуст, а не занят другой программой.</summary>
        internal static bool TryGetText(out string text, out string error, out bool empty)
        {
            text = null;
            error = null;
            empty = false;
            try
            {
                if (!Clipboard.ContainsText())
                {
                    empty = true;
                    error = "В буфере обмена нет текста.";
                    return false;
                }
                text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text))
                {
                    empty = true;
                    error = "В буфере обмена нет текста.";
                    return false;
                }
                return true;
            }
            catch (Exception failure)
            {
                error = "Не удалось прочитать буфер обмена: " + failure.Message;
                return false;
            }
        }

        /// <summary>Есть ли в буфере текст. Буфер, занятый другой программой, считается пустым.</summary>
        internal static bool HasText()
        {
            try
            {
                return Clipboard.ContainsText();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Есть ли в буфере картинка: PNG или обычный растр.</summary>
        internal static bool HasImage()
        {
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                return data != null && (data.GetDataPresent("PNG") || Clipboard.ContainsImage());
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Картинка из буфера как новый растр 32 бита: так с ней не уходят метаданные и исходный файл.
        /// Сначала PNG (его кладут браузеры и «Ножницы», в нём есть прозрачность), потом DIB.
        /// null: картинки нет (error = null) или её не удалось прочитать.
        /// </summary>
        internal static Bitmap TryGetImage(out string error)
        {
            error = null;
            try
            {
                IDataObject data = Clipboard.GetDataObject();
                if (data == null)
                {
                    return null;
                }
                if (data.GetDataPresent("PNG"))
                {
                    MemoryStream stream = data.GetData("PNG") as MemoryStream;
                    if (stream != null)
                    {
                        using (stream)
                        using (Image loaded = Image.FromStream(stream))
                        {
                            return Copy(loaded);
                        }
                    }
                }
                if (Clipboard.ContainsImage())
                {
                    using (Image loaded = Clipboard.GetImage())
                    {
                        if (loaded != null)
                        {
                            return Copy(loaded);
                        }
                    }
                }
                return null;
            }
            catch (Exception failure)
            {
                error = "Не удалось прочитать картинку из буфера: " + failure.Message;
                return null;
            }
        }

        private static Bitmap Copy(Image source)
        {
            Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(copy))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height), 0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel);
            }
            return copy;
        }

        /// <summary>
        /// Кладёт в буфер только готовую картинку: PNG и растр. Прежние форматы (HTML со ссылкой на исходник,
        /// файл) удаляются вместе со старым содержимым буфера.
        /// </summary>
        internal static bool TrySetImage(Bitmap image, out string error)
        {
            error = null;
            MemoryStream png = new MemoryStream();
            try
            {
                image.Save(png, ImageFormat.Png);
                png.Position = 0;
                DataObject data = new DataObject();
                data.SetData("PNG", false, png);
                data.SetData(DataFormats.Bitmap, true, image);
                Clipboard.SetDataObject(data, true, RetryCount, RetryDelayMs);
                ownSequence = Native.GetClipboardSequenceNumber();
                return true;
            }
            catch (Exception failure)
            {
                error = "Не удалось записать картинку в буфер: " + failure.Message;
                return false;
            }
            finally
            {
                png.Dispose();
            }
        }

        /// <summary>Очищает буфер, если последним его менял сам SafePaste: для картинки текст не сравнить.</summary>
        internal static void ClearIfOwn()
        {
            try
            {
                if (IsOwnChange())
                {
                    Clipboard.Clear();
                    ownSequence = Native.GetClipboardSequenceNumber();
                }
            }
            catch (Exception)
            {
                // Буфер занят другим приложением: там обезличенная картинка.
            }
        }

        internal static bool TrySetText(string text, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    Clipboard.Clear();
                    ownSequence = Native.GetClipboardSequenceNumber();
                    return true;
                }
                DataObject data = new DataObject();
                data.SetData(DataFormats.UnicodeText, text);
                Clipboard.SetDataObject(data, true, RetryCount, RetryDelayMs);
                ownSequence = Native.GetClipboardSequenceNumber();
                return true;
            }
            catch (Exception failure)
            {
                error = "Не удалось записать буфер обмена: " + failure.Message;
                return false;
            }
        }

        /// <summary>Очищает буфер, только если там всё ещё лежит наш обезличенный текст.</summary>
        internal static void ClearIfUnchanged(string expected)
        {
            try
            {
                if (Clipboard.ContainsText() && string.Equals(Clipboard.GetText(), expected, StringComparison.Ordinal))
                {
                    Clipboard.Clear();
                    ownSequence = Native.GetClipboardSequenceNumber();
                }
            }
            catch (Exception)
            {
                // Буфер занят другим приложением: не страшно, там обезличенный текст.
            }
        }

        /// <summary>История и облачная синхронизация могли сохранить исходный Ctrl+C до запуска SafePaste.</summary>
        internal static List<string> GetRisks()
        {
            List<string> risks = new List<string>();
            int? historyPolicy = ReadDword(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowClipboardHistory");
            int? cloudPolicy = ReadDword(Registry.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\System", "AllowCrossDeviceClipboard");
            int? history = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Clipboard", "EnableClipboardHistory");
            int? cloud = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Clipboard", "EnableCloudClipboard");
            int? upload = ReadDword(Registry.CurrentUser, @"Software\Microsoft\Clipboard", "CloudClipboardAutomaticUpload");

            if (history == 1 && historyPolicy != 0)
            {
                risks.Add("история буфера обмена (Win+V)");
            }
            if (upload == 1 && cloud != 0 && cloudPolicy != 0)
            {
                risks.Add("синхронизация буфера между устройствами");
            }
            return risks;
        }

        private static int? ReadDword(RegistryKey root, string path, string name)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(path))
                {
                    if (key == null)
                    {
                        return null;
                    }
                    object value = key.GetValue(name);
                    if (value == null)
                    {
                        return null;
                    }
                    return Convert.ToInt32(value);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
