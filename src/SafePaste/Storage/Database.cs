using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using SafePaste.Detecting;

namespace SafePaste.Storage
{
    /// <summary>Базу открыть не удалось, поэтому вставка отменяется, чтобы не пропустить заученные значения.</summary>
    public sealed class DatabaseException : Exception
    {
        public DatabaseException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>Значение, которое пользователь пометил вручную и просил скрывать всегда.</summary>
    public sealed class LearnedValue
    {
        public string Value { get; set; }
        public string Type { get; set; }
    }

    /// <summary>
    /// Закреплённый номер заполнителя: значение всегда получает один и тот же
    /// номер, например 10.44.7.219 → [IP_7] в любой вставке.
    /// </summary>
    public sealed class ReservedPlaceholder
    {
        public string Value { get; set; }
        public string Type { get; set; }
        public int Index { get; set; }

        public string Placeholder
        {
            get { return "[" + Type + "_" + Index.ToString(CultureInfo.InvariantCulture) + "]"; }
        }
    }

    /// <summary>Формат файла совместим с прежней версией SafePaste.</summary>
    internal sealed class DatabaseFileModel
    {
        public List<LearnedValue> Sensitive { get; set; }
        public List<string> Allow { get; set; }
        public List<ReservedPlaceholder> Reservations { get; set; }
        public List<string> NotSecrets { get; set; }
    }

    /// <summary>
    /// Локальная база: что скрывать всегда и что не скрывать никогда.
    /// Файл шифруется DPAPI для текущей учётной записи Windows и никуда не отправляется.
    /// </summary>
    public sealed class SafePasteDatabase : IPlaceholderNumbers
    {
        public readonly List<LearnedValue> Learned = new List<LearnedValue>();
        public readonly List<string> Allowed = new List<string>();
        public readonly List<ReservedPlaceholder> Reservations = new List<ReservedPlaceholder>();
        /// <summary>
        /// Значения, которые уже уходили из SafePaste метками (labels.dat). Детектор находит их, как заученные,
        /// но в database.dat они не пишутся: у них свой файл и своя очистка.
        /// </summary>
        public readonly List<LearnedValue> Remembered = new List<LearnedValue>();
        /// <summary>
        /// Значения, которые детектор принял за пароль или ключ, а пользователь отметил «Распознано ошибочно».
        /// Отдельно от allowlist: исключение для имени узла не должно открывать пароль с тем же текстом.
        /// Пароль сравнивается с учётом регистра.
        /// </summary>
        public readonly List<string> NotSecrets = new List<string>();
        private readonly HashSet<string> allowedIndex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> notSecretIndex = new HashSet<string>(StringComparer.Ordinal);

        public bool IsAllowed(string value)
        {
            return value != null && allowedIndex.Contains(value);
        }

        /// <summary>Пользователь отметил, что это значение не пароль и не ключ.</summary>
        public bool IsNotSecret(string value)
        {
            return value != null && notSecretIndex.Contains(value);
        }

        public bool AddNotSecret(string value)
        {
            if (string.IsNullOrEmpty(value) || !notSecretIndex.Add(value))
            {
                return false;
            }
            NotSecrets.Add(value);
            return true;
        }

        public bool RemoveNotSecret(string value)
        {
            if (value == null || !notSecretIndex.Remove(value))
            {
                return false;
            }
            NotSecrets.RemoveAll(delegate(string item) { return string.Equals(item, value, StringComparison.Ordinal); });
            return true;
        }

        /// <summary>Есть ли значение с этим типом в списке «скрывать всегда».</summary>
        public bool IsLearned(string value, string type)
        {
            foreach (LearnedValue entry in Learned)
            {
                if (string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- закреплённые номера

        /// <summary>Номер, закреплённый за значением, или 0, если закрепления нет.</summary>
        public int GetReservedIndex(string type, string value)
        {
            ReservedPlaceholder found = FindReservation(type, value);
            return found == null ? 0 : found.Index;
        }

        /// <summary>Занят ли номер другим значением этого типа.</summary>
        public bool IsIndexReserved(string type, int index)
        {
            foreach (ReservedPlaceholder reservation in Reservations)
            {
                if (reservation.Index == index
                    && string.Equals(reservation.Type, type, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Закрепляет номер за значением. Если желаемый номер уже занят другим значением,
        /// берётся ближайший свободный. Возвращает закреплённый номер.
        /// </summary>
        public int Reserve(string type, string value, int preferredIndex)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(value))
            {
                return 0;
            }
            string normalizedType = type.ToUpperInvariant();
            ReservedPlaceholder existing = FindReservation(normalizedType, value);
            if (existing != null)
            {
                return existing.Index;
            }
            int index = preferredIndex > 0 && !IsIndexReserved(normalizedType, preferredIndex)
                ? preferredIndex
                : NextFreeIndex(normalizedType);
            ReservedPlaceholder added = new ReservedPlaceholder();
            added.Type = normalizedType;
            added.Value = value;
            added.Index = index;
            Reservations.Add(added);
            return index;
        }

        public bool Unreserve(string type, string value)
        {
            ReservedPlaceholder found = FindReservation(type, value);
            if (found == null)
            {
                return false;
            }
            Reservations.Remove(found);
            return true;
        }

        private int NextFreeIndex(string type)
        {
            int index = 1;
            while (IsIndexReserved(type, index))
            {
                index++;
            }
            return index;
        }

        private ReservedPlaceholder FindReservation(string type, string value)
        {
            if (type == null || value == null)
            {
                return null;
            }
            foreach (ReservedPlaceholder reservation in Reservations)
            {
                if (string.Equals(reservation.Type, type, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(reservation.Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    return reservation;
                }
            }
            return null;
        }

        public static SafePasteDatabase Load()
        {
            SafePasteDatabase database = new SafePasteDatabase();
            string path = Paths.DatabaseFile;
            if (!File.Exists(path))
            {
                return database;
            }

            byte[] plain = null;
            try
            {
                byte[] cipher = File.ReadAllBytes(path);
                plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                string json = Encoding.UTF8.GetString(plain);
                DatabaseFileModel model = new JavaScriptSerializer().Deserialize<DatabaseFileModel>(json);
                if (model != null)
                {
                    if (model.Sensitive != null)
                    {
                        foreach (LearnedValue entry in model.Sensitive)
                        {
                            if (entry == null || string.IsNullOrEmpty(entry.Value) || string.IsNullOrEmpty(entry.Type))
                            {
                                continue;
                            }
                            database.AddLearned(entry.Value, entry.Type);
                        }
                    }
                    if (model.Allow != null)
                    {
                        foreach (string value in model.Allow)
                        {
                            if (!string.IsNullOrEmpty(value))
                            {
                                database.AddAllowed(value);
                            }
                        }
                    }
                    if (model.Reservations != null)
                    {
                        foreach (ReservedPlaceholder reservation in model.Reservations)
                        {
                            if (reservation == null || string.IsNullOrEmpty(reservation.Value)
                                || string.IsNullOrEmpty(reservation.Type) || reservation.Index <= 0)
                            {
                                continue;
                            }
                            database.Reserve(reservation.Type, reservation.Value, reservation.Index);
                        }
                    }
                    if (model.NotSecrets != null)
                    {
                        foreach (string value in model.NotSecrets)
                        {
                            database.AddNotSecret(value);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                throw new DatabaseException(
                    "Не удалось открыть локальную базу SafePaste. Вставка отменена. " +
                    "Сбросить базу можно через меню «Правила и исключения».", error);
            }
            finally
            {
                if (plain != null)
                {
                    Array.Clear(plain, 0, plain.Length);
                }
            }
            return database;
        }

        public void Save()
        {
            DatabaseFileModel model = new DatabaseFileModel();
            model.Sensitive = Learned;
            model.Allow = Allowed;
            model.Reservations = Reservations;
            model.NotSecrets = NotSecrets;
            string json = new JavaScriptSerializer().Serialize(model);
            byte[] plain = Encoding.UTF8.GetBytes(json);
            try
            {
                byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                string path = Paths.DatabaseFile;
                string temporary = path + ".tmp";
                File.WriteAllBytes(temporary, cipher);
                // Замена одним шагом: сбой записи не оставит повреждённую базу.
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                Array.Clear(plain, 0, plain.Length);
            }
        }

        /// <summary>Добавляет значение в список «скрывать всегда» и убирает его из allowlist.</summary>
        public bool AddLearned(string value, string type)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(type))
            {
                return false;
            }
            string normalizedType = type.ToUpperInvariant();
            RemoveAllowed(value);
            foreach (LearnedValue entry in Learned)
            {
                if (string.Equals(entry.Value, value, StringComparison.Ordinal)
                    && string.Equals(entry.Type, normalizedType, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            LearnedValue added = new LearnedValue();
            added.Value = value;
            added.Type = normalizedType;
            Learned.Add(added);
            return true;
        }

        /// <summary>Добавляет значение в allowlist и убирает его из списка «скрывать всегда».</summary>
        public bool AddAllowed(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            RemoveLearned(value, null);
            // Значению, которое никогда не скрывается, номер заполнителя не нужен.
            for (int index = Reservations.Count - 1; index >= 0; index--)
            {
                if (string.Equals(Reservations[index].Value, value, StringComparison.OrdinalIgnoreCase))
                {
                    Reservations.RemoveAt(index);
                }
            }
            if (!allowedIndex.Add(value))
            {
                return false;
            }
            Allowed.Add(value);
            return true;
        }

        public bool RemoveLearned(string value, string type)
        {
            bool removed = false;
            for (int index = Learned.Count - 1; index >= 0; index--)
            {
                LearnedValue entry = Learned[index];
                bool sameValue = string.Equals(entry.Value, value, StringComparison.OrdinalIgnoreCase);
                bool sameType = type == null || string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase);
                if (sameValue && sameType)
                {
                    Learned.RemoveAt(index);
                    removed = true;
                }
            }
            return removed;
        }

        public bool RemoveAllowed(string value)
        {
            if (value == null || !allowedIndex.Remove(value))
            {
                return false;
            }
            for (int index = Allowed.Count - 1; index >= 0; index--)
            {
                if (string.Equals(Allowed[index], value, StringComparison.OrdinalIgnoreCase))
                {
                    Allowed.RemoveAt(index);
                }
            }
            return true;
        }

        /// <summary>
        /// Переименовывает файл базы, чтобы начать с чистой. Возвращает путь копии или null,
        /// если базы не было.
        /// </summary>
        public static string Reset()
        {
            string path = Paths.DatabaseFile;
            if (!File.Exists(path))
            {
                return null;
            }
            string backup = path + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            File.Move(path, backup);
            return backup;
        }
    }
}
