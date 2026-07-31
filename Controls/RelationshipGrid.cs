// relationship（対象→関係データ）をグリッドで表示・編集する共通コントロール。
// relationship は
//   { "player": { affinity:int, affinity_text:str|[str], relationship:[str], conversation_count:int }, ... }
// という「対象キー→関係オブジェクト」の辞書。対象が増えても 1 行ずつ自動で増えるため拡張に強い。
// player は常に先頭に表示する。対象キーは TargetNamer で表示名へ解決する（NPC ID→名前など）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class RelationshipGrid : ResizableGridPanel
    {
        private const int ColTarget = 0, ColAffinity = 1, ColAffText = 2, ColRel = 3, ColConv = 4;

        // 行ごとに、元の対象キーと affinity_text が配列だったかを保持する（書き戻し時の型維持に使う）。
        private sealed class RowMeta { public string Key; public bool AffTextArray; }

        // 対象キー → 表示名の解決フック（player は内部で固定処理）。未設定/空ならキーをそのまま表示。
        public Func<string, string> TargetNamer { get; set; }

        public RelationshipGrid(int height = 220)
        {
            Height = height;
            _grid.RowTemplate.Height = 28;
            BuildColumns();

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 32, Padding = new Padding(0, 4, 0, 0) };
            var add = new Button { Text = I18n.T("btn.add"), Width = 70 };
            var del = new Button { Text = I18n.T("btn.delete"), Width = 70 };
            add.Click += (_, _) => AddNewTargetRow();
            del.Click += (_, _) => DeleteSelected();
            bar.Controls.Add(add); bar.Controls.Add(del);

            var grip = CreateGrip(100);

            Controls.Add(_grid);   // Fill
            Controls.Add(bar);     // Bottom（grip の上）
            Controls.Add(grip);    // Bottom（最下端）
        }

        // 対象 / 好感度 / 状態 / 関係 / 会話回数。状態・関係は「、」区切りで複数値を表示する。
        private void BuildColumns()
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.T("rel.col.target"), FillWeight = 22 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.T("rel.col.affinity"), FillWeight = 12 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.T("rel.col.affText"), FillWeight = 28 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.T("rel.col.relationship"), FillWeight = 26 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = I18n.T("rel.col.convCount"), FillWeight = 12 });
        }

        // relationship 辞書を読み込んで行を作る。player を常に先頭、以降はキー順。
        public void Bind(JsonObject rel)
        {
            _grid.Rows.Clear();
            if (rel == null) return;
            foreach (var key in OrderedKeys(rel))
            {
                if (rel[key] is not JsonObject target) continue;
                bool affArray = target["affinity_text"] is JsonArray;
                int i = _grid.Rows.Add(
                    ResolveName(key),
                    J.Int(target, "affinity").ToString(),
                    Join(target["affinity_text"]),
                    Join(target["relationship"]),
                    J.Int(target, "conversation_count").ToString());
                _grid.Rows[i].Tag = new RowMeta { Key = key, AffTextArray = affArray };
                _grid.Rows[i].Cells[ColTarget].ReadOnly = true;   // 既存の対象キーは固定（誤改名防止）
            }
        }

        // 現在の行内容から relationship 辞書を作り直す。player を常に先頭に出力する。
        public JsonObject ToObject()
        {
            var result = new JsonObject();
            var dropped = new List<string>();
            // player を先に書き、続けて残りを行順に書く。
            foreach (DataGridViewRow r in OrderedRows())
            {
                var meta = r.Tag as RowMeta;
                string key = (meta?.Key ?? r.Cells[ColTarget].Value?.ToString())?.Trim();
                if (string.IsNullOrEmpty(key)) continue;
                // 対象キーは辞書のキー。重複すると後の行が消えるため、黙って捨てずに知らせる。
                if (result.ContainsKey(key)) { dropped.Add(key); continue; }
                bool affArray = meta?.AffTextArray ?? true;   // 新規行は配列形式を既定とする
                string affText = r.Cells[ColAffText].Value?.ToString() ?? "";
                result[key] = new JsonObject
                {
                    ["affinity"] = ParseLong(r, ColAffinity),
                    ["affinity_text"] = affArray ? Split(affText) : affText,
                    ["relationship"] = Split(r.Cells[ColRel].Value?.ToString() ?? ""),
                    ["conversation_count"] = ParseLong(r, ColConv),
                };
            }
            if (dropped.Count > 0)
                MessageBox.Show(I18n.T("msg.relationshipDuplicate", string.Join(", ", dropped.Distinct())),
                    I18n.T("title.confirm"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return result;
        }

        // 新しい対象行を追加する（対象キーは手入力可能、affinity_text は配列既定）。
        private void AddNewTargetRow()
        {
            int i = _grid.Rows.Add("", "0", "", "", "0");
            _grid.Rows[i].Tag = new RowMeta { Key = null, AffTextArray = true };
            _grid.ClearSelection();
            _grid.Rows[i].Selected = true;
            _grid.CurrentCell = _grid.Rows[i].Cells[ColTarget];
        }

        private void DeleteSelected()
        {
            if (_grid.CurrentRow == null) return;
            if (MessageBox.Show(I18n.T("msg.confirmDeleteRelationship"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _grid.Rows.Remove(_grid.CurrentRow);
        }

        // player を先頭、以降はキー長→キー順で安定に並べる。
        private static IEnumerable<string> OrderedKeys(JsonObject rel)
        {
            if (rel.ContainsKey("player")) yield return "player";
            foreach (var k in rel.Select(p => p.Key).Where(k => k != "player").OrderBy(k => k.Length).ThenBy(k => k))
                yield return k;
        }

        // 出力用の行順: player の行を先頭にし、残りは表示順のまま。
        private IEnumerable<DataGridViewRow> OrderedRows()
        {
            var rows = _grid.Rows.Cast<DataGridViewRow>().ToList();
            foreach (var r in rows.Where(r => (r.Tag as RowMeta)?.Key == "player")) yield return r;
            foreach (var r in rows.Where(r => (r.Tag as RowMeta)?.Key != "player")) yield return r;
        }

        private string ResolveName(string key)
        {
            if (key == "player") return I18n.T("rel.player");
            string name = TargetNamer?.Invoke(key);
            return string.IsNullOrEmpty(name) ? key : $"{name} ({key})";
        }

        // 配列は「、」連結、文字列はそのまま、null は空文字。
        private static string Join(JsonNode n)
            => n is JsonArray a ? string.Join("、", a.Select(x => x?.ToString() ?? "")) : n?.ToString() ?? "";

        // 「、」「,」改行いずれかで区切って配列化する（空要素は除外）。
        private static JsonArray Split(string s)
        {
            var arr = new JsonArray();
            foreach (var part in s.Split(new[] { '、', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim();
                if (p.Length > 0) arr.Add(p);
            }
            return arr;
        }
    }
}
