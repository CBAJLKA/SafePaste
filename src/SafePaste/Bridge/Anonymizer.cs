using System;
using System.Collections.Generic;
using System.IO;
using SafePaste.Detecting;
using SafePaste.Storage;

namespace SafePaste.Bridge
{
    public sealed class Anonymizer
    {
        private readonly LabelStore store;
        private SafePasteDatabase user;
        private SafePasteDatabase scan;
        private DateTime databaseStamp;
        private int storeVersion = -1;
        public int LastHidden { get; private set; }
        public string LastWarning { get; private set; }

        public Anonymizer(LabelStore labels) { store = labels; }

        private void Refresh()
        {
            DateTime stamp = File.Exists(Paths.DatabaseFile) ? File.GetLastWriteTimeUtc(Paths.DatabaseFile) : DateTime.MinValue;
            if (scan != null && stamp == databaseStamp && storeVersion == store.Version) return;
            user = SafePasteDatabase.Load();
            scan = SafePasteDatabase.Load();
            foreach (LabelEntry entry in store.Snapshot()) scan.Learned.Add(new LearnedValue { Value = entry.Value, Type = entry.Type });
            databaseStamp = stamp; storeVersion = store.Version;
        }

        public string Anonymize(string raw)
        {
            try
            {
                Refresh();
                string escaped = Labels.EscapeRaw(raw);
                List<Detection> found = Detector.Scan(escaped, scan, ControlMode.Bridge, false);
                ReplacementResult replaced = store.Apply(escaped, found, user, true);
                LastHidden = replaced.HiddenCount;
                string warning;
                string safe = LeakGuard.Check(replaced.Text, store.Snapshot(), out warning);
                LastWarning = warning;
                return safe;
            }
            catch (Exception)
            {
                LastHidden = 0; LastWarning = null;
                throw new InvalidOperationException("Не удалось безопасно обезличить ответ. Ответ не отправлен.");
            }
        }
    }
}
