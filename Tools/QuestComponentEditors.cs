// クエスト部品（敵・ボス・イベント）のGUI編集ダイアログ。QuestCreator の「編集」ボタンから開く。
//   EnemyEditDialog      … 敵/ボス1体（type ＋ data の基本情報・特性・スキル一覧・ドロップ品一覧）
//   EnemySkillEditDialog … 敵スキル1件。effects は既存 EffectEditDialog を流用する
//                          （プレイヤースキルと違い current_uses / usefulness を持たないため SkillEditDialog とは別）
//   DropEditDialog       … ドロップ品1件（item_category は FieldOptions の item_type / item_sub_type.*（無ければ item_detail.*）を候補に使う）
//   EventEditDialog      … イベント1件（event_name / category / event_description）
// いずれも渡された JsonObject の複製へ書き戻し、OK 時のみ ResultNode を返す（キャンセルで無変更）。
// 既知フィールドだけを上書きするため、元データの未知キーは保持される。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class EnemyEditDialog : Form
    {
        // 既定候補（実データから抽出した基本セット）。コンボは編集可なので未知値も入力・保持できる。
        private static readonly string[] DefTypes = { "normal", "miniboss", "boss" };
        private static readonly string[] DefSizes = { "tiny", "small", "medium", "large", "huge" };
        private static readonly string[] DefAttrs =
        { "balanced", "striker", "tank", "scout", "mage", "sage", "fortress", "berserker", "battlemage", "blitzer", "charmer", "juggernaut", "pure_mage" };
        private static readonly string[] DefLookCats = { "monster", "human_male", "human_female" };

        private readonly JsonObject _enemy;              // 編集対象（複製）。OK 時にこれを ResultNode として返す
        private readonly JsonObject _data;               // _enemy["data"]（無ければ新設）
        private readonly JsonArray _skills, _drops;      // _data 内の配列を直接書き換える（複製内なのでキャンセル安全）
        private readonly ComboBox _type = MakeCombo(DefTypes);
        private readonly TextBox _name = new() { Dock = DockStyle.Fill };
        private readonly TextBox _desc = MakeMulti();
        private readonly ComboBox _race = MakeCombo(Array.Empty<string>());
        private readonly ComboBox _size = MakeCombo(DefSizes);
        private readonly ComboBox _attr = MakeCombo(DefAttrs);
        private readonly ComboBox _lookCat = MakeCombo(DefLookCats);
        private readonly TextBox _prompt = MakeMulti();
        private readonly TextBox _traitName = new() { Dock = DockStyle.Fill };
        private readonly TextBox _traitDesc = MakeMulti();
        private readonly ListBox _skillList = new() { Dock = DockStyle.Fill };
        private readonly ListBox _dropList = new() { Dock = DockStyle.Fill };

        public JsonObject ResultNode { get; private set; }

        // candidates はライブラリの敵/ボス一覧。race 等の候補値を実データから補う（null 可）。
        // defaultType は新規作成時（src=null）の type 初期値（ボス編集で "boss" を渡す）。
        public EnemyEditDialog(string title, JsonObject src, IEnumerable<JsonObject> candidates, string defaultType = "normal")
        {
            Text = title; Width = 640; Height = 900; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;

            _enemy = src != null ? (JsonObject)src.DeepClone() : new JsonObject();
            if (_enemy["data"] is not JsonObject) _enemy["data"] = new JsonObject();
            _data = (JsonObject)_enemy["data"];
            _skills = _data["skills"] as JsonArray; if (_skills == null) { _skills = new JsonArray(); _data["skills"] = _skills; }
            _drops = _data["drops"] as JsonArray; if (_drops == null) { _drops = new JsonArray(); _data["drops"] = _drops; }

            // 候補にライブラリの実値を補う。
            foreach (var c in candidates ?? Enumerable.Empty<JsonObject>())
            {
                AddUnique(_type, J.Str(c, "type"));
                var d = J.Obj(c, "data"); if (d == null) continue;
                AddUnique(_race, J.Str(d, "race"));
                AddUnique(_size, J.Str(d, "size"));
                AddUnique(_attr, J.Str(d, "attribute_type"));
                AddUnique(_lookCat, J.Str(J.Obj(d, "look"), "category"));
            }

            // 初期値を流し込む。
            var look = J.Obj(_data, "look");
            var trait = J.Obj(_data, "trait");
            _type.Text = J.Str(_enemy, "type", defaultType);
            _name.Text = J.Str(_data, "name");
            _desc.Text = J.Str(_data, "description");
            _race.Text = J.Str(_data, "race");
            _size.Text = J.Str(_data, "size", src == null ? "medium" : "");
            _attr.Text = J.Str(_data, "attribute_type", src == null ? "balanced" : "");
            _lookCat.Text = J.Str(look, "category", src == null ? "monster" : "");
            _prompt.Text = string.Join(Environment.NewLine,
                (J.Arr(look, "image_generation_prompt") ?? new JsonArray()).Select(n => n?.ToString() ?? ""));
            _traitName.Text = J.Str(trait, "name");
            _traitDesc.Text = J.Str(trait, "description");
            RefreshSkills();
            RefreshDrops();

            // --- レイアウト ---
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.type"), _type);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.name"), _name);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("enemy.field.description"), _desc, 70);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.race"), _race);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.size"), _size);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.attribute"), _attr);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.lookCategory"), _lookCat);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("enemy.field.prompt"), _prompt, 60);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("enemy.field.traitName"), _traitName);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("enemy.field.traitDesc"), _traitDesc, 50);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("enemy.field.skills"),
                ListWithBar(_skillList, AddSkill, EditSkill, DeleteSkill), 130);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("enemy.field.drops"),
                ListWithBar(_dropList, AddDrop, EditDrop, DeleteDrop), 130);

            Controls.Add(grid);
            Controls.Add(BuildOkCancel());
        }

        private Panel BuildOkCancel()
        {
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                _enemy["type"] = _type.Text;
                _data["name"] = _name.Text;
                _data["description"] = _desc.Text;
                var look = J.Obj(_data, "look"); if (look == null) { look = new JsonObject(); _data["look"] = look; }
                look["category"] = _lookCat.Text;
                var prompts = new JsonArray();
                foreach (var line in _prompt.Lines) { var s = line.Trim(); if (s.Length > 0) prompts.Add(s); }
                look["image_generation_prompt"] = prompts;
                _data["race"] = _race.Text;
                _data["size"] = _size.Text;
                _data["attribute_type"] = _attr.Text;
                var trait = J.Obj(_data, "trait"); if (trait == null) { trait = new JsonObject(); _data["trait"] = trait; }
                trait["name"] = _traitName.Text;
                trait["description"] = _traitDesc.Text;
                // skills / drops は _data 内の配列を直接編集済み。
                // ドロップは接尾辞付きの sub_type（"long_weapon" 等）だけ素の形へ直す
                // （ライブラリや手編集由来の値でもゲームが落ちないように）。
                foreach (var n in _drops)
                {
                    var cat = J.Obj(n as JsonObject, "item_category"); if (cat == null) continue;
                    string t = J.Str(cat, "type"), s = J.Str(cat, "sub_type");
                    string fixedSub = DropEditDialog.NormalizeSubType(t?.Trim(), s?.Trim());
                    if (!string.Equals(fixedSub, s, StringComparison.Ordinal)) cat["sub_type"] = fixedSub;
                }
                ResultNode = _enemy;
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            return bar;
        }

        // ---- skills 一覧操作 ----
        private void RefreshSkills()
        {
            _skillList.Items.Clear();
            foreach (var n in _skills)
            {
                var o = n as JsonObject;
                _skillList.Items.Add($"{J.Str(o, "name", I18n.T("label.unnamed"))}  [{J.Str(o, "element")}] ×{J.Int(o, "max_uses")}");
            }
        }
        private void AddSkill()
        {
            using var d = new EnemySkillEditDialog(I18n.T("skill.addTitle"), null);
            if (d.ShowDialog(this) == DialogResult.OK) { _skills.Add(d.ResultNode); RefreshSkills(); }
        }
        private void EditSkill()
        {
            int i = _skillList.SelectedIndex; if (i < 0 || i >= _skills.Count) return;
            using var d = new EnemySkillEditDialog(I18n.T("skill.editTitle"), _skills[i] as JsonObject);
            if (d.ShowDialog(this) == DialogResult.OK) { _skills[i] = d.ResultNode; RefreshSkills(); }
        }
        private void DeleteSkill()
        {
            int i = _skillList.SelectedIndex; if (i < 0 || i >= _skills.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _skills.RemoveAt(i); RefreshSkills(); }
        }

        // ---- drops 一覧操作 ----
        private void RefreshDrops()
        {
            _dropList.Items.Clear();
            foreach (var n in _drops)
            {
                var o = n as JsonObject;
                _dropList.Items.Add($"{J.Str(o, "item_name", I18n.T("label.unnamed"))}  [{J.Str(o, "rarity")}]");
            }
        }
        private void AddDrop()
        {
            using var d = new DropEditDialog(I18n.T("drop.addTitle"), null);
            if (d.ShowDialog(this) == DialogResult.OK) { _drops.Add(d.ResultNode); RefreshDrops(); }
        }
        private void EditDrop()
        {
            int i = _dropList.SelectedIndex; if (i < 0 || i >= _drops.Count) return;
            using var d = new DropEditDialog(I18n.T("drop.editTitle"), _drops[i] as JsonObject);
            if (d.ShowDialog(this) == DialogResult.OK) { _drops[i] = d.ResultNode; RefreshDrops(); }
        }
        private void DeleteDrop()
        {
            int i = _dropList.SelectedIndex; if (i < 0 || i >= _drops.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes)
            { _drops.RemoveAt(i); RefreshDrops(); }
        }

        // ---- 小ヘルパ（本ファイル内の各ダイアログで共有）----
        internal static ComboBox MakeCombo(string[] items)
        {
            var cb = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            cb.Items.AddRange(items);
            return cb;
        }
        internal static TextBox MakeMulti()
            => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill };
        internal static void AddUnique(ComboBox cb, string v)
        {
            if (!string.IsNullOrEmpty(v) && !cb.Items.Contains(v)) cb.Items.Add(v);
        }
        // 一覧＋追加/編集/削除ボタン列のパネル（ダブルクリックで編集）。
        private static Panel ListWithBar(ListBox list, Action add, Action edit, Action del)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.add"), add));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.edit"), edit));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.delete"), del));
            list.DoubleClick += (_, _) => edit();
            panel.Controls.Add(list); panel.Controls.Add(bar);
            return panel;
        }
    }

    // 敵スキル1件の編集。effects は EffectEditDialog を流用する。
    internal sealed class EnemySkillEditDialog : Form
    {
        private readonly JsonObject _sk;                 // 編集対象（複製）。OK 時にこれへ書き戻す
        private readonly JsonArray _effects;             // _sk["effects"]（複製内を直接書き換え）
        private readonly TextBox _name = new() { Dock = DockStyle.Fill };
        private readonly ComboBox _element = EnemyEditDialog.MakeCombo(Array.Empty<string>());
        private readonly ComboBox _type = EnemyEditDialog.MakeCombo(Array.Empty<string>());
        private readonly TextBox _maxUses = new() { Dock = DockStyle.Fill };
        private readonly TextBox _desc = EnemyEditDialog.MakeMulti();
        private readonly ListBox _effList = new() { Dock = DockStyle.Fill };

        public JsonObject ResultNode { get; private set; }

        public EnemySkillEditDialog(string title, JsonObject src)
        {
            Text = title; Width = 560; Height = 520; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;

            _sk = src != null ? (JsonObject)src.DeepClone() : new JsonObject();
            _effects = _sk["effects"] as JsonArray; if (_effects == null) { _effects = new JsonArray(); _sk["effects"] = _effects; }

            _element.Items.AddRange(SkillOptions.Get("element"));
            _type.Items.AddRange(SkillOptions.Get("skill_type"));

            _name.Text = J.Str(_sk, "name");
            _element.Text = J.Str(_sk, "element");
            _type.Text = J.Str(_sk, "skill_type", "physical");
            _maxUses.Text = J.Int(_sk, "max_uses", src == null ? 1 : 0).ToString();
            _desc.Text = J.Str(_sk, "description");
            RefreshEffects();

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            SkillEditDialog.AddRow(grid, ref row, I18n.T("skill.field.name"), _name);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("skill.field.element"), _element);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("skill.field.type"), _type);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("skill.field.maxUses"), _maxUses);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("skill.field.description"), _desc, 70);

            // 効果一覧（残り高さいっぱいに伸ばす）。
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { Text = I18n.T("skill.field.effects"), AutoSize = true, Padding = new Padding(2, 8, 0, 2) }, 0, row);
            grid.SetColumnSpan(grid.GetControlFromPosition(0, row), 2); row++;
            var effPanel = new Panel { Dock = DockStyle.Fill };
            var effBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            effBar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.add"), AddEffect));
            effBar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.edit"), EditEffect));
            effBar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.delete"), DeleteEffect));
            _effList.DoubleClick += (_, _) => EditEffect();
            effPanel.Controls.Add(_effList); effPanel.Controls.Add(effBar);
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.Controls.Add(effPanel, 0, row);
            grid.SetColumnSpan(effPanel, 2);

            Controls.Add(grid);
            Controls.Add(BuildOkCancel());
        }

        private Panel BuildOkCancel()
        {
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                if (!SkillEditDialog.TryInt(_maxUses, "skill.field.maxUses", out long mx)) return;
                _sk["name"] = _name.Text;
                _sk["description"] = _desc.Text;
                _sk["element"] = _element.Text;
                _sk["skill_type"] = _type.Text;
                _sk["max_uses"] = mx;
                ResultNode = _sk;
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            return bar;
        }

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
    }

    // ドロップ品1件の編集。item_category の候補は FieldOptions（item_type / item_sub_type.<type>）から供給する。
    internal sealed class DropEditDialog : Form
    {
        // rarity の候補（低→高）。ItemEditDialog と同じ並び。
        private static readonly string[] Rarities = { "common", "rare", "magical", "epic", "legendary", "mythic" };

        private readonly JsonObject _drop;               // 編集対象（複製）。OK 時にこれへ書き戻す
        private readonly TextBox _name = new() { Dock = DockStyle.Fill };
        private readonly TextBox _desc = EnemyEditDialog.MakeMulti();
        private readonly TextBox _appearance = EnemyEditDialog.MakeMulti();
        private readonly ComboBox _catType = EnemyEditDialog.MakeCombo(Array.Empty<string>());
        private readonly ComboBox _catSub = EnemyEditDialog.MakeCombo(Array.Empty<string>());
        private readonly ComboBox _rarity = EnemyEditDialog.MakeCombo(Rarities);
        private readonly TextBox _value = new() { Dock = DockStyle.Fill };

        public JsonObject ResultNode { get; private set; }

        public DropEditDialog(string title, JsonObject src)
        {
            Text = title; Width = 520; Height = 480; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;

            _drop = src != null ? (JsonObject)src.DeepClone() : new JsonObject();
            var cat = J.Obj(_drop, "item_category");

            _catType.Items.AddRange(FieldOptions.Get("item_type").ToArray());
            _catType.TextChanged += (_, _) => RefreshSubTypes();

            _name.Text = J.Str(_drop, "item_name");
            _desc.Text = J.Str(_drop, "description");
            _appearance.Text = J.Str(_drop, "item_appearance");
            _catType.Text = J.Str(cat, "type", src == null ? "material" : "");
            RefreshSubTypes();
            _catSub.Text = J.Str(cat, "sub_type");
            _rarity.Text = J.Str(_drop, "rarity", src == null ? "common" : "");
            _value.Text = J.Int(_drop, "value").ToString();

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            SkillEditDialog.AddRow(grid, ref row, I18n.T("drop.field.name"), _name);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("drop.field.description"), _desc, 60);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("drop.field.appearance"), _appearance, 60);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("drop.field.categoryType"), _catType);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("drop.field.categorySubType"), _catSub);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("drop.field.rarity"), _rarity);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("drop.field.value"), _value);

            Controls.Add(grid);
            Controls.Add(BuildOkCancel());
        }

        // sub_type の候補を現在の type 配下から作り直す。入力中の値は保持。
        // ドロップの sub_type はインベントリの item_detail と命名が違う種別がある
        // （weapon は item_detail が "small_weapon" だが sub_type は "small"。ゲームが
        //  Data\item_embeddings\{sub_type}_weapon.json を引くため接尾辞付きだと落ちる）。
        // そのため item_sub_type.<type> を優先し、無い種別だけ item_detail.<type> を流用する。
        private void RefreshSubTypes()
        {
            string cur = _catSub.Text;
            string type = _catType.Text.Trim();
            var opts = FieldOptions.Get("item_sub_type." + type);
            if (opts.Count == 0) opts = FieldOptions.Get("item_detail." + type);
            _catSub.Items.Clear();
            _catSub.Items.AddRange(opts.ToArray());
            _catSub.Text = cur;
        }

        // "_{type}" 付きの item_detail 名（例: type=weapon の "long_weapon"）を、剥がすと候補に一致する場合のみ
        // 素の sub_type（"long"）へ直す。ライブラリや手編集由来の接尾辞付きの値でゲームが落ちるのを防ぐ。
        // 候補に載っている値（material の "magical_material" 等）はそのまま。
        internal static string NormalizeSubType(string type, string sub)
        {
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(sub)) return sub;
            var opts = FieldOptions.Get("item_sub_type." + type);
            if (opts.Count == 0 || opts.Contains(sub, StringComparer.Ordinal)) return sub;
            string suffix = "_" + type;
            if (!sub.EndsWith(suffix, StringComparison.Ordinal)) return sub;
            string bare = sub[..^suffix.Length];
            return opts.Contains(bare, StringComparer.Ordinal) ? bare : sub;
        }

        private Panel BuildOkCancel()
        {
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                if (!SkillEditDialog.TryInt(_value, "drop.field.value", out long v)) return;
                _drop["item_name"] = _name.Text;
                _drop["description"] = _desc.Text;
                _drop["item_appearance"] = _appearance.Text;
                var cat = J.Obj(_drop, "item_category"); if (cat == null) { cat = new JsonObject(); _drop["item_category"] = cat; }
                cat["type"] = _catType.Text;
                cat["sub_type"] = NormalizeSubType(_catType.Text.Trim(), _catSub.Text.Trim());
                _drop["rarity"] = _rarity.Text;
                _drop["value"] = v;
                ResultNode = _drop;
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            return bar;
        }
    }

    // イベント1件の編集（event_name / category / event_description）。
    internal sealed class EventEditDialog : Form
    {
        private static readonly string[] Categories = { "benefit", "hazard", "neutral" };

        private readonly JsonObject _ev;                 // 編集対象（複製）。OK 時にこれへ書き戻す
        private readonly TextBox _name = new() { Dock = DockStyle.Fill };
        private readonly ComboBox _category = EnemyEditDialog.MakeCombo(Categories);
        private readonly TextBox _desc = EnemyEditDialog.MakeMulti();

        public JsonObject ResultNode { get; private set; }

        public EventEditDialog(string title, JsonObject src)
        {
            Text = title; Width = 520; Height = 320; StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;

            _ev = src != null ? (JsonObject)src.DeepClone() : new JsonObject();
            _name.Text = J.Str(_ev, "event_name");
            _category.Text = J.Str(_ev, "category", src == null ? "neutral" : "");
            _desc.Text = J.Str(_ev, "event_description");

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(8) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int row = 0;
            SkillEditDialog.AddRow(grid, ref row, I18n.T("event.field.name"), _name);
            SkillEditDialog.AddRow(grid, ref row, I18n.T("event.field.category"), _category);
            SkillEditDialog.FullRow(grid, ref row, I18n.T("event.field.description"), _desc, 100);

            Controls.Add(grid);
            Controls.Add(BuildOkCancel());
        }

        private Panel BuildOkCancel()
        {
            var ok = new Button { Text = I18n.T("btn.ok"), Dock = DockStyle.Right, Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Dock = DockStyle.Right, Width = 90 };
            ok.Click += (_, _) =>
            {
                _ev["event_name"] = _name.Text;
                _ev["category"] = _category.Text;
                _ev["event_description"] = _desc.Text;
                ResultNode = _ev;
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(8) };
            bar.Controls.Add(ok); bar.Controls.Add(cancel);
            return bar;
        }
    }
}
