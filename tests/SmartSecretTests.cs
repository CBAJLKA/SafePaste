using System;
using System.Collections.Generic;
using System.IO;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Tests
{
    public static partial class TestProgram
    {
        private static void SmartSecretTests()
        {
            Section("Интеллектуальный анализ секретов");
            string[,] cases =
            {
                { "Пароль 1290!!!Iva уже не подходят", "1290!!!Iva" },
                { "Пароль от почты 1290!!!Iva уже не подходит", "1290!!!Iva" },
                { "Пароль (1290!!!Iva) не используйте", "1290!!!Iva" },
                { "Новый пароль — Va7!moon, старый не используй", "Va7!moon" },
                { "The password is Blu3!river and has expired", "Blu3!river" },
                { "Passcode: 482916; keep it private", "482916" },
                { "Verification code 684205 expires soon", "684205" },
                { "Пароль 7482 вже не працює", "7482" },
                { "Das Passwort lautet Berg9!Mond heute", "Berg9!Mond" },
                { "Le mot de passe est Lune7!Bleue maintenant", "Lune7!Bleue" },
                { "La contraseña es Sol7!Azul ahora", "Sol7!Azul" },
                { "A senha é Lua7!Verde agora", "Lua7!Verde" },
                { "La password di accesso è Luna7!Verde oggi", "Luna7!Verde" },
                { "Hasło to Kwiat7!Las dziś", "Kwiat7!Las" },
                { "Şifre = Deniz7!Ay artık geçersiz", "Deniz7!Ay" },
                { "API key is Ab7!River used yesterday", "Ab7!River" },
                { "Le mot de passe «rose-moon» est ancien", "rose-moon" }
            };
            foreach (ControlMode mode in new ControlMode[] { ControlMode.Strict, ControlMode.Balanced, ControlMode.Light })
            {
                foreach (bool quick in new bool[] { false, true })
                {
                    for (int index = 0; index < cases.GetLength(0); index++)
                    {
                        string source = cases[index, 0];
                        string secret = cases[index, 1];
                        List<Detection> found = Detector.Scan(source, null, mode, quick, true);
                        Detection match = found.Find(delegate(Detection item)
                        {
                            return item.Type == "SECRET" && item.Value == secret;
                        });
                        Check("контекстный секрет " + index + ", " + mode + ", quick=" + quick,
                            match != null && match.Locked && match.Enabled && source.Substring(match.Start, match.Length) == secret, "");
                        string result = Replacer.Apply(source, found).Text;
                        Check("значение скрыто " + index + ", " + mode + ", quick=" + quick,
                            !result.Contains(secret), result);
                    }
                }
            }

            const string example = "Пароль 1290!!!Iva уже не подходят";
            foreach (ControlMode mode in new ControlMode[] { ControlMode.Strict, ControlMode.Balanced, ControlMode.Light })
            {
                string off = Replacer.Apply(example, Detector.Scan(example, null, mode, true, false)).Text;
                Check("выключатель действует в " + mode, off.Contains("1290!!!Iva"), off);
            }
            string config = "password=Demo123!";
            string configOff = Replacer.Apply(config, Detector.Scan(config, null, ControlMode.Light, true, false)).Text;
            Check("старые правила секретов действуют при выключенном анализе", !configOff.Contains("Demo123!"), configOff);

            string[] prose =
            {
                "Пароль должен содержать 12 символов",
                "Пароль неверный, попробуйте снова",
                "The password is expired and must be changed",
                "Das Passwort muss geändert werden",
                "La contraseña debe tener 12 caracteres",
                "Şifre geçersiz, tekrar deneyin"
            };
            foreach (string line in prose)
            {
                List<Detection> found = Detector.Scan(line, null, ControlMode.Light, false, true);
                Check("обычная фраза не становится секретом", !found.Exists(delegate(Detection item)
                {
                    return item.Type == "SECRET";
                }), line);
            }

            SafePasteSettings setting = new SafePasteSettings();
            Check("анализ включён по умолчанию", setting.SmartSecrets, "");
            setting.SmartSecrets = false;
            setting.Save();
            Check("настройка анализа сохраняется", !SafePasteSettings.Load().SmartSecrets, "");
            File.Delete(Paths.SettingsFile);
        }
    }
}
