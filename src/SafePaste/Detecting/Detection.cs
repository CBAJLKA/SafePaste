using System;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Насколько детектор уверен в находке. Быстрый режим скрывает только High, Learned и Manual,
    /// Low показывается в окне проверки, но по умолчанию не скрывается.
    /// </summary>
    public enum Confidence
    {
        Low,
        Medium,
        High,
        Learned,
        Manual
    }

    /// <summary>Одна находка: диапазон в исходном тексте и решение о её скрытии.</summary>
    public sealed class Detection
    {
        /// <summary>Происхождение значения из правил пользователя (database.dat).</summary>
        public const string LearnedSource = "База";
        /// <summary>Происхождение значения, которое уже уходило из SafePaste меткой (labels.dat).</summary>
        public const string RememberedSource = "Запомнено";

        public int Start;
        public int Length;
        public string Value;
        public string Type;
        public Confidence Confidence;
        public int Priority;
        public string Source;
        public bool Locked;
        public bool Manual;
        public bool Enabled;
        /// <summary>Мнение детектора без учёта режима: догадки он сам не скрывает.</summary>
        public bool DefaultEnabled;
        /// <summary>Отмечено без сохранения: скрывается только в этот раз, метка не запоминается.</summary>
        public bool Transient;
        /// <summary>Почему скрыто, для подсказки: «после слова «кластер», латиница в русском тексте».</summary>
        public string Reason;
        public string Placeholder;

        public Detection(int start, int length, string value, string type, Confidence confidence, int priority)
        {
            Start = start;
            Length = length;
            Value = value;
            Type = type;
            Confidence = confidence;
            Priority = priority;
            Source = "Regex";
            Locked = false;
            Manual = false;
            Enabled = true;
            DefaultEnabled = true;
            Placeholder = string.Empty;
        }

        public int End
        {
            get { return Start + Length; }
        }

        public bool Overlaps(int start, int end)
        {
            return start < End && Start < end;
        }

        /// <summary>Секреты не показываются в списке открытым текстом.</summary>
        public bool IsSensitiveValue
        {
            get
            {
                return Locked || Type == "SECRET" || Type == "TOKEN" || Type == "PRIVATE_KEY";
            }
        }

        public Detection Clone()
        {
            Detection copy = new Detection(Start, Length, Value, Type, Confidence, Priority);
            copy.Source = Source;
            copy.Locked = Locked;
            copy.Manual = Manual;
            copy.Enabled = Enabled;
            copy.DefaultEnabled = DefaultEnabled;
            copy.Transient = Transient;
            copy.Reason = Reason;
            copy.Placeholder = Placeholder;
            return copy;
        }
    }
}
