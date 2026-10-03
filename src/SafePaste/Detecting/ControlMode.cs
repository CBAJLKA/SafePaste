using System;

namespace SafePaste.Detecting
{
    /// <summary>Насколько строго скрывать найденное. Режим один на всё приложение.</summary>
    public enum ControlMode
    {
        /// <summary>Скрыть всё найденное, даже догадки и пути к файлам целиком.</summary>
        Strict,

        /// <summary>Скрыть то, в чём детектор уверен; догадки и пути остаются в тексте.</summary>
        Balanced,

        /// <summary>Скрыть только пароли, ключи и значения из правил пользователя.</summary>
        Light,

        /// <summary>
        /// Для моста к ИИ-агентам, в интерфейсе не показывается. Человека, который поправил бы
        /// детектор, рядом нет, поэтому догадки об именах скрываются. Путь целиком и то, что
        /// ничего не выдаёт (маски, 127.0.0.1, встроенные SID), остаются: без них агент не поймёт
        /// ни сеть, ни структуру папок.
        /// </summary>
        Bridge
    }

    /// <summary>
    /// Что скрывается в каждом режиме. Секреты, заученные и отмеченные вручную значения
    /// скрываются всегда: без них даже лёгкий режим терял бы смысл.
    /// </summary>
    public static class ControlModes
    {
        /// <summary>Галочка по умолчанию в окне проверки.</summary>
        public static bool HiddenInReview(Detection detection, ControlMode mode)
        {
            if (detection.Locked)
            {
                return true;
            }
            switch (mode)
            {
                case ControlMode.Strict:
                    return true;
                case ControlMode.Light:
                    return detection.DefaultEnabled && IsUserRule(detection);
                case ControlMode.Bridge:
                    return HiddenInBridge(detection);
                default:
                    return detection.DefaultEnabled;
            }
        }

        /// <summary>
        /// Выключенные по умолчанию находки этих типов ничего не выдают: маска подсети, 127.0.0.1,
        /// встроенный SID, нулевой GUID. Путь целиком выключен, чтобы победили узел, сетевая папка
        /// и пользователь внутри него.
        /// </summary>
        private static bool HiddenInBridge(Detection detection)
        {
            if (detection.DefaultEnabled)
            {
                return true;
            }
            switch (detection.Type)
            {
                case "IP":
                case "IPV6":
                case "SID":
                case "GUID":
                case "PATH":
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// Что скрывает быстрая вставка без окна. В обычном режиме это только надёжные находки:
        /// исправить ошибку детектора там негде.
        /// </summary>
        public static bool HiddenInQuick(Detection detection, ControlMode mode)
        {
            if (detection.Locked)
            {
                return true;
            }
            switch (mode)
            {
                case ControlMode.Strict:
                    return true;
                case ControlMode.Light:
                    return detection.DefaultEnabled && IsUserRule(detection);
                case ControlMode.Bridge:
                    return HiddenInBridge(detection);
                default:
                    return detection.DefaultEnabled
                        && (detection.Confidence == Confidence.High || IsUserRule(detection));
            }
        }

        private static bool IsUserRule(Detection detection)
        {
            return detection.Confidence == Confidence.Learned || detection.Confidence == Confidence.Manual;
        }

        /// <summary>Значение для settings.json.</summary>
        public static string ToKey(ControlMode mode)
        {
            return mode.ToString();
        }

        public static ControlMode Parse(string value, ControlMode fallback)
        {
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }
            foreach (ControlMode mode in new ControlMode[] { ControlMode.Strict, ControlMode.Balanced, ControlMode.Light })
            {
                if (string.Equals(mode.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return mode;
                }
            }
            return fallback;
        }
    }
}
