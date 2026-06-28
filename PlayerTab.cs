// プレイヤータブ: player_data 専用の編集UI。
// 基本値 / 説明 / キャラクター画像 / 能力値 / 身体部位 / 特性 / インベントリ・装備 / 生涯ログ / その他(JSON編集)
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // 編集対象の構造定義（Spec 群）を先頭に置き、UI 生成と反映はそれらを回して行う。
    internal sealed class PlayerTab : UserControl
    {
        // 基本値の定義: (JSONキー, 表示名のi18nキー, 整数として扱うか)。整数欄は保存時に数値検証する。
        private static readonly (string key, string label, bool isInt)[] BasicSpec =
        {
            ("name","player.basic.name",false),
            ("category","player.basic.category",false),
            ("age","player.basic.age",true),
            ("experience_level","player.basic.level",true),
            ("experience_point","player.basic.exp",true),
            ("gold","player.basic.gold",true),
            ("current_hp","player.basic.hp",true),
            ("physical_integrity","player.basic.stamina",true),
            ("max_physical_integrity","player.basic.staminaMax",true),
            ("original_max_physical_integrity","player.basic.staminaBase",true),
            ("location","player.basic.locationId",false),
            ("current_area","player.basic.areaId",false),
            ("current_node","player.basic.nodeId",false),
        };
        // 能力値6項目: JSONキー。表示名は ability.<key>（Common.cs と共通）。
        private static readonly string[] AbilityKeys =
        { "strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma" };
        // 身体部位6つ: JSONキー。表示名は player.body.<key>。各部位は injury/stage/state/description を持つ。
        private static readonly string[] BodyKeys =
        { "head", "body", "arms", "legs", "organs", "sanity" };
        // ログ・記憶など構造が複雑な項目は専用UIを作らず JSON 編集ボタンで扱う。表示名は player.opaque.<key>。
        private static readonly string[] OpaqueKeys =
        { "memory", "current_log", "image_src", "story_achievements" };

        private JsonObject _pd;                                            // player_data 本体
        private JsonObject _areas;                                         // areas データ（エリアComboBox生成用）
        private string _worldDir;                                          // worlds/{スロット}/ のパス。画像フォルダ解決に使う
        private readonly NpcImagePanel _imagePanel = new();               // 顔画像＋立ち絵パネル（NPCタブと共通）
        private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly Dictionary<string, TextBox> _basic = new();       // 基本値の入力欄(キー→欄)
        private readonly Dictionary<string, ComboBox> _combos = new();    // ComboBox で表示する基本値欄
        private readonly Dictionary<string, TextBox> _abil = new();        // 能力値の入力欄(キー→欄)
        private DataGridView _bodyGrid;                                    // 身体部位の表
        private SkillListPanel _skillPanel;                               // スキル一覧（NPCと共通）
        private ListBox _traitList;                                       // 特性一覧
        private InventoryPanel _invPanel;                                 // インベントリ（グリッド＋追加/編集/削除。NPCと共通）
        private ComboBox _cbWeapon, _cbWearable;                          // 装備(アイテムID参照)
        private LifeLogGrid _lifeLog;                                      // 生涯ログ(life_log)のグリッド
        private AreaHistoryGrid _areaHistory;                             // エリア履歴(area_history)のグリッド

        // current_area=エリア / current_node=ノード / location=施設 をプルダウンで表示する。
        private static readonly HashSet<string> ComboKeys = new() { "current_area", "current_node", "location" };

        private JsonObject _root;       // 言語切替で再構築するため保持
        private string _filePath;       // 同上

        public PlayerTab()
        {
            Controls.Add(_host);
            // 言語切替で開いているタブを作り直して文言を反映する。Dispose で解除する。
            I18n.LanguageChanged += OnLanguageChanged;
        }

        // 言語切替時に同じデータで再バインドして文言を更新する。
        private void OnLanguageChanged() { if (_root != null) Bind(_root, _filePath); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= OnLanguageChanged;
            base.Dispose(disposing);
        }

        // player_data を読み込み、各セクションを縦に並べて表示する。player_data 無しなら案内のみ。
        public void Bind(JsonObject root, string filePath = null)
        {
            _root = root; _filePath = filePath;
            _areas = J.Obj(root, "areas");
            _pd = J.Obj(root, "player_data");
            _worldDir = WorldTab.ResolveWorldDir(filePath);
            _host.Controls.Clear(); _basic.Clear(); _abil.Clear(); _combos.Clear();
            if (_pd == null)
            {
                _host.Controls.Add(new Label { Text = I18n.T("msg.noPlayerData"), AutoSize = true, Padding = new Padding(12) });
                return;
            }

            // 1列レイアウト。各セクションを Dock=Top で横幅いっぱい（ウィンドウ幅に追従）に。
            var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true, Padding = new Padding(8) };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var sections = new Control[]
            {
                BuildBasic(), BuildDescriptions(), BuildImages(), BuildAbilities(), BuildBody(),
                BuildSkills(), BuildTraits(), BuildInventoryAndEquip(), BuildLifeLog(), BuildAreaHistory(), BuildOpaque(),
            };
            for (int i = 0; i < sections.Length; i++)
            {
                sections[i].Dock = DockStyle.Top;
                stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stack.Controls.Add(sections[i], 0, i);
            }

            _host.Controls.Add(stack);
            LinkAreaCombos();
        }

        // current_area の選択変更に追従して location(施設)・current_node(ノード)の候補を作り直す。
        // 変更後のエリアに無いIDを指していた場合は選択をクリアする。
        private void LinkAreaCombos()
        {
            if (!_combos.TryGetValue("current_area", out var areaCb)) return;
            areaCb.SelectedIndexChanged += (_, _) =>
            {
                string area = AreaComboHelper.ExtractId(areaCb.Text);
                if (_combos.TryGetValue("location", out var locCb))
                    RefillCombo(locCb, id => AreaComboHelper.FillFacilityItems(locCb, _areas, area, id));
                if (_combos.TryGetValue("current_node", out var nodeCb))
                    RefillCombo(nodeCb, _ => AreaComboHelper.FillNodeItems(nodeCb, _areas, area));
            };
        }

        // コンボの候補を fill(旧ID) で作り直し、旧IDが残っていれば "ID: 名前" に補完、無ければクリアする。
        private static void RefillCombo(ComboBox cb, Action<string> fill)
        {
            string id = AreaComboHelper.ExtractId(cb.Text);
            fill(id);
            string match = cb.Items.Cast<string>()
                .FirstOrDefault(it => AreaComboHelper.ExtractId(it) == id);
            cb.Text = match ?? "";
        }

        // 各ウィジェットの値を player_data に書き戻す（保存前に呼ばれる）。
        // 一覧系(特性/インベントリ)は操作時に即反映済みなのでここでは扱わない。型エラー時は false。
        public bool Apply()
        {
            if (_pd == null) return true;

            // 基本値: 整数項目はパース検証、それ以外は文字列としてそのまま。
            // ComboBox 欄は "ID: 名前" から ID 部分だけを取り出して保存する。
            foreach (var (key, _, isInt) in BasicSpec)
            {
                if (_combos.TryGetValue(key, out var cb))
                {
                    _pd[key] = AreaComboHelper.ExtractId(cb.Text);
                    continue;
                }
                if (!_basic.TryGetValue(key, out var tb)) continue;
                if (isInt)
                {
                    // 整数ならそのまま。小数が入っていたら小数点以下切り捨てで整数化。
                    // 数値として解釈できない（文字列等）場合のみ型エラー。
                    if (long.TryParse(tb.Text, out long lv)) _pd[key] = lv;
                    else if (double.TryParse(tb.Text, out double dv)) _pd[key] = (long)dv;
                    else return Fail(key, I18n.T("type.integer"));
                }
                else _pd[key] = tb.Text;
            }
            // 説明3項目（複数行）。
            foreach (var tb in new[] { "profile", "personality", "look_description" })
                _pd[tb] = _descs[tb].Text;

            // 基礎能力値（小数で保持）。無ければ作る。
            var oas = J.Obj(_pd, "original_ability_scores");
            if (oas == null) { oas = new JsonObject(); _pd["original_ability_scores"] = oas; }
            foreach (var key in AbilityKeys)
            {
                if (!double.TryParse(_abil[key].Text, out double dv)) return Fail(key, I18n.T("type.number"));
                oas[key] = dv;
            }

            // 身体部位: 表の各行を対応する部位オブジェクトへ反映（列1=損傷,2=段階,3=状態,4=説明）。
            var bp = J.Obj(_pd, "body_parts");
            if (bp != null)
                foreach (DataGridViewRow r in _bodyGrid.Rows)
                {
                    string part = r.Cells[0].Tag as string;   // 行に保持した内部キー
                    if (part == null || bp[part] is not JsonObject po) continue;
                    if (!long.TryParse(Cell(r, 1), out long inj)) return Fail(I18n.T("player.injuryOf", part), I18n.T("type.integer"));
                    po["injury"] = inj;
                    po["stage"] = Cell(r, 2);
                    po["state"] = Cell(r, 3);
                    po["description"] = Cell(r, 4);
                }

            // 装備: コンボの選択をアイテムID(なしは null)として反映。
            var eq = J.Obj(_pd, "equipments");
            if (eq == null) { eq = new JsonObject(); _pd["equipments"] = eq; }
            eq["weapon"] = ComboVal(_cbWeapon);
            eq["wearable"] = ComboVal(_cbWearable);

            // 生涯ログ: グリッドの行内容を life_log 配列へ反映。
            if (_lifeLog != null) _pd["life_log"] = _lifeLog.ToArray();

            // エリア履歴: グリッドの行内容を area_history 辞書へ反映。
            if (_areaHistory != null) _pd["area_history"] = _areaHistory.ToObject();

            return true;
        }

        private readonly Dictionary<string, TextBox> _descs = new();   // 説明欄(キー→欄)

        // ---------------- 各セクション ----------------
        private GroupBox BuildBasic()
        {
            var g = Group(I18n.T("player.group.basic"), 440);
            var t = Grid(2);
            int row = 0;
            foreach (var (key, label, _) in BasicSpec)
            {
                t.Controls.Add(L(I18n.T(label)), 0, row);
                if (ComboKeys.Contains(key))
                {
                    // current_area: エリア一覧 / current_node: 現在エリアのノード一覧 /
                    // location: 現在エリアの施設一覧(NPCのcurrent_locationと同様、入口/出口/区画は除外)。
                    string curVal = ValAsText(key);
                    string curArea = ValAsText("current_area");
                    var cb = key switch
                    {
                        "current_area" => AreaComboHelper.MakeAreaCombo(_areas, curVal),
                        "location" => AreaComboHelper.MakeFacilityCombo(_areas, curArea, curVal),
                        _ => AreaComboHelper.MakeNodeCombo(_areas, curArea, curVal),
                    };
                    cb.Dock = DockStyle.Fill;
                    t.Controls.Add(cb, 1, row); _combos[key] = cb;
                }
                else
                {
                    var tb = new TextBox { Dock = DockStyle.Fill, Text = ValAsText(key) };
                    t.Controls.Add(tb, 1, row); _basic[key] = tb;
                }
                row++;
            }
            g.Controls.Add(t);
            return g;
        }

        private GroupBox BuildDescriptions()
        {
            var g = new GroupBox { Text = I18n.T("player.group.descriptions"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(4), Padding = new Padding(8) };
            var t = Grid(1);
            int row = 0;
            foreach (var (key, label) in new[] { ("profile", "player.desc.profile"), ("personality", "player.desc.personality"), ("look_description", "player.desc.look") })
            {
                t.Controls.Add(L(I18n.T(label)), 0, row++);
                var rtb = new ResizableTextBox(520, 90) { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 8) };
                rtb.Box.Text = J.Str(_pd, key);
                t.Controls.Add(rtb, 0, row++); _descs[key] = rtb.Box;
            }
            g.Controls.Add(t);
            return g;
        }

        // キャラクター画像（顔＋立ち絵3種）。NPCタブと共通の NpcImagePanel を使う。
        // 画像は worlds/{スロット}/characters/{プレイヤー名}/ から読み込む。
        private GroupBox BuildImages()
        {
            var g = new GroupBox { Text = I18n.T("player.group.images"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 760, Margin = new Padding(4), Padding = new Padding(8) };
            _imagePanel.Dock = DockStyle.Top;
            g.Controls.Add(_imagePanel);
            string name = J.Str(_pd, "name");
            string charDir = _worldDir != null && !string.IsNullOrEmpty(name)
                ? Path.Combine(_worldDir, "characters", name)
                : null;
            _imagePanel.LoadImages(charDir != null && Directory.Exists(charDir) ? charDir : null);
            return g;
        }

        private GroupBox BuildAbilities()
        {
            var g = Group(I18n.T("player.group.abilities"), 230);
            var t = Grid(2);
            int row = 0;
            var oas = J.Obj(_pd, "original_ability_scores");
            foreach (var key in AbilityKeys)
            {
                t.Controls.Add(L($"{I18n.T("ability." + key)} ({key})"), 0, row);
                var tb = new TextBox { Width = 100, Text = J.Dbl(oas, key).ToString() };
                t.Controls.Add(tb, 1, row); _abil[key] = tb; row++;
            }
            g.Controls.Add(t);
            return g;
        }

        // 身体部位の表。6行固定（追加/削除不可）。1列目に内部キーを Tag で保持し読み取り専用にする。
        private GroupBox BuildBody()
        {
            var g = Group(I18n.T("player.group.body"), 250);
            _bodyGrid = new DataGridView
            {
                Dock = DockStyle.Top,
                Height = 200,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            _bodyGrid.Columns.Add(Col(I18n.T("player.bodyCol.part"), true));
            _bodyGrid.Columns.Add(Col(I18n.T("player.bodyCol.injury"), false));
            _bodyGrid.Columns.Add(Col(I18n.T("player.bodyCol.stage"), false));
            _bodyGrid.Columns.Add(Col(I18n.T("player.bodyCol.state"), false));
            _bodyGrid.Columns.Add(Col(I18n.T("player.bodyCol.description"), false));
            var bp = J.Obj(_pd, "body_parts");
            foreach (var key in BodyKeys)
            {
                var po = bp != null ? bp[key] as JsonObject : null;
                if (po == null) continue;
                int idx = _bodyGrid.Rows.Add(I18n.T("player.body." + key), J.Int(po, "injury").ToString(), J.Str(po, "stage"), J.Str(po, "state"), J.Str(po, "description"));
                _bodyGrid.Rows[idx].Cells[0].Tag = key;          // 内部キーを保持
                _bodyGrid.Rows[idx].Cells[0].ReadOnly = true;
            }
            g.Controls.Add(_bodyGrid);
            return g;
        }

        // スキル一覧。skills はスキル名をキーにした辞書。一覧＋追加/編集/削除は NPC と共通の
        // SkillListPanel に委譲する（辞書を直接書き換えるため別途 Apply 不要＝即反映）。
        private GroupBox BuildSkills()
        {
            var g = Group(I18n.T("player.group.skills"), 200);
            _skillPanel = new SkillListPanel { Dock = DockStyle.Top };
            _skillPanel.Bind(EnsureObj("skills"));   // 無ければ作る（追加操作の受け皿）
            g.Controls.Add(_skillPanel);
            return g;
        }

        // 特性一覧。追加は最小テンプレを足し、編集/削除は選択行に対して JSON ダイアログ等で行う。
        // 配列を直接書き換えるため別途 Apply 不要（即反映）。
        private GroupBox BuildTraits()
        {
            var g = Group(I18n.T("player.group.traits"), 190);
            _traitList = new ListBox { Dock = DockStyle.Top, Height = 100 };
            RefreshTraits();
            var bar = ButtonBar(
                (I18n.T("btn.add"), () => { EnsureArr("traits").Add(new JsonObject { ["name"] = I18n.T("player.newTrait"), ["description"] = "", ["severity"] = 0 }); RefreshTraits(); }
            ),
                (I18n.T("btn.edit"), () => EditListItem(_traitList, J.Arr(_pd, "traits"), RefreshTraits)),
                (I18n.T("btn.delete"), () => RemoveListItem(_traitList, J.Arr(_pd, "traits"), RefreshTraits)));
            g.Controls.Add(bar); g.Controls.Add(_traitList);
            return g;
        }

        // インベントリ（グリッド式）＋装備コンボ。
        // グリッドはゲーム同様の格子にアイテムを配置し、ドラッグで移動/入れ替え、ダブルクリックで編集する。
        // 追加/削除はグリッド外のボタンで行い（v1 スコープ外の機能を維持）、選択は単一クリックで行う。
        private GroupBox BuildInventoryAndEquip()
        {
            // 倉庫付き InventoryPanel と装備コンボの合計高は可変なので、中身に追従する AutoSize 枠にする
            // （固定高だと下端の装備コンボが見切れる）。
            var g = new GroupBox { Text = I18n.T("player.group.invEquip"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 760, Margin = new Padding(4), Padding = new Padding(8) };

            // インベントリは NPC と共通の InventoryPanel（グリッド＋追加/編集/削除）。
            // プレイヤーのみアイテム倉庫を併設する（NPC では無効）。
            // 変更時は装備コンボを作り直す（削除したアイテムを装備していた場合の追従）。
            _invPanel = new InventoryPanel(warehouseEnabled: true) { Dock = DockStyle.Top };
            _invPanel.InventoryChanged += () => RefreshEquipCombos();
            // 個別倉庫(item\{world}\{char})の場所決定にワールド名（_worldDir 末尾）とキャラ名（player_data.name）を渡す。
            string worldName = string.IsNullOrEmpty(_worldDir) ? null : Path.GetFileName(_worldDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _invPanel.Bind(EnsureObj("inventory"), worldName, J.Str(_pd, "name"));   // 無ければ作る（追加操作の受け皿）

            var eqPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            eqPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            eqPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            eqPanel.Controls.Add(L(I18n.T("player.equip.weapon")), 0, 0);
            _cbWeapon = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            eqPanel.Controls.Add(_cbWeapon, 1, 0);
            eqPanel.Controls.Add(L(I18n.T("player.equip.wearable")), 0, 1);
            _cbWearable = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
            eqPanel.Controls.Add(_cbWearable, 1, 1);
            RefreshEquipCombos();

            // Dock=Top は後から追加したものが上に来る（ResizableTextBox/LifeLogGrid と同規則）。
            g.Controls.Add(eqPanel);
            g.Controls.Add(_invPanel);
            return g;
        }

        // 生涯ログ(life_log)をグリッドで表示・編集する。各行は開始日/終了日/要約回数/内容。
        private GroupBox BuildLifeLog()
        {
            // グリップで高さを変えられるよう、枠はグリッド高に追従する AutoSize にする。
            var g = new GroupBox { Text = I18n.T("player.group.lifeLog"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 760, Margin = new Padding(4), Padding = new Padding(8) };
            _lifeLog = new LifeLogGrid(250) { Dock = DockStyle.Top };
            _lifeLog.Bind(J.Arr(_pd, "life_log"));
            g.Controls.Add(_lifeLog);
            return g;
        }

        private GroupBox BuildAreaHistory()
        {
            // グリップで高さを変えられるよう、枠はグリッド高に追従する AutoSize にする。
            var g = new GroupBox { Text = I18n.T("player.group.areaHistory"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 760, Margin = new Padding(4), Padding = new Padding(8) };
            _areaHistory = new AreaHistoryGrid(250) { Dock = DockStyle.Top };
            _areaHistory.Bind(J.Obj(_pd, "area_history"), _areas);
            g.Controls.Add(_areaHistory);
            return g;
        }

        // 構造が複雑な項目（記憶・ログ等）を JSON 編集ボタンとして並べる。押すと丸ごと JSON 編集。
        private GroupBox BuildOpaque()
        {
            var g = Group(I18n.T("player.group.opaque"), 120);
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4) };
            foreach (var key in OpaqueKeys)
            {
                string label = I18n.T("player.opaque." + key);
                var btn = new Button { Text = label, AutoSize = true, Margin = new Padding(4) };
                string k = key;
                btn.Click += (_, _) =>
                {
                    using var d = new JsonEditDialog(label, _pd.TryGetPropertyValue(k, out var cur) ? cur : null);
                    if (d.ShowDialog(this) == DialogResult.OK) _pd[k] = d.ResultNode;
                };
                flow.Controls.Add(btn);
            }
            g.Controls.Add(flow);
            return g;
        }

        // ---------------- リスト操作（一覧の再描画 / 追加・編集・削除） ----------------
        // 特性一覧を traits 配列から作り直す（名前と severity を表示）。
        private void RefreshTraits()
        {
            _traitList.Items.Clear();
            var arr = J.Arr(_pd, "traits");
            if (arr == null) return;
            foreach (var n in arr) _traitList.Items.Add(n is JsonObject o ? I18n.T("player.traitItem", J.Str(o, "name", I18n.T("label.unnamed")), J.Int(o, "severity")) : n?.ToString() ?? "null");
        }

        // 装備コンボの候補を作り直す。武器は item_type=weapon、防具は wearable のアイテムのみを候補にし、
        // 既存の装備値を選択状態にする（種別が一致しない現装備はデータ消失を防ぐため例外的に残す）。
        private void RefreshEquipCombos()
        {
            var inv = J.Obj(_pd, "inventory");
            var eq = _pd != null ? J.Obj(_pd, "equipments") : null;
            FillEquipCombo(_cbWeapon, inv, "weapon", J.Str(eq, "weapon"));
            FillEquipCombo(_cbWearable, inv, "wearable", J.Str(eq, "wearable"));
        }

        // ---------------- 小ヘルパ ----------------
        // 基本値欄の初期テキスト。整数/文字列いずれでも表示用文字列にして返す。
        private string ValAsText(string key)
        {
            if (_pd == null || !_pd.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return "";
            if (v.TryGetValue<long>(out long l)) return l.ToString();
            if (v.TryGetValue<string>(out string s)) return s;
            return v.ToString();
        }

        // 指定キーの配列/辞書を取得（無ければ作って入れてから返す）。
        private JsonArray EnsureArr(string key)
        {
            var a = J.Arr(_pd, key); if (a == null) { a = new JsonArray(); _pd[key] = a; }
            return a;
        }
        private JsonObject EnsureObj(string key)
        {
            var o = J.Obj(_pd, key); if (o == null) { o = new JsonObject(); _pd[key] = o; }
            return o;
        }

        // 配列の選択要素を JSON ダイアログで編集 / 削除。辞書版も同様（キー単位）。
        private void EditListItem(ListBox lb, JsonArray arr, Action refresh)
        {
            int i = lb.SelectedIndex; if (arr == null || i < 0 || i >= arr.Count) return;
            using var d = new JsonEditDialog(I18n.T("title.editItem"), arr[i]);
            if (d.ShowDialog(this) == DialogResult.OK) { arr[i] = d.ResultNode; refresh(); }
        }
        private void RemoveListItem(ListBox lb, JsonArray arr, Action refresh)
        {
            int i = lb.SelectedIndex; if (arr == null || i < 0 || i >= arr.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) == DialogResult.Yes) { arr.RemoveAt(i); refresh(); }
        }
        // 装備コンボの1候補。表示は "id：名前"、保持する値は id。先頭の「なし」は Id=null。
        private sealed class EquipChoice
        {
            public string Id;
            public string Label;
            public override string ToString() => Label;
        }

        // inv から item_type==type のアイテムだけを "id：名前" 候補にして詰める。先頭は「なし」。
        // current（現装備ID）が種別不一致でインベントリに存在する場合も、その項目だけは残して選択する。
        private static void FillEquipCombo(ComboBox cb, JsonObject inv, string type, string current)
        {
            cb.Items.Clear();
            cb.Items.Add(new EquipChoice { Id = null, Label = I18n.T("label.none") });
            int sel = 0;
            if (inv != null)
                foreach (var kv in inv)
                {
                    if (kv.Value is not JsonObject o) continue;
                    bool isType = string.Equals(J.Str(o, "item_type"), type, StringComparison.Ordinal);
                    bool isCurrent = kv.Key == current;
                    if (!isType && !isCurrent) continue;   // 種別不一致でも現装備は残す（データ消失防止）
                    cb.Items.Add(new EquipChoice { Id = kv.Key, Label = $"{kv.Key}：{J.Str(o, "name")}" });
                    if (isCurrent) sel = cb.Items.Count - 1;
                }
            cb.SelectedIndex = sel;
        }
        // コンボ選択をJSON値へ。「なし」(Id=null)は null、それ以外はアイテムID文字列。
        private static JsonNode ComboVal(ComboBox cb)
            => cb.SelectedItem is EquipChoice c && c.Id != null ? JsonValue.Create(c.Id) : null;

        // 表セルの文字列取得（null安全）。
        private static string Cell(DataGridViewRow r, int c) => r.Cells[c].Value?.ToString() ?? "";

        // 型エラー表示（false を返す）。
        private static bool Fail(string f, string type)
        { MessageBox.Show(I18n.T("msg.typeError", f, type), I18n.T("title.typeError"), MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }

        // --- UI 部品ファクトリ ---
        // 固定幅760・固定高のグループ枠（Dock=Top で実幅は窓に追従）。
        private static GroupBox Group(string title, int height)
            => new() { Text = title, AutoSize = false, Width = 760, Height = height, Margin = new Padding(4), Padding = new Padding(8) };
        // ラベル。
        private static Label L(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(2, 6, 4, 0) };
        // 表の列（読み取り専用指定可）。
        private static DataGridViewTextBoxColumn Col(string h, bool ro) => new() { HeaderText = h, ReadOnly = ro };
        // ラベル+入力の表。cols=2 はラベル列固定、1 は単一列。いずれも右側はウィンドウ幅に追従。
        private TableLayoutPanel Grid(int cols)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = cols == 2 ? 2 : 1, AutoSize = true, Padding = new Padding(6) };
            if (cols == 2) { t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); }
            else t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return t;
        }
        // (ラベル,処理) の並びからボタン列を作る。
        private FlowLayoutPanel ButtonBar(params (string text, Action act)[] btns)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            foreach (var (text, act) in btns)
            {
                var b = new Button { Text = text, Width = 70, Margin = new Padding(2) };
                b.Click += (_, _) => act();
                bar.Controls.Add(b);
            }
            return bar;
        }
    }
}
