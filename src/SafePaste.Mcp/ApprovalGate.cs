using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using SafePaste.Ui;

namespace SafePaste.Mcp
{
    internal interface IApprovalGate
    {
        bool Approve(string kind, string title, string details);

        /// <summary>Агент отключился: открытые окна закрываются, новые запросы отклоняются.</summary>
        void Cancel();
    }

    /// <summary>Показывает окно подтверждения. 0: отклонено, 1: разрешено, 2: разрешено на сессию.</summary>
    internal delegate int ApprovalPrompt(string title, string details, WaitHandle cancel);

    internal sealed class ApprovalGate : IApprovalGate
    {
        internal const int TimeoutMs = 120000;
        private readonly HashSet<string> session = new HashSet<string>(StringComparer.Ordinal);
        private readonly ManualResetEvent cancelled = new ManualResetEvent(false);
        private readonly ApprovalPrompt prompt;

        internal ApprovalGate() : this(null) { }

        internal ApprovalGate(ApprovalPrompt prompt)
        {
            this.prompt = prompt ?? ShowOnOwnThread;
        }

        public bool Approve(string kind, string title, string details)
        {
            lock (session)
            {
                if (session.Contains(kind)) return true;
            }
            if (cancelled.WaitOne(0)) return false;
            int answer;
            try { answer = prompt(title, details, cancelled); }
            catch (Exception) { answer = 0; }
            if (answer == 2)
            {
                lock (session) session.Add(kind);
            }
            return answer != 0;
        }

        public void Cancel()
        {
            cancelled.Set();
        }

        /// <summary>Окно в своём потоке: так подтверждение работает и без трея.</summary>
        internal static int ShowOnOwnThread(string title, string details, WaitHandle cancel)
        {
            int answer = 0;
            ApprovalForm shown = null;
            ManualResetEvent done = new ManualResetEvent(false);
            Thread thread = new Thread(delegate()
            {
                try
                {
                    Application.EnableVisualStyles();
                    using (ApprovalForm form = new ApprovalForm(title, details))
                    {
                        shown = form;
                        form.ShowDialog();
                        answer = form.Decision;
                    }
                }
                catch (Exception) { answer = 0; }
                finally { done.Set(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (WaitHandle.WaitAny(new WaitHandle[] { done, cancel }, TimeoutMs) == 0) return answer;
            // Нет ответа за 2 минуты или агент ушёл: окно закрывается, это отказ.
            ApprovalForm open = shown;
            if (open != null)
            {
                try { open.BeginInvoke((MethodInvoker)delegate { open.Close(); }); }
                catch (Exception) { }
            }
            done.WaitOne(5000);
            return 0;
        }
    }

    internal sealed class ApprovalForm : GlassForm
    {
        private readonly WindowHeader header;
        private readonly TextView content;
        private readonly GlassButton allow;
        private readonly GlassButton session;
        private readonly GlassButton reject;
        internal int Decision { get; private set; }

        internal ApprovalForm(string title, string details)
        {
            Text = "SafePaste: подтверждение";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            MinimizeBox = false;
            header = new WindowHeader(title, "Реальные значения видны только в этом окне");
            content = new TextView();
            content.SetContent(details, null, false);
            allow = new GlassButton(null, "Разрешить", ButtonKind.Primary, null);
            session = new GlassButton(null, "На эту сессию", ButtonKind.Glass, null);
            reject = new GlassButton(null, "Отклонить", ButtonKind.Glass, null);
            allow.Click += delegate { Decision = 1; Close(); };
            session.Click += delegate { Decision = 2; Close(); };
            reject.Click += delegate { Decision = 0; Close(); };
            Controls.Add(header); Controls.Add(content);
            Controls.Add(allow); Controls.Add(session); Controls.Add(reject);
            CancelButton = reject;
            ClientSize = new Size(Dpi.S(760), Dpi.S(520));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (header == null) return;
            int p = Dpi.S(18), h = Dpi.S(38), gap = Dpi.S(10);
            header.SetBounds(p, p, Width - p * 2, Dpi.S(64));
            content.SetBounds(p, Dpi.S(90), Width - p * 2, Height - Dpi.S(90) - h - p * 2);
            int y = Height - h - p;
            allow.SetBounds(Width - p - Dpi.S(126), y, Dpi.S(126), h);
            session.SetBounds(allow.Left - gap - Dpi.S(156), y, Dpi.S(156), h);
            reject.SetBounds(session.Left - gap - Dpi.S(126), y, Dpi.S(126), h);
        }
    }
}
