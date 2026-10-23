using System;
using System.Drawing;
using System.Windows.Forms;

namespace PastaGBIcons
{
    internal sealed class PreferencesDialog : Form
    {
        private readonly ComboBox units = new ComboBox(), theme = new ComboBox(),
            scale = new ComboBox(), performance = new ComboBox();
        private readonly CheckBox follow = new CheckBox(), cross = new CheckBox();
        private readonly AppPreferences original;
        private static readonly float[] Scales = { .85f, 1f, 1.15f, 1.3f };

        internal PreferencesDialog(AppPreferences value)
        {
            original = value.Copy();
            Text = "Preferências · Pasta GB 6.0";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(500, 405);
            ShowInTaskbar = false;
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18),
                ColumnCount = 2, RowCount = 8 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Configure(units, new[] { "Unidade automática (B, KB, MB, GB, TB)", "Sempre em GB" }, (int)value.Units);
            Configure(theme, new[] { "Acompanhar o Windows", "Etiqueta clara", "Etiqueta escura" }, (int)value.Theme);
            Configure(performance, new[] { "Equilibrado · até 2 leituras", "Econômico · 1 leitura" }, (int)value.Performance);
            int scaleIndex = 1;
            for (int i = 0; i < Scales.Length; i++) if (Math.Abs(Scales[i] - value.TextScale) < .01f) scaleIndex = i;
            Configure(scale, new[] { "85%", "100%", "115%", "130%" }, scaleIndex);
            AddRow(table, 0, "Tamanhos", units);
            AddRow(table, 1, "Aparência", theme);
            AddRow(table, 2, "Escala do texto", scale);
            AddRow(table, 3, "Desempenho", performance);
            follow.Text = "Seguir junções e links de pastas locais";
            follow.Checked = value.FollowDirectoryLinks; follow.AutoSize = true;
            cross.Text = "Incluir destinos em outras unidades locais";
            cross.Checked = value.CrossVolumeLinks; cross.AutoSize = true; cross.Enabled = follow.Checked;
            follow.CheckedChanged += delegate { cross.Enabled = follow.Checked; };
            table.Controls.Add(follow, 0, 4); table.SetColumnSpan(follow, 2);
            table.Controls.Add(cross, 0, 5); table.SetColumnSpan(cross, 2);
            var explanation = new Label { AutoSize = true, MaximumSize = new Size(455, 0),
                Margin = new Padding(0, 10, 0, 12),
                Text = "↻ Atualizando o último total conhecido.  * Soma parcial.\n" +
                    "Os tamanhos representam o conteúdo lógico. Ligações repetidas e ciclos são detectados; " +
                    "itens não lidos aparecem como parciais." };
            table.Controls.Add(explanation, 0, 6); table.SetColumnSpan(explanation, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var save = new Button { Text = "Salvar", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true };
            buttons.Controls.Add(save); buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 0, 7); table.SetColumnSpan(buttons, 2);
            AcceptButton = save; CancelButton = cancel;
            for (int row = 0; row < 6; row++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, row < 4 ? 40 : 30));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            Controls.Add(table);
        }

        private static void Configure(ComboBox control, string[] entries, int selected)
        { control.DropDownStyle = ComboBoxStyle.DropDownList; control.Dock = DockStyle.Fill;
            control.Items.AddRange(entries); control.SelectedIndex = selected; }
        private static void AddRow(TableLayoutPanel table, int row, string name, Control control)
        {
            table.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            table.Controls.Add(control, 1, row);
        }
        internal AppPreferences Result
        {
            get
            {
                AppPreferences result = original.Copy();
                result.Units = (SizeUnits)units.SelectedIndex; result.Theme = (AppearanceTheme)theme.SelectedIndex;
                result.Performance = (PerformanceMode)performance.SelectedIndex;
                result.TextScale = Scales[scale.SelectedIndex];
                result.FollowDirectoryLinks = follow.Checked; result.CrossVolumeLinks = cross.Checked;
                return result;
            }
        }
    }
}
