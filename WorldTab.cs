// ワールドタブ: セーブ内の世界データ(world_data/areas/npcs/quests/story_quests/index)を
// 左ツリーで辿り、選択レコードを右の汎用フォーム(ObjectForm)で編集する。
// NPC/facility 選択時は対応する画像パネル(NpcImagePanel/BackgroundImagePanel)を差し込む。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // 編集はフォーカスアウトで即モデルへ反映されるため、レコード切替時の特別な保存処理は不要。
    internal sealed class WorldTab : UserControl
    {
        // ツリーに「セクション→項目」で展開する、id付き辞書のセクション。
        private static readonly string[] Sections = { "areas", "npcs", "quests", "story_quests" };
        // 項目の表示名に使うフィールドの優先順位（最初に見つかったものを使う）。
        private static readonly string[] NameFields = { "name", "quest_title", "title", "id" };
        // 死亡 NPC をまとめる擬似グループキー（area id は数値文字列のため衝突しない）。
        private const string DeadKey = "__dead__";

        private JsonObject _root;
        private string _worldDir;  // worlds/{スロット}/ のパス。NPC画像フォルダの解決に使う。
        private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
        private readonly ObjectForm _form = new() { Dock = DockStyle.Fill };
        private readonly NpcImagePanel _npcPanel = new();         // NPC選択時にフォームへ注入する画像パネル
        private readonly BackgroundImagePanel _bgPanel = new();   // facility選択時にフォームへ注入する背景画像パネル
        private Button _btnDup, _btnDel, _btnUnlock, _btnExport;
        private bool _npcLocked;            // 現在のNPCが未生成（立ち絵未取得）で閲覧のみか
        private string _curKind;            // obj / sec / item / facility（node 階層はツリーに出さない）
        private JsonObject _curContainer;   // 選択レコードを保持する辞書（複製/削除の対象）
        private string _curKey;             // _curContainer 内のキー

        public WorldTab()
        {
            // 左:ツリー / 右:操作ボタン+フォーム の2ペイン。
            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 120 };
            Load += (_, _) => { try { split.SplitterDistance = 280; } catch { } };  // 表示後に分割位置を設定
            split.Panel1.Controls.Add(_tree);

            // 右ペインは上段(ボタン)・下段(フォーム)の2行。行を明示して重なりを防ぐ。
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var ops = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(4) };
            _btnDup = new Button { Width = 70, Enabled = false };
            _btnDel = new Button { Width = 70, Enabled = false };
            _btnUnlock = new Button { Width = 90, Visible = false };
            _btnExport = new Button { Width = 100, Visible = false };
            _btnDup.Click += (_, _) => Duplicate();
            _btnDel.Click += (_, _) => Delete();
            _btnUnlock.Click += (_, _) => UnlockNpc();
            _btnExport.Click += (_, _) => ExportNpc();
            ops.Controls.AddRange(new Control[] { _btnDup, _btnDel, _btnUnlock, _btnExport });
            right.Controls.Add(ops, 0, 0);
            right.Controls.Add(_form, 0, 1);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            _tree.AfterSelect += (_, e) => OnSelect(e.Node);

            Localize();
            // 言語切替で操作ボタンとツリーの見出し（死者/エリアなし等）を再構築する。Dispose で解除。
            I18n.LanguageChanged += OnLanguageChanged;
        }

        // 操作ボタンの文言を現在言語で適用する。
        private void Localize()
        {
            _btnDup.Text = I18n.T("btn.duplicate");
            _btnDel.Text = I18n.T("btn.delete");
            _btnUnlock.Text = I18n.T("btn.unlock");
            _btnExport.Text = I18n.T("btn.export");
        }

        // 言語切替時にボタン文言を更新し、ツリー見出しの翻訳を反映するため作り直す。
        private void OnLanguageChanged() { Localize(); if (_root != null) Populate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= OnLanguageChanged;
            base.Dispose(disposing);
        }

        // ルートをバインドしてツリーを構築する。filePath はワールドディレクトリの解決に使う。
        public void Bind(JsonObject root, string filePath = null)
        {
            _root = root;
            _worldDir = ResolveWorldDir(filePath);
            Populate();
        }

        // 開いたファイルパスから worlds/{スロット}/ ディレクトリを導く。
        // world_data.json 直接の場合: そのディレクトリ。
        // saves/{スロット}/savedata.json の場合: ../worlds/{スロット}/ を探す。
        internal static string ResolveWorldDir(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            try
            {
                if (Path.GetFileName(filePath).Equals("world_data.json", StringComparison.OrdinalIgnoreCase))
                    return Path.GetDirectoryName(filePath);

                string slotDir = Path.GetDirectoryName(filePath);  // saves/{スロット}
                string slot = Path.GetFileName(slotDir);
                string savesDir = Path.GetDirectoryName(slotDir);   // saves/
                string baseDir = Path.GetDirectoryName(savesDir);    // 基底
                string candidate = Path.Combine(baseDir, "worlds", slot);
                if (Directory.Exists(candidate)) return candidate;
            }
            catch { }
            return null;
        }

        // 保存前に、現在表示中のレコードの未確定入力を反映する（型エラーなら false）。
        public bool ApplyCurrent() => _form.Apply();

        // ツリーを再構築する。world_data / index は単一ノード、areas 等はセクション→項目で展開。
        // dungeon / npcs / quests は、紐づく area（拠点）ごとの見出しノードでグループ化する。
        private void Populate()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            _form.Clear();
            if (_root == null) { _tree.EndUpdate(); return; }

            if (_root["world_data"] is JsonObject)
                _tree.Nodes.Add(new TreeNode("world_data") { Tag = new[] { "obj", "world_data" } });

            // ダンジョン area → 紐づく拠点 id。各ダンジョンを quest_area_id で参照するクエストの
            // neighboring_settlement_id から逆引きする（最初に見つかった拠点を採用）。
            var dungeonTown = BuildDungeonTownMap();

            foreach (var sec in Sections)
            {
                if (_root[sec] is not JsonObject so) continue;
                // areas は size=="dungeon" を別ツリー(dungeon)に分ける。どちらも実体は _root["areas"] の
                // ため、項目/facility の Tag セクションは "areas" のまま（編集・複製・削除はそのまま動く）。
                if (sec == "areas")
                {
                    var keys = so.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k).ToList();
                    bool IsDungeon(string k) => J.Str(so[k] as JsonObject, "size") == "dungeon";
                    var normal = keys.Where(k => !IsDungeon(k)).ToList();
                    var dungeon = keys.Where(IsDungeon).ToList();

                    var areaNode = new TreeNode($"areas ({normal.Count})") { Tag = new[] { "sec", "areas" } };
                    foreach (var k in normal) areaNode.Nodes.Add(BuildAreaItem(so, k));
                    _tree.Nodes.Add(areaNode);

                    if (dungeon.Count > 0)
                    {
                        var dunNode = new TreeNode($"dungeon ({dungeon.Count})") { Tag = new[] { "sec", "areas" } };
                        AddGroups(dunNode, dungeon,
                            k => dungeonTown.TryGetValue(k, out var t) ? t : null,
                            k => BuildAreaItem(so, k));
                        _tree.Nodes.Add(dunNode);
                    }
                    continue;
                }

                var node = new TreeNode($"{sec} ({so.Count})") { Tag = new[] { "sec", sec } };
                var itemKeys = so.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k).ToList();
                if (sec == "npcs")
                    AddGroups(node, itemKeys,
                        // 死亡 NPC は area で分けず「死者」グループへ一括りにする。
                        k => so[k] is JsonObject npc && J.NpcIsDead(npc) ? DeadKey : IdStr(so[k]?["current_area"]),
                        k => SectionItemNode(sec, so, k));
                else if (sec == "quests")
                    AddGroups(node, itemKeys,
                        k => IdStr(so[k]?["neighboring_settlement_id"]),
                        k => SectionItemNode(sec, so, k));
                else  // story_quests など area で区分けしないセクションは従来通りフラット
                    foreach (var k in itemKeys) node.Nodes.Add(SectionItemNode(sec, so, k));
                _tree.Nodes.Add(node);
            }
            if (_root["index"] is JsonObject)
                _tree.Nodes.Add(new TreeNode("index") { Tag = new[] { "obj", "index" } });
            _tree.EndUpdate();
        }

        // セクション内の1レコードを項目ノード化する。NPC は死亡扱い(config.is_dead)なら「（死亡）」を付ける。
        private static TreeNode SectionItemNode(string sec, JsonObject so, string k)
        {
            string label = Label(so[k]);
            if (sec == "npcs" && so[k] is JsonObject npcO && J.NpcIsDead(npcO)) label += I18n.T("suffix.dead");
            return new TreeNode($"{k}: {label}") { Tag = new[] { "item", sec, k } };
        }

        // 項目を area（拠点）ごとの見出しノードに振り分けて parent 配下に積む。
        // groupKeyOf: 項目キー→所属 area id（空/null なら「（エリアなし）」へ）。見出しは選択不可（Tag=null）。
        private void AddGroups(TreeNode parent, List<string> itemKeys, Func<string, string> groupKeyOf, Func<string, TreeNode> itemNodeOf)
        {
            var byArea = new Dictionary<string, List<string>>();
            foreach (var k in itemKeys)
            {
                string a = groupKeyOf(k) ?? "";
                if (!byArea.TryGetValue(a, out var lst)) byArea[a] = lst = new List<string>();
                lst.Add(k);
            }
            foreach (var areaId in OrderGroups(byArea.Keys))
            {
                var grp = new TreeNode(GroupLabel(areaId, byArea[areaId].Count));   // Tag=null → 見出し（選択時はフォームをクリア）
                foreach (var k in byArea[areaId]) grp.Nodes.Add(itemNodeOf(k));
                parent.Nodes.Add(grp);
            }
        }

        // グループ見出しの並び順。areas のキー順（長さ→辞書順）を尊重し、areas に無い id を続け、
        // 「（エリアなし）」（空文字キー）は末尾に置く。
        private IEnumerable<string> OrderGroups(IEnumerable<string> areaIds)
        {
            var set = new HashSet<string>(areaIds);
            bool hasDead = set.Remove(DeadKey);   // 「死者」は末尾に固定
            var ordered = new List<string>();
            if (_root?["areas"] is JsonObject areas)
                foreach (var k in areas.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k))
                    if (set.Remove(k)) ordered.Add(k);
            foreach (var r in set.Where(s => s.Length > 0).OrderBy(s => s.Length).ThenBy(s => s)) ordered.Add(r);
            if (set.Contains("")) ordered.Add("");
            if (hasDead) ordered.Add(DeadKey);
            return ordered;
        }

        // グループ見出しのラベル（"id: 名前 (件数)"）。空文字は未所属、areas に無い id は不明扱い。
        private string GroupLabel(string areaId, int count)
        {
            if (areaId == DeadKey) return I18n.T("world.group.dead", count);
            if (string.IsNullOrEmpty(areaId)) return I18n.T("world.group.noArea", count);
            if (_root?["areas"]?[areaId] is JsonObject ao)
            {
                string nm = J.Str(ao, "name");
                return nm.Length > 0 ? $"{areaId}: {nm} ({count})" : $"{areaId} ({count})";
            }
            return I18n.T("world.group.unknownArea", areaId, count);
        }

        // ダンジョン area id → 紐づく拠点 id のマップ。quest_area_id で参照するクエストから逆引きする。
        private Dictionary<string, string> BuildDungeonTownMap()
        {
            var map = new Dictionary<string, string>();
            if (_root?["quests"] is JsonObject quests)
                foreach (var kv in quests)
                    if (kv.Value is JsonObject q)
                    {
                        string da = IdStr(q["quest_area_id"]);
                        if (!string.IsNullOrEmpty(da) && !map.ContainsKey(da))
                            map[da] = IdStr(q["neighboring_settlement_id"]) ?? "";
                    }
            return map;
        }

        // JsonNode を id 文字列にする（数値・文字列いずれの id でも統一して扱う。null は null）。
        private static string IdStr(JsonNode n) => n?.ToString();

        // areas の1レコードを項目ノード化する。配下の facilities を接続グラフで階層展開する
        // （中間の node 階層は隠す）。areas/dungeon の両ツリーから共通で使う。
        private static TreeNode BuildAreaItem(JsonObject areas, string k)
        {
            var itemNode = new TreeNode($"{k}: {Label(areas[k])}") { Tag = new[] { "item", "areas", k } };
            if (J.Obj(areas[k] as JsonObject, "nodes") is JsonObject nodes)
                foreach (var nk in nodes.Select(p => p.Key).OrderBy(k2 => k2.Length).ThenBy(k2 => k2))
                    if (J.Obj(nodes[nk] as JsonObject, "facilities") is JsonObject facs)
                        AddFacilityTree(itemNode, k, nk, facs, J.Str(nodes[nk].AsObject(), "entrance_facility"));
            return itemNode;
        }

        // facilities を connections に沿って木構造で展開し、areaNode 直下に差し込む。
        // entrance_facility を起点に DFS し、接続順を保つ。起点から辿れない facility は
        // 最後に ID 順でフラットに追加する（connections が空のダンジョン等に対応）。
        private static void AddFacilityTree(TreeNode areaNode, string areaId, string nodeId, JsonObject facilities, string entranceId)
        {
            var visited = new HashSet<string>();

            void AddFac(TreeNode parent, string fid)
            {
                if (facilities[fid] is not JsonObject fo || !visited.Add(fid)) return;
                var t = new TreeNode($"{fid}: {Label(fo)}") { Tag = new[] { "facility", areaId, nodeId, fid } };
                parent.Nodes.Add(t);
                if (J.Arr(fo, "connections") is JsonArray conns)
                    foreach (var c in conns)
                        if (c?.ToString() is string cid && facilities.ContainsKey(cid) && !visited.Contains(cid))
                            AddFac(t, cid);
            }

            if (!string.IsNullOrEmpty(entranceId) && facilities.ContainsKey(entranceId))
                AddFac(areaNode, entranceId);
            foreach (var fk in facilities.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k))
                AddFac(areaNode, fk);   // 未訪問の facility のみ追加される（visited で自動スキップ）
        }

        // 項目の表示名を NameFields の優先順で決める（空文字は未設定とみなす）。
        private static string Label(JsonNode item)
        {
            if (item is JsonObject o)
                foreach (var f in NameFields)
                    if (o.TryGetPropertyValue(f, out var v) && v != null && v.ToString().Length > 0)
                        return v.ToString();
            return I18n.T("label.item");
        }

        // ツリー選択に応じて対象オブジェクトをフォームへバインドし、操作ボタンの有効/無効を切り替える。
        private void OnSelect(TreeNode node)
        {
            _btnUnlock.Visible = false; _npcLocked = false; _btnExport.Visible = false;
            if (node?.Tag is not string[] tag) { _form.Clear(); SetBtns(false); return; }
            _curKind = tag[0];
            switch (tag[0])
            {
                case "obj":   // world_data / index など単一オブジェクト
                    _curContainer = null; _curKey = null;
                    _form.ClearComboFields();
                    _form.Bind(_root[tag[1]].AsObject()); SetBtns(false); break;
                case "item":  // セクション内の1レコード（複製/削除可）
                    _curContainer = _root[tag[1]].AsObject(); _curKey = tag[2];
                    var itemObj = _curContainer[_curKey].AsObject();
                    SetBtns(true);
                    if (tag[1] == "npcs")
                    {
                        // NPC: look_description 直後に画像パネルを差し込む。
                        // エリア/ノードのフィールドをプルダウンで表示する。
                        string npcName = J.Str(itemObj, "name");
                        string charDir = _worldDir != null
                            ? Path.Combine(_worldDir, "characters", npcName)
                            : null;
                        _npcPanel.LoadImages(Directory.Exists(charDir) ? charDir : null);
                        RegisterNpcCombos(itemObj);
                        // relationship の対象キー（player 以外は NPC ID 想定）を NPC 名へ解決する。
                        _form.RelationshipTargetNamer = ResolveNpcName;
                        // look（外見タグ）を look_description の直下へ移動し、画像パネルはその下に差し込む。
                        string injectAfter = itemObj.ContainsKey("look") ? "look" : "look_description";
                        _form.Bind(itemObj, _npcPanel, injectAfter, ("look", "look_description"));
                        LinkAreaLocation();
                        ApplyNpcLock(charDir);
                        _btnExport.Visible = true;   // NPC はロック状態に関わらずエクスポート可
                        _btnExport.Enabled = true;
                    }
                    else
                    {
                        _form.ClearComboFields();
                        _form.Bind(itemObj);
                    }
                    break;
                case "facility":  // areas[area].nodes[node].facilities[facility]
                    _curContainer = J.Obj(J.Obj(_root?["areas"]?[tag[1]]?.AsObject(), "nodes")?[tag[2]]?.AsObject(), "facilities");
                    _curKey = tag[3];
                    var facObj = _curContainer[_curKey].AsObject();
                    _form.ClearComboFields();
                    // facility_type / tier は FieldOptions（外部 JSON 由来）のテンプレート候補のプルダウンにする。
                    _form.RegisterComboField("facility_type", val => MakeValueCombo(FieldOptions.Get("facility_type"), val));
                    _form.RegisterComboField("tier", val => MakeValueCombo(FieldOptions.Get("tier"), val));
                    // owner は所有者 NPC の ID。NPC 一覧から "ID: 名前" で選べるプルダウンにする。
                    _form.RegisterComboField("owner", val => MakeNpcCombo(val));
                    // description 直後に背景画像(backgrounds/{facility名}/image.png)を差し込む。
                    _bgPanel.LoadImage(_worldDir, J.Str(facObj, "name"));
                    _form.Bind(facObj, _bgPanel, "description");
                    SetBtns(true); break;
                default:      // セクション見出しなど
                    _form.Clear(); SetBtns(false); break;
            }
        }

        // 複製/削除ボタンはレコード（item/facility）選択時のみ有効。
        private void SetBtns(bool on) { _btnDup.Enabled = on; _btnDel.Enabled = on; }

        // 未生成NPC（立ち絵未取得）はゲームが落ちる恐れがあるため、既定でフォームを読み取り専用にし
        // 複製/削除も無効化する。ロック解除ボタンを表示し、押下で警告のうえ編集を許可する。
        private void ApplyNpcLock(string charDir)
        {
            _npcLocked = !IsNpcGenerated(charDir);
            _form.SetReadOnly(_npcLocked);
            _btnUnlock.Visible = _npcLocked;
            _btnUnlock.Enabled = _npcLocked;
            _btnUnlock.Text = I18n.T("btn.unlock");
            if (_npcLocked) { _btnDup.Enabled = false; _btnDel.Enabled = false; }
        }

        // NPCがゲーム内で生成済みか。立ち絵(reduced_color_image.png)の有無で判定する。
        // charDir が null（ワールドディレクトリ不明で判定不能）の場合はロックしない。
        private static bool IsNpcGenerated(string charDir)
            => string.IsNullOrEmpty(charDir)
               || File.Exists(Path.Combine(charDir, "reduced_color_image.png"));

        // ロック解除: 警告に同意した場合のみ、読み取り専用を解除して編集・複製・削除を可能にする。
        private void UnlockNpc()
        {
            if (!_npcLocked) return;
            if (MessageBox.Show(
                    I18n.T("msg.npcUnlockConfirm"),
                    I18n.T("title.npcUnlock"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            _npcLocked = false;
            _form.SetReadOnly(false);
            _btnDup.Enabled = true;
            _btnDel.Enabled = true;
            _btnUnlock.Enabled = false;
            _btnUnlock.Text = I18n.T("btn.unlocked");
        }

        // relationship の対象キーを NPC 名へ解決する。キーが NPC ID（または name）に一致すれば名前を返す。
        // 見つからなければ null（呼び出し側でキーをそのまま表示）。
        private string ResolveNpcName(string key)
        {
            var npcs = _root?["npcs"]?.AsObject();
            if (npcs == null || string.IsNullOrEmpty(key)) return null;
            if (npcs[key] is JsonObject byId) return J.Str(byId, "name");
            foreach (var kv in npcs)
                if (kv.Value is JsonObject o && J.Str(o, "name") == key) return key;
            return null;
        }

        // NPC の current_area / current_location をエリア/ノードのプルダウンにする。
        // category / job は FieldOptions（外部 JSON 由来のテンプレート候補）の自由入力プルダウンにする。
        private void RegisterNpcCombos(JsonObject npcObj)
        {
            var areas = _root?["areas"]?.AsObject();
            string curArea = J.Str(npcObj, "current_area");
            _form.ClearComboFields();
            _form.RegisterComboField("current_area",
                val => AreaComboHelper.MakeAreaCombo(areas, val));
            _form.RegisterComboField("current_location",
                val => AreaComboHelper.MakeFacilityCombo(areas, curArea, val));
            _form.RegisterComboField("category", val => MakeValueCombo(FieldOptions.Get("category"), val));
            _form.RegisterComboField("job", val => MakeValueCombo(FieldOptions.Get("job"), val));
        }

        // テンプレート候補を持つ自由入力プルダウンを作る（category/job など列挙的フィールド用）。
        // 候補に無い現在値も保持できるよう Text に直接設定する。
        private static ComboBox MakeValueCombo(IEnumerable<string> options, string currentVal)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                Dock = DockStyle.Fill,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
            };
            foreach (var o in options) cb.Items.Add(o);
            cb.Text = currentVal ?? "";
            return cb;
        }

        // facility の owner（所有者 NPC の ID）を "ID: 名前" のプルダウンで表示・選択する。
        // 候補に無い現在値も保持できるよう、未一致なら ID をそのまま表示する。
        private ComboBox MakeNpcCombo(string currentVal)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                Dock = DockStyle.Fill,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
            };
            var npcs = _root?["npcs"]?.AsObject();
            if (npcs != null)
                foreach (var kv in npcs.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                {
                    string name = kv.Value is JsonObject o ? J.Str(o, "name", kv.Key) : kv.Key;
                    cb.Items.Add($"{kv.Key}: {name}");
                }
            void AutoFill(string val)
            {
                string id = AreaComboHelper.ExtractId(val);
                string match = cb.Items.Cast<string>()
                    .FirstOrDefault(it => AreaComboHelper.ExtractId(it) == id);
                cb.Text = string.IsNullOrEmpty(val) ? "" : (match ?? val);
            }
            AutoFill(currentVal);
            cb.Leave += (_, _) => AutoFill(cb.Text);
            return cb;
        }

        // current_area の選択変更に追従して current_location のノード一覧を作り直す。
        // 変更後のエリアに無いノードを指していた場合は選択をクリアする。
        private void LinkAreaLocation()
        {
            var areas = _root?["areas"]?.AsObject();
            var areaCb = _form.GetCombo("current_area");
            var locCb = _form.GetCombo("current_location");
            if (areaCb == null || locCb == null) return;

            void Refill()
            {
                string area = AreaComboHelper.ExtractId(areaCb.Text);
                // 旧施設IDが新エリアに存在しなければクリア。あれば（構造施設でも）残して "ID: 名前" に補完。
                string id = AreaComboHelper.ExtractId(locCb.Text);
                AreaComboHelper.FillFacilityItems(locCb, areas, area, id);
                string match = locCb.Items.Cast<string>()
                    .FirstOrDefault(it => AreaComboHelper.ExtractId(it) == id);
                locCb.Text = match ?? "";
            }

            areaCb.SelectedIndexChanged += (_, _) => Refill();
        }

        // 辞書内の数値キーの最大+1 を新IDとして返す。
        private static string NextKey(JsonObject container)
        {
            int max = -1;
            foreach (var kv in container)
                if (int.TryParse(kv.Key, out int n) && n > max) max = n;
            return (max + 1).ToString();
        }

        // 選択レコード（item/facility）を確認の上で複製する。表示中の編集を反映してからディープコピーし、新IDを振る。
        private void Duplicate()
        {
            if (_curContainer == null || _curKey == null) return;
            string name = Label(_curContainer[_curKey]);
            if (MessageBox.Show(I18n.T("msg.duplicateConfirm", name, _curKind, _curKey), I18n.T("title.duplicateConfirm"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから複製
            string nk = NextKey(_curContainer);
            var clone = _curContainer[_curKey].DeepClone();
            if (clone is JsonObject co && co.ContainsKey("id")) co["id"] = nk;
            _curContainer[nk] = clone;
            Populate();
        }

        // 選択中の NPC を JSON＋画像の zip としてエクスポートする。
        private void ExportNpc()
        {
            if (_curContainer == null || _curKey == null || _curContainer[_curKey] is not JsonObject npc) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから書き出す
            string name = J.Str(npc, "name", "npc");
            string source = J.Str(J.Obj(_root, "world_data"), "name");
            if (string.IsNullOrEmpty(source) && _worldDir != null) source = Path.GetFileName(_worldDir);

            using var dlg = new SaveFileDialog
            {
                Filter = I18n.T("filter.npcPackageSave"),
                DefaultExt = "zip",
                FileName = SafeFileName(name) + ".zip",
            };
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            try { NpcPortability.Export(npc, _worldDir, source, _curKey, dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.exportFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(I18n.T("msg.npcExported", name, dlg.FileName), I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ファイル名に使えない文字を '_' に置き換える。
        private static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "npc" : s;
        }

        // 選択レコード（item/facility）を確認の上で削除する。
        private void Delete()
        {
            if (_curContainer == null || _curKey == null) return;
            if (MessageBox.Show(I18n.T("msg.deleteConfirm", _curKind, _curKey), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _curContainer.Remove(_curKey);
            _form.Clear(); Populate();
        }
    }
}
