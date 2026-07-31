// スキル編集ダイアログ。player_data.skills の各スキル（辞書の値）を
// 生 JSON ではなく構造化したフォームで編集する。
//   SkillEditDialog  … スキル本体（名前/属性/種別/使用回数/有用度/説明 ＋ effects 一覧）
//   EffectEditDialog … effects の1要素。type に応じて入力欄を出し分け、
//                       text_status の effects_per_turn は同ダイアログで再帰編集する。
// どちらも入力を渡された JsonObject の複製に書き戻し、OK 時のみ ResultNode を返す（キャンセルで無変更）。
using System.Globalization;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class SkillEditDialog : Form
    {
        private readonly JsonObject _sk;                 // 編集対象（複製）。OK 時にこれへ書き戻す
        private readonly JsonArray _effects;             // _sk["effects"]（複製）。一覧操作で直接書き換え
        private readonly TextBox _name = new() { Dock = DockStyle.Fill };
        private readonly ComboBox _element = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox _type = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly TextBox _maxUses = new() { Dock = DockStyle.Fill };
        private readonly TextBox _curUses = new() { Dock = DockStyle.Fill };
        private readonly TextBox _useful = new() { Dock = DockStyle.Fill };
        private readonly TextBox _desc = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };
        private readonly ListBox _effList = new() { Dock = DockStyle.Fill };

        public JsonObject ResultNode { get; private set; }   // OK 時の確定スキル

        public SkillEditDialog(string title, JsonObject src)
        {
            Text = title; Width = 560; Height = 620; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;

            // 元を壊さないよう複製して編集する。effects は _sk から切り離して保持し、OK 時に付け直す。
            _sk = src != null ? (JsonObject)src.DeepClone() : new JsonObject();
            _effects = _sk["effects"] as JsonArray ?? new JsonArray();
            _sk.Remove("effects");

            // 候補は既定＋セーブから抽出した実値（SkillOptions）。編集可コンボなので未知値も保持できる。
            _element.Items.AddRange(SkillOptions.Get("element"));
            _type.Items.AddRange(SkillOptions.Get("skill_type"));

            // 初期値を流し込む。
            _name.Text = J.Str(_sk, "name");
            _element.Text = J.Str(_sk, "element");
            _type.Text = J.Str(_sk, "skill_type", "physical");
            _maxUses.Text = J.Int(_sk, "max_uses").ToString();
            _curUses.Text = J.Int(_sk, "current_uses").ToString();
            _useful.Text = J.Int(_sk, "usefulness").ToString();
            _desc.Text = LineEnds.ToBox(J.Str(_sk, "description"));
            RefreshEffects();

            // --- レイアウト ---
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            AddRow(grid, ref row, I18n.T("skill.field.name"), _name);
            AddRow(grid, ref row, I18n.T("skill.field.element"), _element);
            AddRow(grid, ref row, I18n.T("skill.field.type"), _type);
            AddRow(grid, ref row, I18n.T("skill.field.maxUses"), _maxUses);
            AddRow(grid, ref row, I18n.T("skill.field.currentUses"), _curUses);
            AddRow(grid, ref row, I18n.T("skill.field.usefulness"), _useful);

            // 説明（複数行）と効果一覧は2列ぶち抜き。効果一覧の行を伸縮させる。
            FullRow(grid, ref row, I18n.T("skill.field.description"), _desc, 90);
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = I18n.T("skill.field.effects"), AutoSize = true, Padding = new Padding(2, 8, 0, 2) }, 0, row);
            grid.SetColumnSpan(grid.GetControlFromPosition(0, row), 2); row++;

            var effPanel = new Panel { Dock = DockStyle.Fill };
            var effBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            effBar.Controls.Add(MakeBtn(I18n.T("btn.add"), AddEffect));
            effBar.Controls.Add(MakeBtn(I18n.T("btn.edit"), EditEffect));
            effBar.Controls.Add(MakeBtn(I18n.T("btn.delete"), DeleteEffect));
            _effList.DoubleClick += (_, _) => EditEffect();
            effPanel.Controls.Add(_effList); effPanel.Controls.Add(effBar);
            grid.Controls.Add(effPanel, 0, row);
            grid.SetColumnSpan(effPanel, 2);
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.SetRow(effPanel, row);

            Controls.Add(grid);
            Controls.Add(BuildOkCancel());
        }

        // OK/キャンセルのボタン列を作る。OK で値を検証して書き戻す。
        private Panel BuildOkCancel()
        {
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                if (!TryInt(_maxUses, "skill.field.maxUses", out long mx)) return;
                if (!TryInt(_curUses, "skill.field.currentUses", out long cu)) return;
                if (!TryInt(_useful, "skill.field.usefulness", out long us)) return;
                _sk["name"] = _name.Text;
                _sk["description"] = LineEnds.FromBox(_desc.Text);
                _sk["max_uses"] = mx;
                _sk["current_uses"] = cu;
                _sk["element"] = _element.Text;
                _sk["skill_type"] = _type.Text;
                // usefulness は player_data のスキルにしか無い（実データの NPC スキル 458 件は
                // 全て未所持）。無い相手に足すと実データに無い形になるため、元からある場合だけ書く。
                if (_sk.ContainsKey("usefulness")) _sk["usefulness"] = us;
                _sk["effects"] = _effects;   // 切り離していた配列を付け直す
                ResultNode = _sk;
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            return bar;
        }

        // ---- effects 一覧操作（_effects を直接書き換え）----
        private void RefreshEffects()
        {
            _effList.Items.Clear();
            foreach (var n in _effects) _effList.Items.Add(EffectEditDialog.Summary(n as JsonObject));
        }
        private void AddEffect()
        {
            using var d = new EffectEditDialog(I18n.T("effect.addTitle"), null);
            if (d.ShowDialog(this) == DialogResult.OK) { _effects.Add(d.ResultNode); RefreshEffects(); }
        }
        private void EditEffect()
        {
            int i = _effList.SelectedIndex; if (i < 0 || i >= _effects.Count) return;
            using var d = new EffectEditDialog(I18n.T("effect.editTitle"), _effects[i] as JsonObject);
            if (d.ShowDialog(this) == DialogResult.OK) { _effects[i] = d.ResultNode; RefreshEffects(); }
        }
        private void DeleteEffect()
        {
            int i = _effList.SelectedIndex; if (i < 0 || i >= _effects.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _effects.RemoveAt(i); RefreshEffects(); }
        }

        // ---- 小ヘルパ（EffectEditDialog と共有）----
        // ラベル(左)＋フィールド(右)の1行を追加する。
        internal static void AddRow(TableLayoutPanel t, ref int row, string label, Control field)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(2, 6, 4, 0) }, 0, row);
            t.Controls.Add(field, 1, row); row++;
        }
        // ラベル＋2列ぶち抜きフィールド（説明欄など）。
        internal static void FullRow(TableLayoutPanel t, ref int row, string label, Control field, int height)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(2, 8, 0, 2) }, 0, row);
            t.SetColumnSpan(t.GetControlFromPosition(0, row), 2); row++;
            field.Height = height;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(field, 0, row);
            t.SetColumnSpan(field, 2); row++;
        }
        internal static Button MakeBtn(string text, Action act)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(2) };
            b.Click += (_, _) => act();
            return b;
        }
        // 整数欄の検証。失敗時はエラー表示して false。ロケール非依存で解釈する。
        internal static bool TryInt(TextBox tb, string labelKey, out long v)
        {
            if (long.TryParse(tb.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return true;
            MessageBox.Show(I18n.T("msg.typeError", I18n.T(labelKey), I18n.T("type.integer")), I18n.T("title.typeError"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    // effects の1要素を編集する。type を選ぶと関係する欄だけ表示する。
    internal sealed class EffectEditDialog : Form
    {
        // power（威力）欄を持つ type。escape_from_battle も power を持つ（実データに準拠）。
        private static readonly string[] PowerTypes = { "instant_damage", "instant_heal", "buff", "debuff", "escape_from_battle" };

        private readonly JsonObject _ef;
        private readonly JsonArray _perTurn;             // text_status の effects_per_turn（複製）
        private readonly ComboBox _type = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ComboBox _target = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox _power = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly ComboBox _attr = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly TextBox _duration = new() { Dock = DockStyle.Fill };
        private readonly TextBox _statusName = new() { Dock = DockStyle.Fill };
        private readonly TextBox _intensity = new() { Dock = DockStyle.Fill };
        private readonly TextBox _desc = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };
        private readonly ListBox _perTurnList = new() { Dock = DockStyle.Fill };
        // type ごとに表示する行コンテナ（ラベル＋フィールドを1つにまとめた入れ物）と、表示する type 集合。
        private readonly List<(Control row, string[] types)> _rows = new();

        public JsonObject ResultNode { get; private set; }

        public EffectEditDialog(string title, JsonObject src)
        {
            Text = title; Width = 520; Height = 600; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            _ef = src != null ? (JsonObject)src.DeepClone() : new JsonObject { ["type"] = "instant_damage", ["target_type"] = "all_opponents", ["power"] = "normal" };
            // _ef から切り離して保持する（OK 時に別オブジェクトへ付け替えるため、親を持たせない）。
            _perTurn = _ef["effects_per_turn"] as JsonArray ?? new JsonArray();
            _ef.Remove("effects_per_turn");

            // 候補は既定＋セーブから抽出した実値（SkillOptions）。
            _type.Items.AddRange(SkillOptions.Get("effect.type"));
            EnsureItem(_type, J.Str(_ef, "type", "instant_damage"));
            _target.Items.AddRange(SkillOptions.Get("target_type"));
            _power.Items.AddRange(SkillOptions.Get("power"));
            _attr.Items.AddRange(SkillOptions.Get("attribute_type"));

            _type.Text = J.Str(_ef, "type", "instant_damage");
            _target.Text = J.Str(_ef, "target_type");
            _power.Text = J.Str(_ef, "power", "normal");
            _attr.Text = J.Str(_ef, "attribute_type");
            _duration.Text = J.Int(_ef, "duration").ToString();
            _statusName.Text = J.Str(_ef, "status_name");
            _intensity.Text = J.Int(_ef, "intensity").ToString();
            _desc.Text = LineEnds.ToBox(J.Str(_ef, "description"));
            RefreshPerTurn();

            // 各行は「ラベル＋フィールドを1つにまとめたコンテナ」を1セルに置く。表示切替はコンテナ単位で行うため、
            // 個別コントロールの Visible 切替で起きる行の折りたたみズレが発生しない。
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.type"), _type), null);
            // target_type はどの type でも持つため常に表示（null=切替対象外）。
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.target"), _target), null);
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.power"), _power), PowerTypes);
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.attribute"), _attr), new[] { "buff", "debuff" });
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.duration"), _duration), new[] { "buff", "debuff", "text_status" });
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.statusName"), _statusName), new[] { "text_status" });
            AddRow(grid, ref row, FieldRow(I18n.T("effect.field.intensity"), _intensity), new[] { "text_status" });

            // 説明（text_status のみ）。ラベルの下に複数行欄を積む。
            _desc.Height = 70;
            AddRow(grid, ref row, StackRow(I18n.T("effect.field.description"), _desc), new[] { "text_status" });

            // 毎ターン効果（text_status のみ）。一覧＋追加/編集/削除。
            var ptPanel = new Panel { Dock = DockStyle.Top, Height = 120 };
            var ptBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            ptBar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.add"), AddPerTurn));
            ptBar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.edit"), EditPerTurn));
            ptBar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.delete"), DeletePerTurn));
            _perTurnList.DoubleClick += (_, _) => EditPerTurn();
            ptPanel.Controls.Add(_perTurnList); ptPanel.Controls.Add(ptBar);
            AddRow(grid, ref row, StackRow(I18n.T("effect.field.perTurn"), ptPanel), new[] { "text_status" });

            Controls.Add(grid);
            Controls.Add(BuildOkCancel());

            _type.SelectedIndexChanged += (_, _) => UpdateVisibility();
            UpdateVisibility();
        }

        // コンテナを1セル（1列）に積む。types 指定時は type による表示切替対象として登録する。
        private void AddRow(TableLayoutPanel grid, ref int row, Control container, string[] types)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(container, 0, row); row++;
            if (types != null) _rows.Add((container, types));
        }

        // ラベル(左)＋フィールド(右)を横並びにした行コンテナ。
        private static Control FieldRow(string label, Control field)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(2, 6, 4, 0) }, 0, 0);
            field.Dock = DockStyle.Fill;
            t.Controls.Add(field, 1, 0);
            return t;
        }

        // ラベル(上)＋フィールド(下)を縦並びにした行コンテナ（説明欄・毎ターン効果など）。
        private static Control StackRow(string label, Control field)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, Margin = new Padding(0, 1, 0, 1) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(2, 8, 0, 2) }, 0, 0);
            field.Dock = DockStyle.Top;
            t.Controls.Add(field, 0, 1);
            return t;
        }

        // type に該当しない行コンテナを隠す（コンテナ単位なのでラベルとフィールドがずれない）。
        private void UpdateVisibility()
        {
            string t = _type.Text;
            foreach (var (rowCtrl, types) in _rows)
                rowCtrl.Visible = Array.IndexOf(types, t) >= 0;
        }

        private Panel BuildOkCancel()
        {
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                // type に応じた欄だけを書き出した新しい effect を組み立てる。
                string t = _type.Text;
                var o = new JsonObject { ["type"] = t, ["target_type"] = _target.Text };
                if (Array.IndexOf(PowerTypes, t) >= 0)
                    o["power"] = _power.Text;
                if (t is "buff" or "debuff")
                {
                    o["attribute_type"] = _attr.Text;
                    if (!SkillEditDialog.TryInt(_duration, "effect.field.duration", out long du)) return;
                    o["duration"] = du;
                }
                if (t == "text_status")
                {
                    o["status_name"] = _statusName.Text;
                    o["description"] = LineEnds.FromBox(_desc.Text);
                    if (!SkillEditDialog.TryInt(_duration, "effect.field.duration", out long du)) return;
                    o["duration"] = du;
                    if (!SkillEditDialog.TryInt(_intensity, "effect.field.intensity", out long it)) return;
                    o["intensity"] = it;
                    o["effects_per_turn"] = _perTurn;
                }
                ResultNode = o;
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            return bar;
        }

        // ---- effects_per_turn 一覧（同ダイアログで再帰編集）----
        private void RefreshPerTurn()
        {
            _perTurnList.Items.Clear();
            foreach (var n in _perTurn) _perTurnList.Items.Add(Summary(n as JsonObject));
        }
        private void AddPerTurn()
        {
            using var d = new EffectEditDialog(I18n.T("effect.addTitle"), null);
            if (d.ShowDialog(this) == DialogResult.OK) { _perTurn.Add(d.ResultNode); RefreshPerTurn(); }
        }
        private void EditPerTurn()
        {
            int i = _perTurnList.SelectedIndex; if (i < 0 || i >= _perTurn.Count) return;
            using var d = new EffectEditDialog(I18n.T("effect.editTitle"), _perTurn[i] as JsonObject);
            if (d.ShowDialog(this) == DialogResult.OK) { _perTurn[i] = d.ResultNode; RefreshPerTurn(); }
        }
        private void DeletePerTurn()
        {
            int i = _perTurnList.SelectedIndex; if (i < 0 || i >= _perTurn.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _perTurn.RemoveAt(i); RefreshPerTurn(); }
        }

        // 一覧表示用の要約文字列（type / 対象 / 威力 など）。
        internal static string Summary(JsonObject e)
        {
            if (e == null) return "null";
            string t = J.Str(e, "type");
            string s = t + "  " + J.Str(e, "target_type");
            string status = J.Str(e, "status_name");
            if (!string.IsNullOrEmpty(status)) return s + "  [" + status + "]";
            string power = J.Str(e, "power");
            if (!string.IsNullOrEmpty(power)) return s + "  " + power;
            return s;
        }

        // コンボに現在値が無ければ追加して選択可能にする（未知の type を保持するため）。
        private static void EnsureItem(ComboBox cb, string val)
        {
            if (!string.IsNullOrEmpty(val) && !cb.Items.Contains(val)) cb.Items.Add(val);
        }
    }

    // スキル辞書（スキル名をキーにした辞書）を一覧＋追加/編集/削除で編集する共通コントロール。
    // 辞書を直接書き換えるため Apply 不要（即反映）。プレイヤー(PlayerTab)と NPC(ObjectForm)で共用する。
    // 追加/編集では SkillEditDialog を開き、スキル名（name）と辞書キーを一致させる（衝突時は元キー維持）。
    internal sealed class SkillListPanel : Panel
    {
        private readonly ListBox _list = new() { Dock = DockStyle.Top, Height = 110 };
        private readonly List<string> _keys = new();   // 一覧の行 → 辞書キーの対応
        private JsonObject _skills;

        public SkillListPanel()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            void Mk(string text, Action act)
            {
                var b = new Button { Text = text, Width = 70, Margin = new Padding(2) };
                b.Click += (_, _) => act();
                bar.Controls.Add(b);
            }
            Mk(I18n.T("btn.add"), AddSkill);
            Mk(I18n.T("btn.edit"), EditSkill);
            Mk(I18n.T("btn.delete"), DeleteSkill);
            _list.DoubleClick += (_, _) => EditSkill();

            // Dock=Top は後入れが上。下から: 一覧 → ボタン列。
            Controls.Add(_list);
            Controls.Add(bar);
        }

        // スキル辞書をバインドして一覧を作り直す。
        public void Bind(JsonObject skills)
        {
            _skills = skills;
            RefreshList();
        }

        // skills 辞書から一覧を作り直す（名前・属性・回数を表示）。行→キー対応も作る。
        private void RefreshList()
        {
            _list.Items.Clear(); _keys.Clear();
            if (_skills == null) return;
            foreach (var kv in _skills)
            {
                _keys.Add(kv.Key);
                _list.Items.Add(kv.Value is JsonObject o
                    ? I18n.T("player.skillItem", J.Str(o, "name", kv.Key), J.Str(o, "element"), J.Int(o, "current_uses"), J.Int(o, "max_uses"))
                    : kv.Key);
            }
        }

        // 新規スキルを専用フォームで作成し、名前をキーにして辞書へ追加する（重複名は連番回避）。
        private void AddSkill()
        {
            if (_skills == null) return;
            using var d = new SkillEditDialog(I18n.T("skill.addTitle"), null);
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            string baseName = J.Str(d.ResultNode, "name", I18n.T("player.newSkill"));
            if (string.IsNullOrEmpty(baseName)) baseName = I18n.T("player.newSkill");
            string key = baseName;
            for (int n = 2; _skills.ContainsKey(key); n++) key = baseName + n;
            d.ResultNode["name"] = key;   // キーと name を一致させる
            _skills[key] = d.ResultNode;
            RefreshList();
        }

        // 選択スキルを専用フォームで編集。name を変更したらキーも追従する（衝突時は元キー維持）。
        private void EditSkill()
        {
            int i = _list.SelectedIndex;
            if (_skills == null || i < 0 || i >= _keys.Count) return;
            string key = _keys[i];
            if (_skills[key] is not JsonObject cur) return;
            using var d = new SkillEditDialog(I18n.T("skill.editTitle"), cur);
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            string newKey = J.Str(d.ResultNode, "name", key);
            if (string.IsNullOrEmpty(newKey) || (newKey != key && _skills.ContainsKey(newKey))) newKey = key;
            d.ResultNode["name"] = newKey;
            if (newKey == key)
            {
                _skills[key] = d.ResultNode;   // 同一キーへの置換は辞書内の位置を保つ（保存 round-trip を崩さない）
            }
            else
            {
                // 改名時もキーの位置を保つ: key より後ろのエントリを一旦退避し、改名後に入れ直す。
                var tail = _skills.Select(p => p.Key).SkipWhile(k => k != key).Skip(1).ToList();
                var moved = tail.Select(k => (k, _skills[k])).ToList();
                foreach (var k in tail) _skills.Remove(k);
                _skills.Remove(key);
                _skills[newKey] = d.ResultNode;
                foreach (var (k, v) in moved) _skills[k] = v;
            }
            RefreshList();
        }

        private void DeleteSkill()
        {
            int i = _list.SelectedIndex;
            if (_skills == null || i < 0 || i >= _keys.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _skills.Remove(_keys[i]); RefreshList(); }
        }
    }
}
