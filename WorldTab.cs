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

        private JsonObject _root;
        private string _worldDir;  // worlds/{スロット}/ のパス。NPC画像フォルダの解決に使う。
        private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
        private readonly ObjectForm _form = new() { Dock = DockStyle.Fill };
        private readonly NpcImagePanel _npcPanel = new();         // NPC選択時にフォームへ注入する画像パネル
        private readonly BackgroundImagePanel _bgPanel = new();   // facility選択時にフォームへ注入する背景画像パネル
        private Button _btnDup, _btnDel, _btnUnlock;
        private bool _npcLocked;            // 現在のNPCが未生成（立ち絵未取得）で閲覧のみか
        private string _curKind;            // obj / item / node / facility
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
            _btnDup = new Button { Text = "複製", Width = 70, Enabled = false };
            _btnDel = new Button { Text = "削除", Width = 70, Enabled = false };
            _btnUnlock = new Button { Text = "ロック解除", Width = 90, Visible = false };
            _btnDup.Click += (_, _) => Duplicate();
            _btnDel.Click += (_, _) => Delete();
            _btnUnlock.Click += (_, _) => UnlockNpc();
            ops.Controls.AddRange(new Control[] { _btnDup, _btnDel, _btnUnlock });
            right.Controls.Add(ops, 0, 0);
            right.Controls.Add(_form, 0, 1);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            _tree.AfterSelect += (_, e) => OnSelect(e.Node);
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
        private void Populate()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            _form.Clear();
            if (_root == null) { _tree.EndUpdate(); return; }

            if (_root["world_data"] is JsonObject)
                _tree.Nodes.Add(new TreeNode("world_data") { Tag = new[] { "obj", "world_data" } });

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
                        foreach (var k in dungeon) dunNode.Nodes.Add(BuildAreaItem(so, k));
                        _tree.Nodes.Add(dunNode);
                    }
                    continue;
                }
                var node = new TreeNode($"{sec} ({so.Count})") { Tag = new[] { "sec", sec } };
                foreach (var k in so.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k))
                {
                    var itemNode = new TreeNode($"{k}: {Label(so[k])}") { Tag = new[] { "item", sec, k } };
                    node.Nodes.Add(itemNode);
                }
                _tree.Nodes.Add(node);
            }
            if (_root["index"] is JsonObject)
                _tree.Nodes.Add(new TreeNode("index") { Tag = new[] { "obj", "index" } });
            _tree.EndUpdate();
        }

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
            return "(項目)";
        }

        // ツリー選択に応じて対象オブジェクトをフォームへバインドし、操作ボタンの有効/無効を切り替える。
        private void OnSelect(TreeNode node)
        {
            _btnUnlock.Visible = false; _npcLocked = false;
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
                    // description 直後に背景画像(backgrounds/{facility名}/image.png)を差し込む。
                    _bgPanel.LoadImage(_worldDir, J.Str(facObj, "name"));
                    _form.Bind(facObj, _bgPanel, "description");
                    SetBtns(true); break;
                default:      // セクション見出しなど
                    _form.Clear(); SetBtns(false); break;
            }
        }

        // 複製/削除ボタンはレコード（item/node/facility）選択時のみ有効。
        private void SetBtns(bool on) { _btnDup.Enabled = on; _btnDel.Enabled = on; }

        // 未生成NPC（立ち絵未取得）はゲームが落ちる恐れがあるため、既定でフォームを読み取り専用にし
        // 複製/削除も無効化する。ロック解除ボタンを表示し、押下で警告のうえ編集を許可する。
        private void ApplyNpcLock(string charDir)
        {
            _npcLocked = !IsNpcGenerated(charDir);
            _form.SetReadOnly(_npcLocked);
            _btnUnlock.Visible = _npcLocked;
            _btnUnlock.Enabled = _npcLocked;
            _btnUnlock.Text = "ロック解除";
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
                    "このNPCはゲーム内でまだ生成されていない可能性があります（立ち絵画像が未取得）。\n" +
                    "生成前にデータを改変すると、ゲームが正常に起動しなくなる場合があります。\n\n" +
                    "編集を有効にしますか？",
                    "編集ロックの解除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            _npcLocked = false;
            _form.SetReadOnly(false);
            _btnDup.Enabled = true;
            _btnDel.Enabled = true;
            _btnUnlock.Enabled = false;
            _btnUnlock.Text = "解除済み";
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
        private void RegisterNpcCombos(JsonObject npcObj)
        {
            var areas = _root?["areas"]?.AsObject();
            string curArea = J.Str(npcObj, "current_area");
            _form.ClearComboFields();
            _form.RegisterComboField("current_area",
                val => AreaComboHelper.MakeAreaCombo(areas, val));
            _form.RegisterComboField("current_location",
                val => AreaComboHelper.MakeFacilityCombo(areas, curArea, val));
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

        // 選択レコード（item/node/facility）を確認の上で複製する。表示中の編集を反映してからディープコピーし、新IDを振る。
        private void Duplicate()
        {
            if (_curContainer == null || _curKey == null) return;
            string name = Label(_curContainer[_curKey]);
            if (MessageBox.Show($"「{name}」({_curKind}[{_curKey}]) を複製しますか？", "複製の確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから複製
            string nk = NextKey(_curContainer);
            var clone = _curContainer[_curKey].DeepClone();
            if (clone is JsonObject co && co.ContainsKey("id")) co["id"] = nk;
            _curContainer[nk] = clone;
            Populate();
        }

        // 選択レコード（item/node/facility）を確認の上で削除する。
        private void Delete()
        {
            if (_curContainer == null || _curKey == null) return;
            if (MessageBox.Show($"{_curKind}[{_curKey}] を削除しますか？", "確認", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _curContainer.Remove(_curKey);
            _form.Clear(); Populate();
        }
    }
}
