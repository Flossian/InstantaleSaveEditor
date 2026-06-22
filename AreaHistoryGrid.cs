// area_history（エリア履歴）をグリッドで表示・編集する共通コントロール。
// player_data の area_history は
//   { "<エリアID>": { residency:{ total_days:int, last_stay_end:int }, achievements:[string,...], lawfulness:int }, ... }
// というエリアIDをキーとする辞書。行の追加・削除はせず、既存エリアの値のみ編集する。
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // area_history を DataGridView で編集する。数値3列はセル直接編集、
    // achievements（長文配列）はダブルクリックで複数行ダイアログ（1行＝1実績）を開いて編集する。
    // エリア列は areas から名前を解決して表示する読み取り専用の参照列。
    internal sealed class AreaHistoryGrid : ResizableGridPanel
    {
        private const int ColArea = 0, ColTotalDays = 1, ColLastStayEnd = 2, ColLawfulness = 3, ColAchievements = 4;

        public AreaHistoryGrid(int height = 250)
        {
            Height = height;
            _grid.RowTemplate.Height = 28;
            BuildColumns();
            _grid.CellDoubleClick += OnCellDoubleClick;

            // 下端のグリップ。上下にドラッグしてグリッド全体の高さを変える。
            var grip = CreateGrip();

            // 追加順: Fill を先に、Bottom を後に。最後に追加した grip が最下端を確保する。
            Controls.Add(_grid);   // Fill
            Controls.Add(grip);    // Bottom（最下端）
        }

        // エリア(参照・読み取り専用) + 数値3列 + achievements(残り幅・読み取り専用/ダイアログ編集)。
        private void BuildColumns()
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.T("areahist.col.area"), ReadOnly = true, FillWeight = 40 });
            _grid.Columns.Add(IntCol(I18n.T("areahist.col.totalDays")));
            _grid.Columns.Add(IntCol(I18n.T("areahist.col.lastStayEnd")));
            _grid.Columns.Add(IntCol(I18n.T("areahist.col.lawfulness")));
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = I18n.T("areahist.col.achievements"),
                ReadOnly = true,
                FillWeight = 60,
            });
        }

        // achievements セルのダブルクリックで実績リスト編集ダイアログを開く（1実績＝1項目）。
        private void OnCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != ColAchievements) return;
            var cell = _grid.Rows[e.RowIndex].Cells[ColAchievements];
            var list = cell.Tag as List<string> ?? new List<string>();
            using var d = new AchievementsDialog(list);
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            cell.Tag = d.Result;
            cell.Value = AchSummary(d.Result);
        }

        private static string AchSummary(List<string> list) => I18n.T("areahist.countSuffix", list.Count);

        // area_history 辞書を読み込んで行を作る。areas からエリア名を解決して表示する。
        // 元オブジェクトは行 Tag に複製して保持し、未知のフィールドを書き戻し時に温存する。
        public void Bind(JsonObject hist, JsonObject areas)
        {
            _grid.Rows.Clear();
            if (hist == null) return;
            foreach (var kv in hist.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
            {
                var o = kv.Value as JsonObject ?? new JsonObject();
                var res = J.Obj(o, "residency");
                string name = areas?[kv.Key] is JsonObject ao ? J.Str(ao, "name", I18n.T("label.unknown")) : I18n.T("label.unknown");
                var list = new List<string>();
                foreach (var a in J.Arr(o, "achievements") ?? new JsonArray()) list.Add(a?.ToString() ?? "");

                int idx = _grid.Rows.Add(
                    $"{kv.Key}: {name}",
                    J.Int(res, "total_days").ToString(),
                    J.Int(res, "last_stay_end").ToString(),
                    J.Int(o, "lawfulness").ToString(),
                    AchSummary(list));
                var row = _grid.Rows[idx];
                row.Cells[ColArea].Tag = kv.Key;            // エリアID（書き戻しのキー）
                row.Cells[ColAchievements].Tag = list;      // 実績文字列リスト
                row.Tag = o.DeepClone().AsObject();         // 未知フィールド温存用の元コピー
            }
        }

        // 現在の行内容から area_history 辞書を作り直す。元の residency 他フィールドは温存し、
        // total_days / last_stay_end / lawfulness / achievements のみ上書きする。
        public JsonObject ToObject()
        {
            var obj = new JsonObject();
            foreach (DataGridViewRow r in _grid.Rows)
            {
                if (r.Cells[ColArea].Tag is not string key) continue;
                var entry = (r.Tag as JsonObject)?.DeepClone().AsObject() ?? new JsonObject();

                var res = J.Obj(entry, "residency");
                if (res == null) { res = new JsonObject(); entry["residency"] = res; }
                res["total_days"] = ParseLong(r, ColTotalDays);
                res["last_stay_end"] = ParseLong(r, ColLastStayEnd);
                entry["lawfulness"] = ParseLong(r, ColLawfulness);

                var arr = new JsonArray();
                foreach (var s in r.Cells[ColAchievements].Tag as List<string> ?? new List<string>()) arr.Add(s);
                entry["achievements"] = arr;

                obj[key] = entry;
            }
            return obj;
        }
    }

    // 実績(achievements)のリストを編集するダイアログ。1実績＝1行のグリッドで表示し、
    // 追加・編集・削除を個別に行う。本文の編集は複数行テキストダイアログを使う。
    internal sealed class AchievementsDialog : Form
    {
        private readonly DataGridView _grid = new()
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            ColumnHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };

        // OK 確定時の編集結果（空項目は除外済み）。
        public List<string> Result =>
            _grid.Rows.Cast<DataGridViewRow>()
                .Select(r => r.Cells[0].Value?.ToString() ?? "")
                .Where(s => s.Trim().Length > 0).ToList();

        public AchievementsDialog(List<string> items)
        {
            Text = I18n.T("areahist.editAchievements");
            Width = 720; Height = 480; StartPosition = FormStartPosition.CenterParent;

            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                ReadOnly = true,
                FillWeight = 100,
                DefaultCellStyle = { WrapMode = DataGridViewTriState.True },
            });
            foreach (var s in items) _grid.Rows.Add(s);
            _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRow(e.RowIndex); };

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 4, 0, 4) };
            var add = new Button { Text = I18n.T("btn.add"), Width = 80 };
            var edit = new Button { Text = I18n.T("btn.edit"), Width = 80 };
            var del = new Button { Text = I18n.T("btn.delete"), Width = 80 };
            add.Click += (_, _) =>
            {
                using var d = new TextEditDialog(I18n.T("areahist.addAchievement"), "");
                if (d.ShowDialog(this) == DialogResult.OK && d.Value.Trim().Length > 0)
                {
                    int i = _grid.Rows.Add(d.Value.Trim());
                    _grid.ClearSelection(); _grid.Rows[i].Selected = true;
                }
            };
            edit.Click += (_, _) => { if (_grid.CurrentRow != null) EditRow(_grid.CurrentRow.Index); };
            del.Click += (_, _) =>
            {
                if (_grid.CurrentRow == null) return;
                if (MessageBox.Show(I18n.T("msg.confirmDeleteAchievement"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes)
                    _grid.Rows.Remove(_grid.CurrentRow);
            };
            bar.Controls.Add(add); bar.Controls.Add(edit); bar.Controls.Add(del);

            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
            var btnBar = new Panel { Dock = DockStyle.Bottom, Height = 40 };
            btnBar.Controls.Add(ok); btnBar.Controls.Add(cancel);

            // 追加順: Fill(_grid) を先に、Top/Bottom を後に。
            Controls.Add(_grid);
            Controls.Add(bar);
            Controls.Add(btnBar);
            AcceptButton = ok; CancelButton = cancel;
        }

        // 指定行の本文を複数行ダイアログで編集する。
        private void EditRow(int index)
        {
            var cell = _grid.Rows[index].Cells[0];
            using var d = new TextEditDialog(I18n.T("areahist.editAchievement"), cell.Value?.ToString() ?? "");
            if (d.ShowDialog(this) == DialogResult.OK) cell.Value = d.Value.Trim();
        }
    }
}
