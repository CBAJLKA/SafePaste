using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Bridge
{
    public sealed class LabelEntry
    {
        public string Type { get; set; }
        public string Value { get; set; }
        public int Index { get; set; }
        public string Used { get; set; }
        public bool Secret { get; set; }
        public string Placeholder { get { return "[" + Type + "_" + Index.ToString(CultureInfo.InvariantCulture) + "]"; } }
    }

    internal sealed class LabelFileModel
    {
        public List<LabelEntry> Labels { get; set; }
    }

    public sealed class LabelStore : IPlaceholderNumbers
    {
        private readonly string path;
        private readonly bool persist;
        private readonly Mutex mutex;
        private readonly Dictionary<string, LabelEntry> byValue = new Dictionary<string, LabelEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LabelEntry> byLabel = new Dictionary<string, LabelEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<int>> taken = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<LabelEntry> memorySecrets = new List<LabelEntry>();
        private SafePasteDatabase userDatabase;
        private DateTime stamp;
        private long size = -1;
        private bool dirty;
        public int Version { get; private set; }

        public LabelStore(bool persist) : this(Paths.LabelsFile, persist) { }

        public LabelStore(string file, bool persist)
        {
            path = file;
            this.persist = persist;
            string sid = WindowsIdentity.GetCurrent().User.Value.Replace('-', '_');
            mutex = new Mutex(false, @"Local\SafePaste-Labels-" + sid);
        }

        public static LabelStore OpenIfExists()
        {
            return File.Exists(Paths.LabelsFile) ? new LabelStore(true) : null;
        }

        private static string Key(string type, string value) { return type + "\u001f" + value; }
        private static bool IsSecret(string type) { return type == "SECRET" || type == "TOKEN" || type == "PRIVATE_KEY"; }

        private void WithLock(Action action)
        {
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("Не удалось получить доступ к хранилищу меток.");
                lock (this) { action(); }
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }

        private void Reindex()
        {
            byValue.Clear(); byLabel.Clear(); taken.Clear();
            foreach (LabelEntry entry in entries)
            {
                byValue[Key(entry.Type, entry.Value)] = entry;
                byLabel[entry.Placeholder] = entry;
                HashSet<int> numbers;
                if (!taken.TryGetValue(entry.Type, out numbers)) { numbers = new HashSet<int>(); taken.Add(entry.Type, numbers); }
                numbers.Add(entry.Index);
            }
        }

        private readonly List<LabelEntry> entries = new List<LabelEntry>();

        private void Reload(SafePasteDatabase user, bool force)
        {
            if (!persist) return;
            FileInfo info = new FileInfo(path);
            DateTime nextStamp = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            long nextSize = info.Exists ? info.Length : -1;
            if (!force && nextStamp == stamp && nextSize == size) return;
            List<LabelEntry> loaded = new List<LabelEntry>();
            if (info.Exists)
            {
                byte[] plain = null;
                try
                {
                    plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
                    LabelFileModel model = new JavaScriptSerializer().Deserialize<LabelFileModel>(Encoding.UTF8.GetString(plain));
                    if (model == null || model.Labels == null) throw new FormatException("Пустой файл меток.");
                    foreach (LabelEntry entry in model.Labels)
                    {
                        DateTime used;
                        if (entry == null || string.IsNullOrEmpty(entry.Type) || string.IsNullOrEmpty(entry.Value)
                            || entry.Index <= 0 || IsSecret(entry.Type) || entry.Secret
                            || !DateTime.TryParseExact(entry.Used, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out used) || used < DateTime.UtcNow.Date.AddDays(-30))
                        { dirty = true; continue; }
                        if (user != null && user.IsIndexReserved(entry.Type, entry.Index)
                            && user.GetReservedIndex(entry.Type, entry.Value) != entry.Index)
                        { dirty = true; continue; }
                        loaded.Add(entry);
                    }
                }
                catch (Exception error) { throw new DatabaseException("Не удалось открыть хранилище меток. Операция отменена.", error); }
                finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
            }
            entries.Clear();
            entries.AddRange(loaded);
            entries.AddRange(memorySecrets);
            Reindex();
            stamp = nextStamp; size = nextSize; Version++;
        }

        private void PruneReservations(SafePasteDatabase user)
        {
            if (user == null) return;
            bool removed = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                LabelEntry entry = entries[i];
                int reserved = user.GetReservedIndex(entry.Type, entry.Value);
                if ((reserved > 0 && reserved != entry.Index)
                    || (user.IsIndexReserved(entry.Type, entry.Index) && reserved != entry.Index))
                {
                    entries.RemoveAt(i);
                    if (entry.Secret) memorySecrets.Remove(entry);
                    else dirty = true;
                    removed = true;
                }
            }
            if (removed) { Reindex(); Version++; }
        }

        private void Save()
        {
            if (!persist || !dirty) return;
            LabelFileModel model = new LabelFileModel();
            model.Labels = new List<LabelEntry>();
            foreach (LabelEntry entry in entries) if (!entry.Secret) model.Labels.Add(entry);
            byte[] plain = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(model));
            try
            {
                byte[] cipher = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, cipher);
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                FileInfo info = new FileInfo(path);
                stamp = info.LastWriteTimeUtc; size = info.Length; dirty = false;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public int GetReservedIndex(string type, string value)
        {
            int index = userDatabase == null ? 0 : userDatabase.GetReservedIndex(type, value);
            if (index > 0) return index;
            LabelEntry entry;
            return byValue.TryGetValue(Key(type, value), out entry) ? entry.Index : 0;
        }

        public bool IsIndexReserved(string type, int index)
        {
            HashSet<int> numbers;
            return (userDatabase != null && userDatabase.IsIndexReserved(type, index))
                || (taken.TryGetValue(type, out numbers) && numbers.Contains(index));
        }

        public ReplacementResult Apply(string text, IList<Detection> detections, SafePasteDatabase user, bool keepLineBreaks)
        {
            ReplacementResult result = null;
            WithLock(delegate
            {
                Reload(user, false);
                PruneReservations(user);
                userDatabase = user;
                try
                {
                    result = Replacer.Apply(text, detections, this, keepLineBreaks);
                    // Отмеченное без сохранения скрыто только в этот раз и меткой не запоминается.
                    foreach (Detection detection in result.Applied)
                        if (!detection.Transient)
                            Add(detection.Type, detection.Value, detection.Placeholder, detection.IsSensitiveValue);
                    Save();
                }
                finally { userDatabase = null; }
            });
            return result;
        }

        public ReplacementResult Preview(string text, IList<Detection> detections, SafePasteDatabase user)
        {
            ReplacementResult result = null;
            WithLock(delegate
            {
                Reload(user, false);
                PruneReservations(user);
                userDatabase = user;
                try { result = Replacer.Apply(text, detections, this); }
                finally { userDatabase = null; }
            });
            return result;
        }

        private void Add(string type, string value, string placeholder, bool secret)
        {
            LabelEntry existing;
            if (byValue.TryGetValue(Key(type, value), out existing))
            {
                if (existing.Placeholder != placeholder) throw new InvalidOperationException("Конфликт меток. Операция отменена.");
                string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (existing.Used != today) { existing.Used = today; dirty |= !existing.Secret; Version++; }
                return;
            }
            if (byLabel.ContainsKey(placeholder)) throw new InvalidOperationException("Номер метки уже занят. Операция отменена.");
            LabelEntry entry = new LabelEntry();
            entry.Type = type.ToUpperInvariant(); entry.Value = value;
            entry.Index = int.Parse(placeholder.Substring(placeholder.LastIndexOf('_') + 1).TrimEnd(']'), CultureInfo.InvariantCulture);
            entry.Used = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            entry.Secret = secret || IsSecret(entry.Type);
            entries.Add(entry);
            if (entry.Secret) memorySecrets.Add(entry);
            else dirty = true;
            Reindex(); Version++;
        }

        public string Hide(string value, string type)
        {
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Пустое значение.");
            if (string.IsNullOrEmpty(type)) type = "TEXT";
            type = type.ToUpperInvariant();
            if (!Regex.IsMatch(type, @"^[A-Z][A-Z0-9_]{0,31}$")) throw new ArgumentException("Недопустимый тип метки.");
            string placeholder = null;
            WithLock(delegate
            {
                SafePasteDatabase user = SafePasteDatabase.Load();
                Reload(user, false);
                PruneReservations(user);
                userDatabase = user;
                try
                {
                LabelEntry existing;
                if (byValue.TryGetValue(Key(type, value), out existing))
                { placeholder = existing.Placeholder; Add(type, value, placeholder, existing.Secret); Save(); return; }
                int index = 1;
                while (IsIndexReserved(type, index)) index++;
                placeholder = "[" + type + "_" + index.ToString(CultureInfo.InvariantCulture) + "]";
                Add(type, value, placeholder, false); Save();
                }
                finally { userDatabase = null; }
            });
            return placeholder;
        }

        public bool TryResolve(string label, out string value, out bool secret)
        {
            LabelEntry found = null;
            WithLock(delegate
            {
                Reload(null, false);
                if (byLabel.TryGetValue(label, out found))
                {
                    string today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (found.Used != today) { found.Used = today; dirty |= !found.Secret; Version++; }
                }
            });
            value = found == null ? null : found.Value;
            secret = found != null && found.Secret;
            return found != null;
        }

        public void Flush() { WithLock(delegate { Reload(null, false); Save(); }); }

        public bool Delete(string placeholder)
        {
            bool removed = false;
            WithLock(delegate
            {
                Reload(null, false);
                LabelEntry entry;
                if (!byLabel.TryGetValue(placeholder, out entry)) return;
                entries.Remove(entry);
                if (entry.Secret) memorySecrets.Remove(entry);
                else dirty = true;
                Reindex(); Version++; Save(); removed = true;
            });
            return removed;
        }

        public bool Reserve(string placeholder)
        {
            bool reserved = false;
            WithLock(delegate
            {
                SafePasteDatabase database = SafePasteDatabase.Load();
                Reload(database, false);
                LabelEntry entry;
                if (!byLabel.TryGetValue(placeholder, out entry) || entry.Secret) return;
                int index = database.Reserve(entry.Type, entry.Value, entry.Index);
                if (index != entry.Index) throw new InvalidOperationException("Номер уже закреплён за другим значением.");
                database.Save(); reserved = true;
            });
            return reserved;
        }

        /// <summary>
        /// Забывает все сохранённые метки. Закреплённые номера живут в правилах и остаются, секреты моста
        /// и так только в памяти. Испорченный файл не мешает: он заменяется пустым.
        /// Возвращает, сколько меток удалено.
        /// </summary>
        public int Clear()
        {
            int removed = 0;
            WithLock(delegate
            {
                try
                {
                    Reload(null, false);
                    foreach (LabelEntry entry in entries) if (!entry.Secret) removed++;
                }
                catch (DatabaseException) { }
                entries.Clear();
                entries.AddRange(memorySecrets);
                Reindex(); Version++;
                if (File.Exists(path)) { dirty = true; Save(); }
            });
            return removed;
        }

        public int CleanupOld()
        {
            int before = 0, after = 0;
            WithLock(delegate
            {
                before = entries.Count;
                Reload(null, true);
                after = entries.Count;
                Save();
            });
            return Math.Max(0, before - after);
        }

        public List<LabelEntry> Snapshot()
        {
            List<LabelEntry> copy = null;
            WithLock(delegate { Reload(null, false); copy = new List<LabelEntry>(entries); });
            return copy;
        }

        public string Describe()
        {
            StringBuilder text = new StringBuilder();
            foreach (LabelEntry entry in Snapshot()) text.Append(entry.Placeholder).Append(entry.Secret ? " (секрет)" : "").Append('\n');
            return text.Length == 0 ? "Меток пока нет." : text.ToString();
        }
    }
}
