using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace SafePaste.Imaging
{
    /// <summary>Слово на картинке: текст и прямоугольник в пикселях исходной картинки.</summary>
    internal sealed class OcrWord
    {
        internal string Text;
        internal RectangleF Bounds;
        /// <summary>Номер строки в чтении.</summary>
        internal int Line;
        /// <summary>Где слово начинается в тексте для детектора.</summary>
        internal int Start;

        internal OcrWord(string text, RectangleF bounds)
        {
            Text = text ?? string.Empty;
            Bounds = bounds;
        }

        internal int End
        {
            get { return Start + Text.Length; }
        }

        /// <summary>Средняя ширина знака: по ней считаются части слова и промежутки.</summary>
        internal float CharWidth
        {
            get { return Text.Length == 0 ? Bounds.Width : Bounds.Width / Text.Length; }
        }
    }

    internal sealed class OcrLine
    {
        internal readonly List<OcrWord> Words = new List<OcrWord>();

        internal RectangleF Bounds
        {
            get
            {
                if (Words.Count == 0)
                {
                    return RectangleF.Empty;
                }
                RectangleF union = Words[0].Bounds;
                for (int i = 1; i < Words.Count; i++)
                {
                    union = RectangleF.Union(union, Words[i].Bounds);
                }
                return union;
            }
        }
    }

    /// <summary>Чтение картинки одним языком.</summary>
    internal sealed class OcrPage
    {
        internal string Language;
        internal readonly List<OcrLine> Lines = new List<OcrLine>();
    }

    /// <summary>
    /// Встроенное распознавание текста Windows (Windows.Media.Ocr). Работает на этом компьютере, без сети.
    /// AsTask и AsBuffer из System.Runtime.WindowsRuntime без SDK не компилируются, поэтому завершение
    /// ждётся через Completed, а буфер делается через CryptographicBuffer. Вызывать из фонового потока:
    /// в потоке интерфейса (STA) WinRT лишний раз не зовём.
    /// </summary>
    internal static class WindowsOcr
    {
        internal const int TimeoutMs = 30000;

        private static readonly object sync = new object();
        private static bool probed;
        private static string problem;
        private static readonly List<string> languages = new List<string>();
        private static int maxDimension = 10000;

        /// <summary>null, если распознавание есть; иначе причина для пользователя.</summary>
        internal static string Problem
        {
            get
            {
                Probe();
                return problem;
            }
        }

        /// <summary>Языки чтения: русский и английский, если они установлены, иначе язык профиля.</summary>
        internal static List<string> Languages
        {
            get
            {
                Probe();
                return new List<string>(languages);
            }
        }

        /// <summary>Больше этого числа пикселей по стороне распознавание не принимает.</summary>
        internal static int MaxDimension
        {
            get
            {
                Probe();
                return maxDimension;
            }
        }

        private static void Probe()
        {
            lock (sync)
            {
                if (probed)
                {
                    return;
                }
                probed = true;
                try
                {
                    ProbeCore();
                }
                catch (Exception failure)
                {
                    languages.Clear();
                    problem = "Распознавание текста Windows недоступно: " + failure.Message;
                    return;
                }
                if (languages.Count == 0)
                {
                    problem = "В Windows нет языка для распознавания текста. Его можно добавить в параметрах: "
                        + "«Время и язык», «Язык и регион».";
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ProbeCore()
        {
            maxDimension = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            string russian = null;
            string english = null;
            foreach (Windows.Globalization.Language language in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
            {
                string tag = language.LanguageTag;
                if (russian == null && tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                {
                    russian = tag;
                }
                if (english == null && tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    english = tag;
                }
            }
            if (russian != null)
            {
                languages.Add(russian);
            }
            if (english != null)
            {
                languages.Add(english);
            }
            if (languages.Count == 0)
            {
                Windows.Media.Ocr.OcrEngine engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine != null)
                {
                    languages.Add(engine.RecognizerLanguage.LanguageTag);
                }
            }
        }

        /// <summary>Читает картинку одним языком. Стороны картинки не больше MaxDimension.</summary>
        internal static OcrPage Recognize(Bitmap image, string language)
        {
            byte[] pixels = ReadPixels(image);
            try
            {
                return RecognizeCore(pixels, image.Width, image.Height, language);
            }
            finally
            {
                Array.Clear(pixels, 0, pixels.Length);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static OcrPage RecognizeCore(byte[] pixels, int width, int height, string language)
        {
            Windows.Media.Ocr.OcrEngine engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(
                new Windows.Globalization.Language(language));
            if (engine == null)
            {
                throw new InvalidOperationException("Язык распознавания " + language + " недоступен.");
            }
            Windows.Storage.Streams.IBuffer buffer = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(pixels);
            Windows.Graphics.Imaging.SoftwareBitmap bitmap = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(buffer,
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, width, height, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            try
            {
                Windows.Media.Ocr.OcrResult result = Wait(engine.RecognizeAsync(bitmap));
                OcrPage page = new OcrPage();
                page.Language = language;
                foreach (Windows.Media.Ocr.OcrLine line in result.Lines)
                {
                    OcrLine target = new OcrLine();
                    foreach (Windows.Media.Ocr.OcrWord word in line.Words)
                    {
                        if (string.IsNullOrEmpty(word.Text))
                        {
                            continue;
                        }
                        Windows.Foundation.Rect box = word.BoundingRect;
                        target.Words.Add(new OcrWord(word.Text,
                            new RectangleF((float)box.X, (float)box.Y, (float)box.Width, (float)box.Height)));
                    }
                    if (target.Words.Count > 0)
                    {
                        page.Lines.Add(target);
                    }
                }
                return page;
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        /// <summary>Ждёт асинхронную операцию WinRT без AsTask: обработчик Completed ставит событие.</summary>
        private static T Wait<T>(Windows.Foundation.IAsyncOperation<T> operation)
        {
            ManualResetEvent done = new ManualResetEvent(false);
            operation.Completed = delegate
            {
                try
                {
                    done.Set();
                }
                catch (ObjectDisposedException)
                {
                    // Ожидание уже бросили по таймауту.
                }
            };
            bool finished = done.WaitOne(TimeoutMs);
            done.Close();
            if (!finished)
            {
                try
                {
                    operation.Cancel();
                }
                catch (Exception)
                {
                }
                throw new TimeoutException("Распознавание текста не уложилось в " + (TimeoutMs / 1000) + " с.");
            }
            if (operation.Status != Windows.Foundation.AsyncStatus.Completed)
            {
                Exception error = operation.ErrorCode;
                throw new InvalidOperationException("Распознавание текста не удалось" + (error != null ? ": " + error.Message : "."));
            }
            return operation.GetResults();
        }

        /// <summary>Пиксели BGRA с премножённой альфой, строки без выравнивания.</summary>
        private static byte[] ReadPixels(Bitmap image)
        {
            Rectangle area = new Rectangle(0, 0, image.Width, image.Height);
            BitmapData data = image.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int row = image.Width * 4;
                byte[] pixels = new byte[row * image.Height];
                for (int y = 0; y < image.Height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), pixels, y * row, row);
                }
                return pixels;
            }
            finally
            {
                image.UnlockBits(data);
            }
        }

        /// <summary>
        /// Выполняет работу в фоновом потоке (MTA) и ждёт её. Для потока интерфейса: ожидание в STA
        /// прокачивает сообщения COM, а WinRT работает в своём потоке.
        /// </summary>
        internal static T OnWorker<T>(Func<T> work)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            {
                return work();
            }
            T result = default(T);
            Exception failure = null;
            ManualResetEvent done = new ManualResetEvent(false);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    result = work();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    try
                    {
                        done.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            });
            bool finished = done.WaitOne(TimeoutMs * 4);
            done.Close();
            if (!finished)
            {
                throw new TimeoutException("Проверка картинки не уложилась во время.");
            }
            if (failure != null)
            {
                throw new InvalidOperationException(failure.Message, failure);
            }
            return result;
        }
    }
}
