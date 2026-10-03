using System;
using System.Collections.Generic;

namespace SafePaste.Detecting
{
    /// <summary>
    /// Оставляет непересекающийся набор находок: побеждает более надёжная и более длинная.
    /// Результат отсортирован по позиции в тексте.
    /// </summary>
    public static class OverlapResolver
    {
        public static List<Detection> Resolve(List<Detection> candidates)
        {
            List<Detection> ordered = DropCoveredByRules(candidates);
            ordered.Sort(Compare);

            List<Detection> kept = new List<Detection>(ordered.Count);
            foreach (Detection item in ordered)
            {
                int low = 0;
                int high = kept.Count;
                while (low < high)
                {
                    int middle = low + ((high - low) / 2);
                    if (kept[middle].Start < item.Start)
                    {
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle;
                    }
                }
                bool overlaps = (low > 0 && item.Start < kept[low - 1].End)
                    || (low < kept.Count && kept[low].Start < item.End);
                if (!overlaps)
                {
                    kept.Insert(low, item);
                }
            }
            return kept;
        }

        /// <summary>
        /// Значение, которое пользователь отметил или запомнил, важнее автоматических находок внутри него.
        /// Иначе путь \\srv\share\dir, заученный целиком, проигрывал бы узлу srv по приоритету
        /// и скрывался кусками, а тип HOST у заученного имени заменялся бы на FQDN.
        /// Секреты правило не забирает: их нельзя оставить в тексте, а правило можно.
        /// Короткое правило внутри длинной находки (имя внутри ссылки) ничего не забирает.
        /// </summary>
        private static List<Detection> DropCoveredByRules(List<Detection> candidates)
        {
            List<Detection> rules = new List<Detection>();
            foreach (Detection item in candidates)
            {
                if (item.Enabled && IsUserRule(item))
                {
                    rules.Add(item);
                }
            }
            if (rules.Count == 0)
            {
                return new List<Detection>(candidates);
            }
            rules.Sort(delegate(Detection left, Detection right) { return left.Start.CompareTo(right.Start); });
            List<Detection> byStart = new List<Detection>(candidates);
            byStart.Sort(delegate(Detection left, Detection right) { return left.Start.CompareTo(right.Start); });

            List<Detection> kept = new List<Detection>(candidates.Count);
            int next = 0;
            int coveredUntil = -1;
            foreach (Detection item in byStart)
            {
                // Самое дальнее правило среди начавшихся не позже находки: если оно дотягивается
                // до её конца, находка целиком внутри правила.
                while (next < rules.Count && rules[next].Start <= item.Start)
                {
                    coveredUntil = Math.Max(coveredUntil, rules[next].End);
                    next++;
                }
                bool covered = !item.Locked && !IsUserRule(item) && item.End <= coveredUntil;
                if (!covered)
                {
                    kept.Add(item);
                }
            }
            return kept;
        }

        private static bool IsUserRule(Detection detection)
        {
            return detection.Confidence == Confidence.Learned || detection.Confidence == Confidence.Manual;
        }

        private static int Compare(Detection left, Detection right)
        {
            // Включённые находки не должны вытесняться выключенными.
            int result = (right.Enabled ? 1 : 0).CompareTo(left.Enabled ? 1 : 0);
            if (result != 0)
            {
                return result;
            }
            result = right.Priority.CompareTo(left.Priority);
            if (result != 0)
            {
                return result;
            }
            result = right.Length.CompareTo(left.Length);
            if (result != 0)
            {
                return result;
            }
            result = left.Start.CompareTo(right.Start);
            if (result != 0)
            {
                return result;
            }
            return string.CompareOrdinal(left.Type, right.Type);
        }
    }
}
