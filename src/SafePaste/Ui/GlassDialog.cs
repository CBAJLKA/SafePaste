using System;
using System.Drawing;
using System.Windows.Forms;

namespace SafePaste.Ui
{
    internal enum AlertKind
    {
        Info,
        Warning,
        Error,
        Question
    }

    /// <summary>Сообщение в оформлении приложения вместо стандартного окна Windows.</summary>
    internal sealed class GlassDialog : GlassForm
    {
        private readonly AlertKind kind;
        private readonly string title;
        private readonly string message;
        private readonly GlassButton primary;
        private readonly GlassButton secondary;
        private readonly GlassCheck check;
        private RectangleF iconBounds;
        private RectangleF titleBounds;
        private RectangleF messageBounds;

        internal GlassDialog(AlertKind kind, string title, string message, string primaryText, string secondaryText,
            string checkText, bool destructive)
        {
            this.kind = kind;
            this.title = title;
            this.message = message;
            Text = "SafePaste";
            Resizable = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            MinimizeBox = false;
            MaximizeBox = false;

            primary = new GlassButton(null, primaryText, destructive ? ButtonKind.Danger : ButtonKind.Primary, null);
            primary.DialogResult = DialogResult.OK;
            Controls.Add(primary);
            if (!string.IsNullOrEmpty(secondaryText))
            {
                secondary = new GlassButton(null, secondaryText, ButtonKind.Glass, null);
                secondary.DialogResult = DialogResult.Cancel;
                Controls.Add(secondary);
            }
            if (!string.IsNullOrEmpty(checkText))
            {
                check = new GlassCheck(checkText);
                Controls.Add(check);
            }
            // Разрушительное действие не должно срабатывать от случайного Enter.
            AcceptButton = destructive && secondary != null ? secondary : primary;
            CancelButton = secondary ?? primary;
            Arrange();
        }

        internal bool Checked
        {
            get { return check != null && check.Checked; }
        }

        internal string Message
        {
            get { return message; }
        }

        private void Arrange()
        {
            float pad = Dpi.F(22);
            float icon = Dpi.F(40);
            float width = Dpi.F(430);
            float textLeft = pad + icon + Dpi.F(16);
            float textWidth = width - textLeft - pad;
            SizeF titleSize = Theme.Measure(title, Theme.TitleFont, textWidth);
            SizeF messageSize = string.IsNullOrEmpty(message) ? SizeF.Empty : Theme.Measure(message, Theme.UiFont, textWidth);
            iconBounds = new RectangleF(pad, pad, icon, icon);
            float y = pad + Math.Max(0f, (icon - titleSize.Height) / 2f - Dpi.F(8));
            titleBounds = new RectangleF(textLeft, y, textWidth + Dpi.F(2), titleSize.Height + Dpi.F(2));
            y += titleSize.Height + Dpi.F(6);
            messageBounds = new RectangleF(textLeft, y, textWidth + Dpi.F(2), messageSize.Height + Dpi.F(2));
            y += messageSize.Height;
            y = Math.Max(y, pad + icon);
            if (check != null)
            {
                y += Dpi.F(14);
                check.SetBounds((int)textLeft, (int)y, (int)textWidth, Dpi.S(26));
                y += Dpi.F(26);
            }
            y += Dpi.F(22);
            int buttonHeight = Dpi.S(36);
            int right = (int)(width - pad);
            int primaryWidth = Math.Max(Dpi.S(112), primary.PreferredWidth(buttonHeight));
            primary.SetBounds(right - primaryWidth, (int)y, primaryWidth, buttonHeight);
            if (secondary != null)
            {
                int secondaryWidth = Math.Max(Dpi.S(112), secondary.PreferredWidth(buttonHeight));
                secondary.SetBounds(primary.Left - Dpi.S(10) - secondaryWidth, (int)y, secondaryWidth, buttonHeight);
            }
            ClientSize = new Size((int)width, (int)(y + buttonHeight + pad));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            IButtonControl focus = AcceptButton;
            Control target = focus as Control;
            if (target != null)
            {
                target.Focus();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            Theme.PrepareText(graphics);
            Color tone;
            string glyph;
            switch (kind)
            {
                case AlertKind.Error:
                    tone = Theme.Danger;
                    glyph = Glyphs.Error;
                    break;
                case AlertKind.Warning:
                    tone = Theme.Warm;
                    glyph = Glyphs.Warning;
                    break;
                case AlertKind.Question:
                    tone = Theme.Accent;
                    glyph = Glyphs.Question;
                    break;
                default:
                    tone = Theme.Accent;
                    glyph = Glyphs.Info;
                    break;
            }
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(40, tone)))
            {
                graphics.FillEllipse(halo, iconBounds);
            }
            Theme.DrawGlyph(graphics, glyph, Theme.LargeIconFont, tone, iconBounds);
            Theme.DrawText(graphics, title, Theme.TitleFont, Theme.Text, titleBounds,
                StringAlignment.Near, StringAlignment.Near, true);
            Theme.DrawText(graphics, message, Theme.UiFont, Theme.Secondary, messageBounds,
                StringAlignment.Near, StringAlignment.Near, true);
        }
    }

    /// <summary>Замена MessageBox: те же сценарии, но в оформлении SafePaste.</summary>
    internal static class Alerts
    {
        internal static void Show(IWin32Window owner, AlertKind kind, string title, string message)
        {
            using (GlassDialog dialog = new GlassDialog(kind, title, message, "Понятно", null, null, false))
            {
                Run(owner, dialog);
            }
        }

        internal static bool Confirm(IWin32Window owner, AlertKind kind, string title, string message,
            string yes, string no, bool destructive)
        {
            using (GlassDialog dialog = new GlassDialog(kind, title, message, yes, no, null, destructive))
            {
                return Run(owner, dialog) == DialogResult.OK;
            }
        }

        /// <summary>Сообщение с флажком вроде «Больше не показывать».</summary>
        internal static bool ShowWithCheck(IWin32Window owner, AlertKind kind, string title, string message, string checkText)
        {
            using (GlassDialog dialog = new GlassDialog(kind, title, message, "Понятно", null, checkText, false))
            {
                Run(owner, dialog);
                return dialog.Checked;
            }
        }

        /// <summary>Последняя попытка сообщить об ошибке, когда своё окно показать не удалось.</summary>
        internal static void ShowSafe(IWin32Window owner, AlertKind kind, string title, string message)
        {
            try
            {
                Show(owner, kind, title, message);
            }
            catch (Exception)
            {
                MessageBox.Show(title + "\r\n\r\n" + message, "SafePaste", MessageBoxButtons.OK,
                    kind == AlertKind.Error ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
            }
        }

        private static DialogResult Run(IWin32Window owner, GlassDialog dialog)
        {
            if (owner == null)
            {
                // Сообщения при запуске появляются без окна-владельца: их нельзя потерять за другими окнами.
                dialog.StartPosition = FormStartPosition.CenterScreen;
                dialog.ShowInTaskbar = true;
                dialog.TopMost = true;
                return dialog.ShowDialog();
            }
            dialog.StartPosition = FormStartPosition.CenterParent;
            return dialog.ShowDialog(owner);
        }
    }
}
