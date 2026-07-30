// free 施設プログラム（free_facility_programs の 1 件）を ObjectForm 内で構造化編集するパネル。
//   FreeProgramStepsPanel … steps（シナリオ命令列）の一覧＋追加/複製/削除/並べ替え/JSON編集
//   FreeProgramRatePanel  … prices / payouts（キー→{unit, mult}）の行編集
// どちらも対象の JsonArray / JsonObject を即時に書き換える（ObjectForm.Apply() の対象外）。
// 命令 1 件の中身は型ごとに形が違うため、骨格を選んで挿入し詳細は JSON 編集で詰める方式にしている。
using System.Globalization;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // steps の一覧編集パネル。行番号付きで要約を並べ、順序が意味を持つため上下移動を用意する。
    internal sealed class FreeProgramStepsPanel : Panel
    {
        private readonly ListBox _list = new() { Dock = DockStyle.Top, Height = 300, IntegralHeight = false };
        private JsonArray _model;

        public FreeProgramStepsPanel()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.add"), Add));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("quest.btnJsonEdit"), EditJson));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.duplicate"), Duplicate));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.delete"), Delete));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("freeprog.moveUp"), () => MoveStep(-1)));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("freeprog.moveDown"), () => MoveStep(1)));
            _list.DoubleClick += (_, _) => EditJson();
            // Dock=Top は後入れが上。下から: 一覧 → ボタン列。
            Controls.Add(_list);
            Controls.Add(bar);
        }

        public void Bind(JsonArray model) { _model = model; RefreshList(); }

        // 選択位置を保ったまま一覧を作り直す（行番号は label/goto の対応を追うのに使うため必ず出す）。
        private void RefreshList(int select = -1)
        {
            int keep = select >= 0 ? select : _list.SelectedIndex;
            _list.BeginUpdate();
            _list.Items.Clear();
            if (_model != null)
                for (int i = 0; i < _model.Count; i++)
                    _list.Items.Add($"{i,3}  {FreeFacilityProgram.StepSummary(_model[i])}");
            _list.EndUpdate();
            if (keep >= 0 && keep < _list.Items.Count) _list.SelectedIndex = keep;
        }

        // 命令型を選んで骨格を挿入する。選択行があればその直後、無ければ末尾へ入れる。
        private void Add()
        {
            if (_model == null) return;
            var types = FreeFacilityProgram.StepTypes;
            var previews = types.Select(t => FreeFacilityProgram.Skeleton(t).ToJsonString(Codec.Pretty)).ToList();
            int t2 = ListPicker.PickOne(FindForm(), I18n.T("freeprog.pickType"), types.ToList(), previews);
            if (t2 < 0) return;
            int at = _list.SelectedIndex >= 0 ? _list.SelectedIndex + 1 : _model.Count;
            _model.Insert(at, FreeFacilityProgram.Skeleton(types[t2]));
            RefreshList(at);
        }

        private void Duplicate()
        {
            int i = _list.SelectedIndex;
            if (_model == null || i < 0 || i >= _model.Count) return;
            _model.Insert(i + 1, _model[i]?.DeepClone());
            RefreshList(i + 1);
        }

        private void Delete()
        {
            int i = _list.SelectedIndex;
            if (_model == null || i < 0 || i >= _model.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _model.RemoveAt(i);
            RefreshList(Math.Min(i, _model.Count - 1));
        }

        // 選択行を delta 分だけ移動する（範囲外は無視）。
        private void MoveStep(int delta)
        {
            int i = _list.SelectedIndex;
            if (_model == null || i < 0 || i >= _model.Count) return;
            int j = i + delta;
            if (j < 0 || j >= _model.Count) return;
            var node = _model[i]?.DeepClone();
            _model.RemoveAt(i);
            _model.Insert(j, node);
            RefreshList(j);
        }

        private void EditJson()
        {
            int i = _list.SelectedIndex;
            if (_model == null || i < 0 || i >= _model.Count) return;
            using var d = new JsonEditDialog(I18n.T("freeprog.editStep", i), _model[i]);
            if (d.ShowDialog(FindForm()) != DialogResult.OK || d.ResultNode == null) return;
            _model[i] = d.ResultNode;
            RefreshList(i);
        }
    }

    // prices / payouts（キー→{unit, mult}）の行編集パネル。キーは steps の文字列内で
    // {price.<キー>} として参照されるため、名前も編集できるようにしてある。
    internal sealed class FreeProgramRatePanel : Panel
    {
        private readonly TableLayoutPanel _rows = new()
        { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private JsonObject _model;

        public FreeProgramRatePanel()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));  // キー
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));  // unit
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));   // mult
            _rows.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));   // 削除
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.add"), Add));
            // Dock=Top は後入れが上。下から: 行テーブル → ボタン列。
            Controls.Add(_rows);
            Controls.Add(bar);
        }

        public void Bind(JsonObject model) { _model = model; Rebuild(); }

        // 行テーブルを作り直す。キー名の変更は辞書の作り替えになるため、確定ごとに全体を再構築する。
        private void Rebuild()
        {
            _rows.Controls.Clear();
            _rows.RowStyles.Clear();
            if (_model == null) return;
            int r = 0;
            _rows.Controls.Add(Head(I18n.T("freeprog.rateKey")), 0, r);
            _rows.Controls.Add(Head(I18n.T("freeprog.rateUnit")), 1, r);
            _rows.Controls.Add(Head(I18n.T("freeprog.rateMult")), 2, r);
            r++;
            foreach (var key in _model.Select(p => p.Key).ToList())
            {
                var val = _model[key] as JsonObject ?? new JsonObject();
                _rows.Controls.Add(KeyBox(key), 0, r);
                _rows.Controls.Add(UnitBox(val), 1, r);
                _rows.Controls.Add(MultBox(val), 2, r);
                string captured = key;
                _rows.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.delete"), () =>
                {
                    _model.Remove(captured);
                    Rebuild();
                }), 3, r);
                r++;
            }
        }

        private static Label Head(string s)
            => new() { Text = s, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(2, 4, 4, 0) };

        // キー名の変更。空・重複は元に戻す。並び順を保つため辞書を作り直して差し替える。
        private Control KeyBox(string key)
        {
            var tb = new TextBox { Text = key, Dock = DockStyle.Fill };
            tb.Leave += (_, _) =>
            {
                string nk = tb.Text.Trim();
                if (nk == key) return;
                if (nk.Length == 0 || _model.ContainsKey(nk)) { tb.Text = key; return; }
                // 値ノードは親を持ったままでは別の辞書へ入れられないため、クローンを取ってから入れ替える。
                var kept = _model.Select(p => (Key: p.Key == key ? nk : p.Key, Val: p.Value?.DeepClone())).ToList();
                _model.Clear();
                foreach (var (k, v) in kept) _model[k] = v;
                Rebuild();
            };
            return tb;
        }

        private Control UnitBox(JsonObject val)
        {
            var tb = new TextBox { Text = J.Str(val, "unit"), Dock = DockStyle.Fill };
            tb.Leave += (_, _) => val["unit"] = tb.Text.Trim();
            return tb;
        }

        // mult は倍率（1.0 等の小数）。数値として解釈できないときは赤くして書き戻さない。
        private Control MultBox(JsonObject val)
        {
            double cur = J.Dbl(val, "mult", 1.0);
            var tb = new TextBox { Text = cur.ToString(CultureInfo.InvariantCulture), Dock = DockStyle.Fill };
            tb.Leave += (_, _) =>
            {
                if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                { val["mult"] = d; tb.BackColor = SystemColors.Window; }
                else tb.BackColor = Color.MistyRose;
            };
            return tb;
        }

        // 新しい行（unit 未設定・倍率 1.0）を追加する。キー名は重複しない連番で仮置きする。
        private void Add()
        {
            if (_model == null) return;
            string key = "new";
            for (int i = 2; _model.ContainsKey(key); i++) key = "new" + i;
            _model[key] = new JsonObject { ["unit"] = "low", ["mult"] = 1.0 };
            Rebuild();
        }
    }
}
