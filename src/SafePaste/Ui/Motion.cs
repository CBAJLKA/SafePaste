using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using SafePaste.Interop;

namespace SafePaste.Ui
{
    /// <summary>
    /// Плавно меняющееся число для анимации: подсветка при наведении, нажатие, поворот значка,
    /// ползунок выключателя. Все значения обновляет один общий таймер, и он работает, только пока
    /// что-то движется. Элемент, который ещё не показан, и выключенная в Windows анимация
    /// ставят значение на место сразу.
    /// </summary>
    internal sealed class Tween
    {
        private static readonly List<Tween> running = new List<Tween>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static readonly bool animations = Native.ClientAreaAnimation();
        private static Timer timer;

        private readonly Control owner;
        private float value;
        private float from;
        private float target;
        private long started;
        private int duration;

        internal Tween(Control owner, float value)
        {
            this.owner = owner;
            this.value = value;
            target = value;
        }

        internal float Value
        {
            get { return value; }
        }

        internal float Target
        {
            get { return target; }
        }

        /// <summary>Ведёт значение к next за milliseconds, быстро в начале и мягко в конце.</summary>
        internal void To(float next, int milliseconds)
        {
            if (next == target)
            {
                return;
            }
            target = next;
            if (milliseconds <= 0 || !animations || !owner.IsHandleCreated || !owner.Visible)
            {
                Snap(next);
                return;
            }
            from = value;
            started = clock.ElapsedMilliseconds;
            duration = milliseconds;
            if (!running.Contains(this))
            {
                running.Add(this);
            }
            if (timer == null)
            {
                timer = new Timer();
                timer.Interval = 15;
                timer.Tick += delegate { Step(); };
            }
            timer.Start();
        }

        /// <summary>Ставит значение сразу, без анимации.</summary>
        internal void Snap(float next)
        {
            running.Remove(this);
            value = next;
            target = next;
            if (!owner.IsDisposed)
            {
                owner.Invalidate();
            }
        }

        private static void Step()
        {
            long now = clock.ElapsedMilliseconds;
            for (int i = running.Count - 1; i >= 0; i--)
            {
                Tween tween = running[i];
                if (tween.owner.IsDisposed)
                {
                    running.RemoveAt(i);
                    continue;
                }
                float progress = Math.Min(1f, (now - tween.started) / (float)tween.duration);
                float eased = 1f - (1f - progress) * (1f - progress) * (1f - progress);
                tween.value = tween.from + (tween.target - tween.from) * eased;
                if (progress >= 1f)
                {
                    tween.value = tween.target;
                    running.RemoveAt(i);
                }
                tween.owner.Invalidate();
            }
            if (running.Count == 0)
            {
                timer.Stop();
            }
        }
    }
}
