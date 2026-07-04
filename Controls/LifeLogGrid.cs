// life_log（生涯ログ）をグリッドで表示・編集する共通コントロール。
// player_data と npcs の life_log はどちらも
//   [{ day_start:int, day_end:int, summarized_count:int, content:string }, ...]
// という同一構造。プレイヤータブと NPC(ObjectForm) の双方から使う。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // life_log 配列を DataGridView で編集する。日付3列はセル直接編集、
    // content（長文）はダブルクリックで複数行ダイアログを開いて編集する。
    internal sealed class LifeLogGrid : ResizableGridPanel
    {
        private const int ColDayStart = 0, ColDayEnd = 1, ColCount = 2, ColContent = 3;

        public LifeLogGrid(int height = 280)
        {
            Height = height;
            _grid.RowTemplate.Height = 46;   // content の折り返しを2行程度見せる
            BuildColumns();
            _grid.CellDoubleClick += OnCellDoubleClick;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(0, 4, 0, 0) };
            var add = new Button { Text = I18n.T("btn.add"), Width = 70 };
            var del = new Button { Text = I18n.T("btn.delete"), Width = 70 };
            add.Click += (_, _) => { int i = AddRow(0, 0, 0, ""); if (i >= 0) { _grid.ClearSelection(); _grid.Rows[i].Selected = true; } };
            del.Click += (_, _) => DeleteSelected();
            bar.Controls.Add(add); bar.Controls.Add(del);

            // 下端のグリップ。上下にドラッグしてグリッド全体の高さを変える。
            var grip = CreateGrip();

            // 追加順: Fill を先に、Bottom 系を後に。最後に追加した grip が最下端を確保する。
            Controls.Add(_grid);   // Fill
            Controls.Add(bar);     // Bottom（grip の上）
            Controls.Add(grip);    // Bottom（最下端）
        }

        // 日付3列(固定幅寄り) + content(残り幅・読み取り専用/ダイアログ編集)。
        private void BuildColumns()
        {
            _grid.Columns.Add(IntCol(I18n.T("lifelog.col.dayStart"), 18));
            _grid.Columns.Add(IntCol(I18n.T("lifelog.col.dayEnd"), 18));
            _grid.Columns.Add(IntCol(I18n.T("lifelog.col.count"), 18));
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = I18n.T("lifelog.col.content"),
                ReadOnly = true,
                FillWeight = 110,
                DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
            });
        }

        // content セルのダブルクリックで複数行ダイアログを開く。
        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColContent) return;
            var cell = _grid.Rows[e.RowIndex].Cells[ColContent];
            using var d = new TextEditDialog(I18n.T("lifelog.editContent"), cell.Value?.ToString() ?? "");
            if (d.ShowDialog(FindForm()) == DialogResult.OK) cell.Value = d.Value;
        }

        private int AddRow(long ds, long de, long sc, string content)
            => _grid.Rows.Add(ds.ToString(), de.ToString(), sc.ToString(), content);

        private void DeleteSelected()
        {
            if (_grid.CurrentRow == null) return;
            if (MessageBox.Show(I18n.T("msg.confirmDeleteRow"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _grid.Rows.Remove(_grid.CurrentRow);
        }

        // life_log 配列を読み込んで行を作る（null は空表示）。
        public void Bind(JsonArray arr)
        {
            _grid.Rows.Clear();
            if (arr == null) return;
            foreach (var n in arr)
            {
                var o = n as JsonObject;
                AddRow(J.Int(o, "day_start"), J.Int(o, "day_end"), J.Int(o, "summarized_count"), J.Str(o, "content"));
            }
        }

        // 現在の行内容から life_log 配列を作り直す。日付列が不正なら 0 とみなす。
        public JsonArray ToArray()
        {
            var arr = new JsonArray();
            foreach (DataGridViewRow r in _grid.Rows)
                arr.Add(new JsonObject
                {
                    ["day_start"] = ParseLong(r, ColDayStart),
                    ["day_end"] = ParseLong(r, ColDayEnd),
                    ["summarized_count"] = ParseLong(r, ColCount),
                    ["content"] = r.Cells[ColContent].Value?.ToString() ?? "",
                });
            return arr;
        }
    }

    // 単純な複数行テキスト編集ダイアログ。content のような長文の編集に使う。
    internal sealed class TextEditDialog : Form
    {
        private readonly TextBox _t;
        public string Value => _t.Text;

        public TextEditDialog(string title, string value)
        {
            Text = title; Width = 680; Height = 480; StartPosition = FormStartPosition.CenterParent;
            _t = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                WordWrap = true,
                Dock = DockStyle.Fill,
                AcceptsReturn = true,
                Text = value,
            };
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            Controls.Add(_t); Controls.Add(bar);
            AcceptButton = ok; CancelButton = cancel;
        }
    }
}
