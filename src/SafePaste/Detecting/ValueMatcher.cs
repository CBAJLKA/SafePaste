using System;
using System.Collections.Generic;

namespace SafePaste.Detecting
{
    /// <summary>Совпадение из <see cref="ValueMatcher"/>: где найдено и какое значение из списка.</summary>
    public struct ValueMatch
    {
        public readonly int Start;
        public readonly int Length;
        public readonly int Index;

        public ValueMatch(int start, int length, int index)
        {
            Start = start;
            Length = length;
            Index = index;
        }
    }

    /// <summary>
    /// Поиск многих значений за один проход по тексту (автомат Ахо-Корасик), без учёта регистра.
    /// Одна регулярка из сотен вариантов проверяет каждый вариант в каждой позиции: при 300 значениях
    /// проверка лога в 350 КБ шла больше трёх секунд, при 3000 не укладывалась в лимит времени.
    /// Автомату число значений почти не важно. Правила совпадения те же, что у шаблона одного значения
    /// в <see cref="Detector"/>: граница нужна только там, где значение начинается или кончается буквой,
    /// цифрой, «_» или «-»; из совпадений, начинающихся в одном месте, берётся самое длинное,
    /// а следующее ищется после его конца.
    /// </summary>
    public sealed class ValueMatcher
    {
        private readonly List<string> values = new List<string>();
        // Узлы дерева: буква ребра от родителя, глубина, первый ребёнок и следующий брат для обхода,
        // ссылка неудачи, номер значения, которое здесь кончается, и ближайший такой узел по ссылкам.
        private readonly List<char> symbol = new List<char>();
        private readonly List<int> depth = new List<int>();
        private readonly List<int> firstChild = new List<int>();
        private readonly List<int> nextSibling = new List<int>();
        private readonly List<int> fail = new List<int>();
        private readonly List<int> output = new List<int>();
        private readonly List<int> dictionary = new List<int>();
        private readonly Dictionary<long, int> edges = new Dictionary<long, int>();
        private bool built;

        public ValueMatcher()
        {
            AddNode('\0', 0);
        }

        public int Count
        {
            get { return values.Count; }
        }

        /// <summary>Значение по номеру из <see cref="ValueMatch.Index"/>.</summary>
        public string this[int index]
        {
            get { return values[index]; }
        }

        /// <summary>
        /// Добавляет значение. Повтор без учёта регистра не добавляется: остаётся первое.
        /// Возвращает номер значения или -1, если оно пустое или уже есть.
        /// </summary>
        public int Add(string value)
        {
            if (built)
            {
                throw new InvalidOperationException("Значения добавляются до первого поиска.");
            }
            if (string.IsNullOrEmpty(value))
            {
                return -1;
            }
            int node = 0;
            foreach (char raw in value)
            {
                char lower = char.ToLowerInvariant(raw);
                int child;
                if (!edges.TryGetValue(Key(node, lower), out child))
                {
                    child = AddNode(lower, depth[node] + 1);
                    edges.Add(Key(node, lower), child);
                    nextSibling[child] = firstChild[node];
                    firstChild[node] = child;
                }
                node = child;
            }
            if (output[node] >= 0)
            {
                return -1;
            }
            output[node] = values.Count;
            values.Add(value);
            return output[node];
        }

        /// <summary>Непересекающиеся совпадения слева направо.</summary>
        public List<ValueMatch> Find(string text)
        {
            List<ValueMatch> result = new List<ValueMatch>();
            if (string.IsNullOrEmpty(text) || values.Count == 0)
            {
                return result;
            }
            Build();
            // Для каждого места начала самое длинное значение с подходящими границами.
            Dictionary<int, ValueMatch> longest = new Dictionary<int, ValueMatch>();
            int state = 0;
            for (int position = 0; position < text.Length; position++)
            {
                state = Step(state, char.ToLowerInvariant(text[position]));
                for (int node = output[state] >= 0 ? state : dictionary[state]; node > 0; node = dictionary[node])
                {
                    int length = depth[node];
                    int start = position - length + 1;
                    ValueMatch best;
                    if (longest.TryGetValue(start, out best) && best.Length >= length)
                    {
                        continue;
                    }
                    if (FitsBounds(text, start, length, values[output[node]]))
                    {
                        longest[start] = new ValueMatch(start, length, output[node]);
                    }
                }
            }
            List<ValueMatch> candidates = new List<ValueMatch>(longest.Values);
            candidates.Sort(delegate(ValueMatch left, ValueMatch right) { return left.Start.CompareTo(right.Start); });
            int end = 0;
            foreach (ValueMatch candidate in candidates)
            {
                if (candidate.Start >= end)
                {
                    result.Add(candidate);
                    end = candidate.Start + candidate.Length;
                }
            }
            return result;
        }

        /// <summary>Та же граница, что у шаблона одного значения: SRV-DB01 не находится внутри SRV-DB01-old.</summary>
        private static bool FitsBounds(string text, int start, int length, string value)
        {
            if (IsWordEdge(value[0]) && start > 0 && IsWordEdge(text[start - 1]))
            {
                return false;
            }
            int end = start + length;
            return !(IsWordEdge(value[value.Length - 1]) && end < text.Length && IsWordEdge(text[end]));
        }

        /// <summary>Буква, цифра, «_» или «-»: то же, что класс [\p{L}\p{N}_-] в регулярках детектора.</summary>
        internal static bool IsWordEdge(char value)
        {
            return char.IsLetter(value) || char.IsNumber(value) || value == '_' || value == '-';
        }

        private int Step(int state, char value)
        {
            int next;
            while (!edges.TryGetValue(Key(state, value), out next))
            {
                if (state == 0)
                {
                    return 0;
                }
                state = fail[state];
            }
            return next;
        }

        /// <summary>Ссылки неудач и словарные ссылки обходом в ширину, один раз перед первым поиском.</summary>
        private void Build()
        {
            if (built)
            {
                return;
            }
            built = true;
            Queue<int> queue = new Queue<int>();
            for (int child = firstChild[0]; child >= 0; child = nextSibling[child])
            {
                queue.Enqueue(child);
            }
            while (queue.Count > 0)
            {
                int node = queue.Dequeue();
                for (int child = firstChild[node]; child >= 0; child = nextSibling[child])
                {
                    fail[child] = Step(fail[node], symbol[child]);
                    int suffix = fail[child];
                    dictionary[child] = output[suffix] >= 0 ? suffix : dictionary[suffix];
                    queue.Enqueue(child);
                }
            }
        }

        private int AddNode(char value, int level)
        {
            symbol.Add(value);
            depth.Add(level);
            firstChild.Add(-1);
            nextSibling.Add(-1);
            fail.Add(0);
            output.Add(-1);
            dictionary.Add(-1);
            return symbol.Count - 1;
        }

        private static long Key(int node, char value)
        {
            return ((long)node << 16) | value;
        }
    }
}
