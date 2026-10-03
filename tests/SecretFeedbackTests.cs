using System;
using System.Collections.Generic;
using System.IO;
using SafePaste.Detecting;
using SafePaste.Imaging;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    public static partial class TestProgram
    {
        /// <summary>
        /// Что паролем не считается: фраза после «Пароль:», заглушки, подписи полей и версии. И отметка
        /// «Распознано ошибочно» из окна проверки: значение больше не секрет, но только это значение.
        /// </summary>
        private static void SecretFeedbackTests()
        {
            Section("Пароли: фразы, заглушки и отметка ошибки");
            string[] prose =
            {
                "Пароль: не менее 8 символов",
                "Пароль: изменён вчера",
                "Пароль: см. KeePass",
                "Пароль: у Иванова",
                "Пароль: тот же, что и раньше",
                "Пароль: обязательно",
                "Пароль: сбрасывается через портал",
                "Пароль: нет",
                "Пароль: -",
                "Пароль: ???",
                "Password: must contain 8 characters",
                "Password: same as before",
                "Password: see vault",
                "Password: expired",
                "Password: TBD",
                "Password: ...",
                "Password: (empty)",
                "Password: [hidden]",
                "Password: the one from the email",
                "Token: expired",
                "токен: истёк",
                "PIN: 4 цифры",
                "Credentials: in the vault",
                "TOTP: включён",
                "Сменить пароль: Ctrl+Alt+Del",
                "Пароль пользователя: задаётся при первом входе",
                "Build:Release2024",
                "Status:Active2024",
                "v1.2.3-Beta!"
            };
            foreach (string line in prose)
            {
                NoSecret("не пароль: " + line, line);
            }

            Hides("пароль одним словом", "Пароль: qwerty", "qwerty");
            Hides("пароль в конце фразы", "Пароль: привет", "привет");
            Hides("латиница в русской фразе", "Пароль: qwerty для входа", "qwerty");
            Hides("пароль с цифрами в начале фразы", "Пароль: Лето2024 для почты", "Лето2024");
            Hides("пароль перед запятой", "Пароль: qwerty, логин: admin", "qwerty");
            Hides("короткий PIN", "PIN: 1234", "1234");
            Hides("CVV", "CVV: 123", "123");
            Hides("пароль через равно", "pwd=ab", "ab");
            Hides("заглушка в кавычках не нужна", "password: \"secret\"", "secret");
            string weak = Replacer.Apply("password: password", Detector.Scan("password: password", null, false)).Text;
            // Повтор значения скрывается везде, поэтому и слово-ключ закрыто той же меткой.
            Check("слабый пароль password", weak.EndsWith(": [SECRET_1]", StringComparison.Ordinal), weak);
            Hides("пароль администратора с подписью", "Пароль администратора: Qwerty123!", "Qwerty123!");
            Hides("пара с учётной записью", "admin:Router2024", "Router2024");

            SafePasteDatabase database = new SafePasteDatabase();
            Check("отметка ошибки добавляется", database.AddNotSecret("Router2024") && !database.AddNotSecret("Router2024"), "");
            List<Detection> found = Detector.Scan("admin:Router2024", database, ControlMode.Strict, false);
            Check("отмеченное не считается паролем", !found.Exists(delegate(Detection item) { return item.Locked; }), "");
            Check("отмеченное остаётся в тексте", Replacer.Apply("admin:Router2024", found).Text.Contains("Router2024"), "");
            Check("отметка с учётом регистра", Detector.Scan("admin:ROUTER2024x", database, false).Exists(delegate(Detection item)
            {
                return item.Locked;
            }), "");
            Check("отметка не трогает другой пароль", Detector.Scan("password=Router2025", database, false).Exists(delegate(Detection item)
            {
                return item.Locked && item.Value == "Router2025";
            }), "");
            Check("быстрая вставка тоже не скрывает отмеченное",
                Replacer.Apply("password=Router2024", Detector.Scan("password=Router2024", database, true)).Text.Contains("Router2024"), "");
            database.AddAllowed("Hunter2024");
            Check("исключение не открывает пароль", !Replacer.Apply("password=Hunter2024",
                Detector.Scan("password=Hunter2024", database, false)).Text.Contains("Hunter2024"), "");

            database.Save();
            SafePasteDatabase loaded = SafePasteDatabase.Load();
            Check("отметка ошибки сохраняется", loaded.IsNotSecret("Router2024") && loaded.IsAllowed("Hunter2024"), "");
            Check("отметка снимается", loaded.RemoveNotSecret("Router2024") && !loaded.IsNotSecret("Router2024"), "");

            // Хвост строки после слова пароля на картинке подчиняется той же отметке.
            ImageReading reading = new ImageReading();
            reading.Width = 400;
            reading.Height = 60;
            OcrLine ocrLine = new OcrLine();
            ocrLine.Words.Add(new OcrWord("password:", new System.Drawing.RectangleF(10, 20, 90, 20)));
            ocrLine.Words.Add(new OcrWord("see", new System.Drawing.RectangleF(105, 20, 30, 20)));
            ocrLine.Words.Add(new OcrWord("wiki", new System.Drawing.RectangleF(140, 20, 40, 20)));
            ImageReader.Build(reading, new List<OcrLine> { ocrLine });
            Check("хвост на картинке закрыт без отметки", ImageScanner.Detect(reading, new SafePasteDatabase(), ControlMode.Balanced, null)
                .Exists(delegate(Detection item) { return item.Locked; }), "");
            SafePasteDatabase marked = new SafePasteDatabase();
            marked.AddNotSecret("see wiki");
            Check("хвост на картинке открыт после отметки", !ImageScanner.Detect(reading, marked, ControlMode.Balanced, null)
                .Exists(delegate(Detection item) { return item.Locked; }), "");
            File.Delete(Paths.DatabaseFile);
        }

        private static void NoSecret(string name, string text)
        {
            List<Detection> found = Detector.Scan(text, null, ControlMode.Strict, false, true);
            Detection secret = found.Find(delegate(Detection item) { return item.Locked || item.Type == "SECRET"; });
            Check(name, secret == null, secret == null ? "" : "найдено: " + secret.Value);
        }
    }
}
