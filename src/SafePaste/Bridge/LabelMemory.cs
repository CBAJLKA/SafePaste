using System;
using System.Collections.Generic;
using System.Globalization;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Bridge
{
    /// <summary>
    /// Запоминание меток при вставке из буфера. Значение, которое ушло из SafePaste меткой (вставкой или
    /// копированием результата), попадает в labels.dat: в следующих вставках оно получает тот же номер
    /// и скрывается даже без подсказок вокруг, иначе по открытому значению стало бы понятно, что стояло
    /// за меткой. Хранилище общее с мостом для агентов, поэтому метки совпадают и там.
    /// Не запоминаются секреты, отмеченное без сохранения и то, что пользователь оставил в тексте.
    /// Без подсказок запомненное скрывается ограниченный срок: в строгом режиме 30 дней, в обычном 14,
    /// в лёгком никогда (settings.json: RememberDays задаёт срок вручную, HideRemembered выключает).
    /// Сам файл хранит запись 30 дней с последнего использования: мосту метки нужны для продолжения
    /// переписки.
    /// </summary>
    public static class LabelMemory
    {
        /// <summary>Сколько дней labels.dat хранит неиспользуемую метку.</summary>
        public const int StoreDays = 30;

        private const string Broken = "Не удалось открыть запомненные метки. Вставка отменена. "
            + "Очистить их можно в окне «Правила и исключения».";

        /// <summary>Сколько дней запомненное скрывается без подсказок в этом режиме. 0: не скрывается.</summary>
        public static int ActiveDays(SafePasteSettings settings, ControlMode mode)
        {
            if (!settings.HideRemembered || mode == ControlMode.Light)
            {
                return 0;
            }
            if (settings.RememberDays > 0)
            {
                return Math.Min(settings.RememberDays, StoreDays);
            }
            return mode == ControlMode.Balanced ? 14 : StoreDays;
        }

        /// <summary>Кладёт запомненные значения в базу для детектора по настройкам и режиму.</summary>
        public static void Load(SafePasteDatabase database, SafePasteSettings settings, ControlMode mode)
        {
            Load(database, ActiveDays(settings, mode));
        }

        /// <summary>
        /// Кладёт в базу значения, которые встречались за последние days дней. Исключения пользователя
        /// важнее, а короткие значения, числа и обычные слова без подсказок не ищутся: «hostname: test»
        /// не должно прятать слово test во всех текстах. Номер из labels.dat они получают и так,
        /// если их нашёл детектор.
        /// </summary>
        public static void Load(SafePasteDatabase database, int days)
        {
            database.Remembered.Clear();
            if (days <= 0)
            {
                return;
            }
            LabelStore store = LabelStore.OpenIfExists();
            if (store == null)
            {
                return;
            }
            DateTime since = DateTime.UtcNow.Date.AddDays(-days);
            foreach (LabelEntry entry in Guard(delegate { return store.Snapshot(); }))
            {
                DateTime used;
                if (entry.Secret || database.IsAllowed(entry.Value) || !IsDistinctive(entry.Value)
                    || !DateTime.TryParseExact(entry.Used, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out used)
                    || used < since)
                {
                    continue;
                }
                LearnedValue value = new LearnedValue();
                value.Value = entry.Value;
                value.Type = entry.Type;
                database.Remembered.Add(value);
            }
        }

        /// <summary>Значение не похоже на обычное слово или число, поэтому его можно искать без подсказок.</summary>
        public static bool IsDistinctive(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Trim().Length < 3)
            {
                return false;
            }
            bool letters = true;
            bool digits = true;
            bool lower = true;
            foreach (char symbol in value)
            {
                letters &= char.IsLetter(symbol);
                digits &= char.IsDigit(symbol);
                lower &= !char.IsUpper(symbol);
            }
            if (digits)
            {
                return value.Length >= 6;
            }
            if (!letters)
            {
                return true;
            }
            return !lower && !Lexicons.UserStopWords.Contains(value) && !Lexicons.NameStopWords.Contains(value);
        }

        /// <summary>Замены для показа: номера берутся из запомненных меток, на диск ничего не пишется.</summary>
        public static ReplacementResult Preview(string text, IList<Detection> detections, SafePasteDatabase database)
        {
            LabelStore store = LabelStore.OpenIfExists();
            if (store == null)
            {
                return Replacer.Apply(text, detections, database);
            }
            return Guard(delegate { return store.Preview(text, detections, database); });
        }

        /// <summary>
        /// Замены для текста, который уходит из SafePaste. save: метки записываются в labels.dat;
        /// без него номера всё равно берутся из запомненных, но файл не меняется.
        /// </summary>
        public static ReplacementResult Apply(string text, IList<Detection> detections, SafePasteDatabase database, bool save)
        {
            if (!save)
            {
                return Preview(text, detections, database);
            }
            LabelStore store = new LabelStore(true);
            return Guard(delegate { return store.Apply(text, detections, database, false); });
        }

        /// <summary>Значение попало в исключения: его метка больше не нужна. Возвращает число удалённых.</summary>
        public static int Forget(string value)
        {
            LabelStore store = LabelStore.OpenIfExists();
            if (store == null || string.IsNullOrEmpty(value))
            {
                return 0;
            }
            int removed = 0;
            foreach (LabelEntry entry in Guard(delegate { return store.Snapshot(); }))
            {
                if (!entry.Secret && string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
                    && store.Delete(entry.Placeholder))
                {
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>Забывает все запомненные метки. Правила пользователя остаются. Возвращает число удалённых.</summary>
        public static int Clear()
        {
            LabelStore store = LabelStore.OpenIfExists();
            return store == null ? 0 : store.Clear();
        }

        /// <summary>Запомненные метки без секретов, для просмотра.</summary>
        public static List<LabelEntry> Snapshot()
        {
            List<LabelEntry> result = new List<LabelEntry>();
            LabelStore store = LabelStore.OpenIfExists();
            if (store == null)
            {
                return result;
            }
            foreach (LabelEntry entry in Guard(delegate { return store.Snapshot(); }))
            {
                if (!entry.Secret)
                {
                    result.Add(entry);
                }
            }
            return result;
        }

        /// <summary>Испорченный файл меток отменяет вставку с понятным советом, как и испорченная база.</summary>
        private static T Guard<T>(Func<T> operation)
        {
            try
            {
                return operation();
            }
            catch (DatabaseException failure)
            {
                throw new DatabaseException(Broken, failure);
            }
        }
    }
}
