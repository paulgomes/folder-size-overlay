using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PastaGBIcons
{
    internal sealed class PerformanceDialog : Form
    {
        private readonly RuntimeMetrics metrics;
        private readonly Func<string> extra;
        private readonly TextBox report;
        private readonly Timer refresh;

        internal PerformanceDialog(RuntimeMetrics metrics, Func<string> extra)
        {
            this.metrics = metrics; this.extra = extra;
            Text = "Pasta GB · Desempenho";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(720, 430); MinimumSize = new Size(500, 320);
            Font = SystemFonts.MessageBoxFont;
            report = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill, WordWrap = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var close = new Button { Text = "Fechar", AutoSize = true, DialogResult = DialogResult.Cancel };
            var export = new Button { Text = "Exportar CSV…", AutoSize = true };
            export.Click += delegate { Export(); };
            buttons.Controls.Add(close); buttons.Controls.Add(export);
            Controls.Add(report); Controls.Add(buttons); CancelButton = close;
            refresh = new Timer { Interval = 2000 };
            refresh.Tick += delegate { RefreshReport(); };
            RefreshReport(); refresh.Start();
        }

        private void RefreshReport()
        {
            int selection = report.SelectionStart;
            report.Text = metrics.Summary + Environment.NewLine + (extra == null ? String.Empty : extra());
            report.SelectionStart = Math.Min(selection, report.TextLength);
        }

        private void Export()
        {
            using (var dialog = new SaveFileDialog { Title = "Exportar consumo do processo principal", Filter = "CSV (*.csv)|*.csv",
                FileName = "PastaGB-desempenho-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".csv", OverwritePrompt = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { File.WriteAllText(dialog.FileName, metrics.ExportCsv(), new UTF8Encoding(true)); }
                catch (Exception ex) { MessageBox.Show(this, "Não foi possível exportar.\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        protected override void Dispose(bool disposing)
        { if (disposing && refresh != null) refresh.Dispose(); base.Dispose(disposing); }
    }
}
