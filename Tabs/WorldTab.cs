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
        private string _filePath;  // 開いたファイルのパス。プリセット出力基底（{Instantale}\characters）の解決に使う。
        private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false };
        private readonly ObjectForm _form = new() { Dock = DockStyle.Fill };
        private readonly NpcImagePanel _npcPanel = new();         // NPC選択時にフォームへ注入する画像パネル
        private readonly BackgroundImagePanel _bgPanel = new();   // facility選択時にフォームへ注入する背景画像パネル
        private Button _btnDup, _btnDel, _btnExport, _btnRefresh, _btnJson;
        // エクスポート方式（zip / プリセット）を選ばせるメニュー。ボタン押下でボタン直下に表示する。
        private readonly ContextMenuStrip _exportMenu = new();
        private ToolStripMenuItem _miExportZip, _miExportPreset;
        // ツリーの右クリックメニュー（新規作成・インポート／エクスポート・JSON編集・複製・削除）。
        // レコード単位の操作はここに集約する（ボタン列・ツールメニューと同じ実装を共用）。
        // 項目の表示は Opening で選択ノードに合わせる。
        private readonly ContextMenuStrip _treeMenu = new();
        private readonly ToolStripMenuItem _miNew = new(), _miNewFacility = new(), _miImportNpc = new(), _miImportFac = new();
        // free 施設（facility_type=="free"）専用。プログラム未設定なら作成、設定済みならそのプログラムへ移動する。
        private readonly ToolStripMenuItem _miFreeProgram = new();
        private readonly ToolStripMenuItem _miCtxExportZip = new(), _miCtxExportPreset = new(), _miCtxExportFac = new(), _miCtxJson = new();
        private readonly ToolStripMenuItem _miCtxDup = new(), _miCtxDel = new();
        private readonly ToolStripSeparator _ctxSep = new(), _ctxSep2 = new();

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

            _btnExport = new Button { Width = 100, Visible = false };
            // メモリ上のデータから左ツリーを再構築する（名前変更などをファイル保存/再読込なしで反映）。
            _btnRefresh = new Button { Width = 100 };
            // 選択中の項目（world_data/index/item/facility）を生 JSON で直接編集する。
            _btnJson = new Button { Width = 110, Enabled = false };
            _btnDup.Click += (_, _) => Duplicate();
            _btnDel.Click += (_, _) => Delete();

            _miExportZip = new ToolStripMenuItem();
            _miExportPreset = new ToolStripMenuItem();
            _miExportZip.Click += (_, _) => ExportNpc();
            _miExportPreset.Click += (_, _) => ExportNpcPreset();
            _exportMenu.Items.AddRange(new ToolStripItem[] { _miExportZip, _miExportPreset });
            // facility 選択時は方式選択が無いため直接エクスポート、NPC 選択時はメニューで方式を選ぶ。
            _btnExport.Click += (_, _) =>
            {
                if (_curKind == "facility") ExportFacility();
                else _exportMenu.Show(_btnExport, new Point(0, _btnExport.Height));
            };
            _btnRefresh.Click += (_, _) => RefreshTree();
            _btnJson.Click += (_, _) => EditJson();
            // エクスポートは NPC 選択時のみ表示されるため、常設ボタンの並びが動かないよう右端に置く。
            ops.Controls.AddRange(new Control[] { _btnDup, _btnDel, _btnRefresh, _btnJson, _btnExport });
            right.Controls.Add(ops, 0, 0);
            right.Controls.Add(_form, 0, 1);
            split.Panel2.Controls.Add(right);

            Controls.Add(split);
            // NPC インベントリの item 採番をセーブの index.item と同期させる（デリゲートは呼び出し時の _root を読む）。
            _form.SaveRootProvider = () => _root;
            _tree.AfterSelect += (_, e) => OnSelect(e.Node);

            // ツリーの右クリックメニュー。TreeView は右クリックで選択が動かないため、
            // Opening でカーソル位置のノードを選択してから項目を構成する。
            _miNew.Click += (_, _) => CreateNew();
            _miNewFacility.Click += (_, _) => CreateFacility();
            _miImportNpc.Click += (_, _) => ImportNpcFromTree();
            _miImportFac.Click += (_, _) => ImportFacilityFromTree();
            _miFreeProgram.Click += (_, _) => OpenOrCreateFreeProgram();
            _miCtxExportZip.Click += (_, _) => ExportNpc();
            _miCtxExportPreset.Click += (_, _) => ExportNpcPreset();
            _miCtxExportFac.Click += (_, _) => ExportFacility();
            _miCtxJson.Click += (_, _) => EditJson();
            _miCtxDup.Click += (_, _) => Duplicate();
            _miCtxDel.Click += (_, _) => Delete();
            _treeMenu.Items.AddRange(new ToolStripItem[]
            {
                _miNew, _miNewFacility, _miImportNpc, _miImportFac, _miFreeProgram, _ctxSep,
                _miCtxExportZip, _miCtxExportPreset, _miCtxExportFac, _miCtxJson, _ctxSep2,
                _miCtxDup, _miCtxDel,
            });
            _tree.ContextMenuStrip = _treeMenu;
            _treeMenu.Opening += (_, e) =>
            {
                // HitTest はラベル上に限らず行全体（インデント・ラベル右の余白）でもノードを返すため、
                // 行のどこを右クリックしてもそのノードのメニューが出る。
                if (_tree.HitTest(_tree.PointToClient(Cursor.Position)).Node is TreeNode n) _tree.SelectedNode = n;
                e.Cancel = _root == null || !ConfigureTreeMenu();
            };

            Localize();
            // 言語切替で操作ボタンとツリーの見出し（死者/エリアなし等）を再構築する。Dispose で解除。
            I18n.LanguageChanged += OnLanguageChanged;
        }

        // 操作ボタンの文言を現在言語で適用する。
        private void Localize()
        {
            _btnDup.Text = I18n.T("btn.duplicate");
            _btnDel.Text = I18n.T("btn.delete");

            _btnExport.Text = I18n.T("btn.export");
            _miExportZip.Text = I18n.T("btn.exportNpcZip");
            _miExportPreset.Text = I18n.T("btn.exportNpcPreset");
            _btnRefresh.Text = I18n.T("btn.refreshTree");
            _btnJson.Text = I18n.T("btn.editJsonDirect");
            // _miNew の文言は選択ノードに応じて ConfigureTreeMenu が都度設定する。
            _miNewFacility.Text = I18n.T("menu.tree.newFacility");
            _miImportNpc.Text = I18n.T("menu.tools.importNpc");         // ツールメニューと同じ文言・同じ機能
            _miImportFac.Text = I18n.T("menu.tools.importFacility");
            // _miFreeProgram の文言はプログラムの有無で変わるため ConfigureTreeMenu が都度設定する。
            _miCtxExportZip.Text = I18n.T("btn.exportNpcZip");
            _miCtxExportPreset.Text = I18n.T("btn.exportNpcPreset");
            _miCtxExportFac.Text = I18n.T("menu.tree.exportFacility");
            _miCtxJson.Text = I18n.T("btn.editJsonDirect");
            _miCtxDup.Text = I18n.T("btn.duplicate");
            _miCtxDel.Text = I18n.T("btn.delete");
        }

        // 言語切替時にボタン文言を更新し、ツリー見出しの翻訳を反映するため作り直す。
        private void OnLanguageChanged() { Localize(); if (_root != null) Populate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { I18n.LanguageChanged -= OnLanguageChanged; _exportMenu.Dispose(); _treeMenu.Dispose(); }
            base.Dispose(disposing);
        }

        // ルートをバインドしてツリーを構築する。filePath はワールドディレクトリの解決に使う。
        public void Bind(JsonObject root, string filePath = null)
        {
            _root = root;
            _filePath = filePath;
            _worldDir = ResolveWorldDir(filePath);
            SkillOptions.Collect(root);   // スキル/効果の候補をセーブ全体（player＋NPC）から抽出し直す
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

        // メモリ上のデータから左ツリーを作り直す（ファイル保存/再読込なしで名前変更などを反映）。
        // 表示中の未確定入力を先にモデルへ反映してから再構築する。
        private void RefreshTree()
        {
            if (_root == null) return;
            if (!_form.Apply()) return;   // 型エラーがあれば再構築しない（入力を失わせない）
            Populate();
        }

        // ツリーを再構築する。world_data / index は単一ノード、areas 等はセクション→項目で展開。
        // dungeon / npcs / quests は、紐づく area（拠点）ごとの見出しノードでグループ化する。
        private void Populate()
        {
            // 途中で失敗しても更新抑止が解除されない（ツリーが空のまま固まる）ことがないよう
            // EndUpdate は必ず通す。
            _tree.BeginUpdate();
            try { PopulateCore(); }
            finally { _tree.EndUpdate(); }
        }

        private void PopulateCore()
        {
            _tree.Nodes.Clear();
            _form.Clear();
            if (_root == null) return;

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
                        AddGroups(dunNode, "areas", dungeon,
                            k => dungeonTown.TryGetValue(k, out var t) ? t : null,
                            k => BuildAreaItem(so, k));
                        _tree.Nodes.Add(dunNode);
                    }
                    continue;
                }

                var node = new TreeNode($"{sec} ({so.Count})") { Tag = new[] { "sec", sec } };
                var itemKeys = so.Select(p => p.Key).OrderBy(k => k.Length).ThenBy(k => k).ToList();
                // レコードがオブジェクトでない（JSON編集で壊れた／別バージョン）場合でもツリーを
                // 壊さないよう、グループ分けのキー取得はオブジェクトのときだけ行う。
                if (sec == "npcs")
                    AddGroups(node, sec, itemKeys,
                        // 死亡 NPC は area で分けず「死者」グループへ一括りにする。
                        k => so[k] is not JsonObject npc ? "" : J.NpcIsDead(npc) ? DeadKey : IdStr(npc["current_area"]),
                        k => SectionItemNode(sec, so, k));
                else if (sec == "quests")
                    AddGroups(node, sec, itemKeys,
                        k => so[k] is JsonObject q ? IdStr(q["neighboring_settlement_id"]) : "",
                        k => SectionItemNode(sec, so, k));
                else  // story_quests など area で区分けしないセクションは従来通りフラット
                    foreach (var k in itemKeys) node.Nodes.Add(SectionItemNode(sec, so, k));
                _tree.Nodes.Add(node);
            }
            // free 施設プログラム（ルート直下。world_data の中ではない）もセクションとして並べる。
            if (FreeFacilityProgram.Programs(_root) is JsonObject progs)
            {
                var pnode = new TreeNode($"{FreeFacilityProgram.RootKey} ({progs.Count})")
                { Tag = new[] { "sec", FreeFacilityProgram.RootKey } };
                foreach (var k in progs.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                    pnode.Nodes.Add(SectionItemNode(FreeFacilityProgram.RootKey, progs, k));
                _tree.Nodes.Add(pnode);
            }
            if (_root["index"] is JsonObject)
                _tree.Nodes.Add(new TreeNode("index") { Tag = new[] { "obj", "index" } });
        }

        // セクション内の1レコードを項目ノード化する。NPC は死亡扱い(config.is_dead)なら「（死亡）」を付ける。
        private static TreeNode SectionItemNode(string sec, JsonObject so, string k)
        {
            string label = Label(so[k]);
            if (sec == "npcs" && so[k] is JsonObject npcO && J.NpcIsDead(npcO)) label += I18n.T("suffix.dead");
            return new TreeNode($"{k}: {label}") { Tag = new[] { "item", sec, k } };
        }

        // 項目を area（拠点）ごとの見出しノードに振り分けて parent 配下に積む。
        // groupKeyOf: 項目キー→所属 area id（空/null なら「（エリアなし）」へ）。
        // 見出しの Tag は {grp, セクション, area id}（選択時はフォームをクリア。右クリックの
        // 新規作成でセクションと所属エリアの初期値に使う）。
        private void AddGroups(TreeNode parent, string sec, List<string> itemKeys, Func<string, string> groupKeyOf, Func<string, TreeNode> itemNodeOf)
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
                var grp = new TreeNode(GroupLabel(areaId, byArea[areaId].Count)) { Tag = new[] { "grp", sec, areaId } };
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
            if (J.Obj(_root, "areas")?[areaId] is JsonObject ao)
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
            // 別レコードへ切り替える前に、表示中レコードの未確定入力を確定する。
            // テキスト等は Leave で反映済みだが、life_log/relationship のグリッド編集は
            // Apply() でしか書き戻されないため、ここで呼ばないと切替時に失われる。
            _form.Apply();
            _btnExport.Visible = false;
            // connections の専用欄は area/facility 選択時のみ有効化する（下の各分岐で設定）。
            _form.ConnectionNamer = null;
            _form.ConnectionCandidates = null;
            _form.ConnectionAdded = null;
            _form.ConnectionRemoved = null;
            if (node?.Tag is not string[] tag)
            { _curContainer = null; _curKey = null; _form.Clear(); SetBtns(false); _btnJson.Enabled = false; return; }
            _curKind = tag[0];
            // inventory / skills のグリッド表示は NPC のときだけ有効化する（下の各分岐で上書き）。
            _form.InventoryGridEnabled = tag[0] == "item" && tag[1] == "npcs";
            _form.SkillsGridEnabled = tag[0] == "item" && tag[1] == "npcs";
            // enemies / boss / events の構造化編集はクエスト系のときだけ有効化する
            //（該当フィールドが無いレコードでは何も変わらないため story_quests にも許可する）。
            _form.QuestComponentsEnabled = tag[0] == "item" && (tag[1] == "quests" || tag[1] == "story_quests");
            // steps / prices / payouts の構造化編集は free 施設プログラム選択時のみ有効化する。
            _form.FreeProgramEnabled = tag[0] == "item" && tag[1] == FreeFacilityProgram.RootKey;
            switch (tag[0])
            {
                case "obj":   // world_data / index など単一オブジェクト
                    // JSON直接編集で差し替えられるよう _root をコンテナとして保持する
                    //（複製/削除ボタンは SetBtns(false) で無効のまま）。
                    _curContainer = _root; _curKey = tag[1];
                    _form.ClearComboFields();
                    if (_root[tag[1]] is JsonObject topObj) _form.Bind(topObj); else _form.Clear();
                    SetBtns(false); break;
                case "item":  // セクション内の1レコード（複製/削除可）
                    _curContainer = _root[tag[1]] as JsonObject; _curKey = tag[2];
                    // レコードが object でない（null 等の壊れたデータ）場合はフォームを出さず、削除だけ許す。
                    if (_curContainer?[_curKey] is not JsonObject itemObj)
                    { _form.ClearComboFields(); _form.Clear(); SetBtns(_curContainer != null); break; }
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
                        _btnExport.Visible = true;

                        _btnExport.Enabled = true;
                    }
                    else
                    {
                        _form.ClearComboFields();
                        // area の connections（隣接エリアID配列）は一覧＋追加/削除の専用欄にする。
                        if (tag[1] == "areas") SetAreaConnectionHooks(tag[2]);
                        _form.Bind(itemObj);
                        // プログラムのキーは施設 ID に紐づく（"free_{施設ID}"）ため機械的な複製はさせない。
                        if (tag[1] == FreeFacilityProgram.RootKey) _btnDup.Enabled = false;
                    }
                    break;
                case "facility":  // areas[area].nodes[node].facilities[facility]
                    _curContainer = J.Obj(J.Obj(J.Obj(_root, "areas")?[tag[1]] as JsonObject, "nodes")?[tag[2]] as JsonObject, "facilities");
                    _curKey = tag[3];
                    if (_curContainer?[_curKey] is not JsonObject facObj)
                    { _form.ClearComboFields(); _form.Clear(); SetBtns(_curContainer != null); break; }
                    _form.ClearComboFields();
                    // facility_type / tier は FieldOptions（外部 JSON 由来）のテンプレート候補のプルダウンにする。
                    _form.RegisterComboField("facility_type", val => MakeValueCombo(FieldOptions.Get("facility_type"), val));
                    // tier は実データで未設定を null で表すため、空欄は "" ではなく null で書き戻す。
                    _form.RegisterComboField("tier", val => MakeValueCombo(FieldOptions.Get("tier"), val),
                                             nullWhenEmpty: true);
                    // owner は所有者 NPC の ID。NPC 一覧から "ID: 名前" で選べるプルダウンにする。
                    _form.RegisterComboField("owner", val => MakeNpcCombo(val), idPrefixed: true);
                    // connections（接続施設ID配列）は同一エリア内の施設のみを候補にした専用欄にする。
                    SetFacilityConnectionHooks(tag[1], tag[3]);
                    // description 直後に背景画像(backgrounds/{facility名}/image.png)を差し込む。
                    _bgPanel.LoadImage(_worldDir, J.Str(facObj, "name"));
                    _form.Bind(facObj, _bgPanel, "description");
                    _btnExport.Visible = true;
                    _btnExport.Enabled = true;
                    SetBtns(true); break;
                default:      // セクション見出しなど
                    _curContainer = null; _curKey = null;
                    _form.Clear(); SetBtns(false); break;
            }
            // JSON直接編集は編集対象（コンテナ＋キー）が定まっていれば有効。
            // レコードが object でない壊れたデータでも、生 JSON で修復できるよう許可する。
            _btnJson.Enabled = _curContainer != null && _curKey != null;
        }

        // 複製/削除ボタンはレコード（item/facility）選択時のみ有効。
        private void SetBtns(bool on) { _btnDup.Enabled = on; _btnDel.Enabled = on; }

        // relationship の対象キーを NPC 名へ解決する。キーが NPC ID（または name）に一致すれば名前を返す。
        // 見つからなければ null（呼び出し側でキーをそのまま表示）。
        private string ResolveNpcName(string key)
        {
            var npcs = J.Obj(_root, "npcs");
            if (npcs == null || string.IsNullOrEmpty(key)) return null;
            if (npcs[key] is JsonObject byId) return J.Str(byId, "name");
            foreach (var kv in npcs)
                if (kv.Value is JsonObject o && J.Str(o, "name") == key) return key;
            return null;
        }

        // area の connections（隣接エリアID配列）用フック。候補は自分以外の拠点エリア
        //（size=="dungeon" は除外。ダンジョンへの導線はクエスト経由のため接続対象にしない）。
        // 追加・削除時は相手エリアの connections も書き換えて双方向を保つ。
        private void SetAreaConnectionHooks(string selfId)
        {
            var areas = J.Obj(_root, "areas");
            _form.ConnectionNamer = id => areas?[id] is JsonObject o ? J.Str(o, "name", id) : null;
            _form.ConnectionCandidates = () => areas == null
                ? Enumerable.Empty<(string, string)>()
                : areas.Where(kv => kv.Key != selfId
                              && !(kv.Value is JsonObject d && J.Str(d, "size") == "dungeon"))
                       .Select(kv => (kv.Key, kv.Value is JsonObject o ? J.Str(o, "name", kv.Key) : kv.Key));
            _form.ConnectionAdded = id => { if (areas?[id] is JsonObject o) AddConnection(o, selfId); };
            _form.ConnectionRemoved = id => { if (areas?[id] is JsonObject o) RemoveConnection(o, selfId); };
        }

        // facility の connections（接続施設ID配列）用フック。候補は同一エリア内（全ノード）の自分以外の施設。
        // 追加・削除時は相手施設の connections も書き換えて双方向を保つ。
        private void SetFacilityConnectionHooks(string areaId, string selfFid)
        {
            JsonObject Peer(string id) => EnumAreaFacilities(areaId).FirstOrDefault(p => p.id == id).fo;
            _form.ConnectionNamer = id => Peer(id) is JsonObject o ? J.Str(o, "name", id) : null;
            _form.ConnectionCandidates = () => EnumAreaFacilities(areaId)
                .Where(p => p.id != selfFid)
                .Select(p => (p.id, J.Str(p.fo, "name", p.id)));
            _form.ConnectionAdded = id => { if (Peer(id) is JsonObject o) AddConnection(o, selfFid); };
            _form.ConnectionRemoved = id => { if (Peer(id) is JsonObject o) RemoveConnection(o, selfFid); };
        }

        // target の connections 配列（無ければ新設）へ id を重複なしで追加する。
        private static void AddConnection(JsonObject target, string id)
        {
            if (target["connections"] is not JsonArray arr) target["connections"] = arr = new JsonArray();
            if (!arr.Any(n => (n?.ToString() ?? "") == id)) arr.Add(id);
        }

        // target の connections 配列から id を（重複していても）すべて取り除く。
        private static void RemoveConnection(JsonObject target, string id)
        {
            if (target["connections"] is not JsonArray arr) return;
            for (int i = arr.Count - 1; i >= 0; i--)
                if ((arr[i]?.ToString() ?? "") == id) arr.RemoveAt(i);
        }

        // 指定エリア配下の全ノードの facility を (ID, オブジェクト) で列挙する。
        private IEnumerable<(string id, JsonObject fo)> EnumAreaFacilities(string areaId)
        {
            if (J.Obj(J.Obj(_root, "areas")?[areaId] as JsonObject, "nodes") is not JsonObject nodes) yield break;
            foreach (var nk in nodes)
                if (J.Obj(nk.Value as JsonObject, "facilities") is JsonObject facs)
                    foreach (var fk in facs)
                        if (fk.Value is JsonObject fo) yield return (fk.Key, fo);
        }

        // NPC の current_area / current_location をエリア/ノードのプルダウンにする。
        // category / job は FieldOptions（外部 JSON 由来のテンプレート候補）の自由入力プルダウンにする。
        private void RegisterNpcCombos(JsonObject npcObj)
        {
            var areas = J.Obj(_root, "areas");
            string curArea = J.Str(npcObj, "current_area");
            _form.ClearComboFields();
            _form.RegisterComboField("current_area",
                val => AreaComboHelper.MakeAreaCombo(areas, val), idPrefixed: true);
            _form.RegisterComboField("current_location",
                val => AreaComboHelper.MakeFacilityCombo(areas, curArea, val), idPrefixed: true);
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
            var npcs = J.Obj(_root, "npcs");
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
            var areas = J.Obj(_root, "areas");
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

        // ---------------- ツリー右クリック: 新規作成 ----------------

        // 右クリックメニューの項目を選択ノードに合わせて構成する。表示する項目が無ければ false（メニューを出さない）。
        // 構成: [新規作成/インポート] [エクスポート/JSON編集] [複製/削除] の3グループ。
        private bool ConfigureTreeMenu()
        {
            var tag = _tree.SelectedNode?.Tag as string[];
            string kind = NewKindOf(tag);
            _miNew.Visible = kind != null;
            if (kind != null) _miNew.Text = I18n.T("menu.tree.new." + kind);
            // 施設の新規作成は、施設ノード（そこへ接続）と area 項目（入口へ接続）で出す。
            bool facScope = tag != null && (tag[0] == "facility" || (tag[0] == "item" && tag[1] == "areas"));
            _miNewFacility.Visible = facScope;
            _miImportNpc.Visible = kind == "npcs";
            _miImportFac.Visible = facScope || (tag != null && tag[0] == "sec" && tag[1] == "areas");
            // free 施設は「プログラムを作成」（未設定時）/「プログラムを編集」（設定済み）を出す。
            var freeFac = tag != null && tag[0] == "facility" ? _curContainer?[_curKey] as JsonObject : null;
            if (freeFac != null && !FreeFacilityProgram.IsFree(freeFac)) freeFac = null;
            _miFreeProgram.Visible = freeFac != null;
            if (freeFac != null)
                _miFreeProgram.Text = I18n.T(FreeFacilityProgram.Of(_root, freeFac) != null
                    ? "menu.tree.editFreeProgram" : "menu.tree.newFreeProgram");

            // エクスポートはボタン列と同じ対象（NPC 項目 / 施設ノード）。壊れたレコードは対象外。
            bool validRec = _curContainer?[_curKey] is JsonObject;
            bool npcItem = tag != null && tag[0] == "item" && tag[1] == "npcs" && validRec;
            _miCtxExportZip.Visible = _miCtxExportPreset.Visible = npcItem;
            _miCtxExportFac.Visible = tag != null && tag[0] == "facility" && validRec;
            _miCtxJson.Visible = _btnJson.Enabled;   // world_data / index の右クリックでも JSON 編集だけは出す

            bool rec = tag != null && (tag[0] == "item" || tag[0] == "facility");
            _miCtxDup.Visible = _miCtxDel.Visible = rec;
            _miCtxDup.Enabled = _btnDup.Enabled;   // 選択は Opening 内で済んでいるためボタンの状態をそのまま使う
            _miCtxDel.Enabled = _btnDel.Enabled;

            bool g1 = _miNew.Visible || _miNewFacility.Visible || _miImportNpc.Visible || _miImportFac.Visible
                   || _miFreeProgram.Visible;
            bool g2 = _miCtxExportZip.Visible || _miCtxExportPreset.Visible || _miCtxExportFac.Visible || _miCtxJson.Visible;
            _ctxSep.Visible = g1 && (g2 || rec);
            _ctxSep2.Visible = g2 && rec;
            return g1 || g2 || rec;
        }

        // 選択ノードから「新規作成」対象のセクションを決める（対象外なら null）。
        // story_quests は骨格を機械的に作れない（ストーリー進行に紐づく）ため対象にしない。
        private static string NewKindOf(string[] tag)
        {
            if (tag == null || tag.Length < 2) return null;
            if (tag[0] is not ("sec" or "item" or "grp")) return null;
            return tag[1] is "areas" or "npcs" or "quests" ? tag[1] : null;
        }

        // 「新規作成」の振り分け。右クリック位置（グループ見出し・項目）から所属エリアを引き継ぐ。
        private void CreateNew()
        {
            var tag = _tree.SelectedNode?.Tag as string[];
            string kind = NewKindOf(tag);
            switch (kind)
            {
                case "areas": CreateArea(); break;
                case "npcs": CreateNpc(PresetAreaOf(tag, kind)); break;
                case "quests": CreateQuestFromTree(PresetAreaOf(tag, kind)); break;
            }
        }

        // 右クリック位置（グループ見出し・項目）から所属エリア id を推測する（不明なら null）。
        // 新規作成・インポートの配置先の初期値に使う。
        private string PresetAreaOf(string[] tag, string kind)
        {
            if (tag == null) return null;
            if (tag[0] == "grp") return tag[2] != DeadKey && tag[2].Length > 0 ? tag[2] : null;
            if (tag[0] == "item" && _curContainer?[_curKey] is JsonObject cur)
                return kind == "npcs" ? IdStr(cur["current_area"])
                     : kind == "quests" ? IdStr(cur["neighboring_settlement_id"]) : null;
            return null;
        }

        // NPC インポート（ツール→NPCをインポートと同じ NpcImportDialog）。右クリック位置の所属エリアを配置先の初期値にする。
        private void ImportNpcFromTree()
        {
            if (_root?["npcs"] is not JsonObject || _root?["areas"] is not JsonObject || _root?["index"] is not JsonObject)
            { MessageBox.Show(I18n.T("msg.noNpcsAreasIndex")); return; }
            var worlds = NpcPortability.ListWorlds();
            if (worlds.Count == 0) { MessageBox.Show(I18n.T("npcimport.empty", NpcPortability.BaseDir())); return; }
            if (!_form.Apply()) return;
            using var dlg = new NpcImportDialog(_root, _worldDir, worlds,
                PresetAreaOf(_tree.SelectedNode?.Tag as string[], "npcs"));
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Populate();
            SelectByTag("item", "npcs", dlg.ImportedId);
            MessageBox.Show(dlg.ResultSummary, I18n.T("title.importDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 施設インポート（ツール→施設をインポートと同じ FacilityImportDialog）。
        // 施設ノードで呼ばれたらその施設を接続先の初期値に、area 項目ならそのエリアを配置先の初期値にする。
        private void ImportFacilityFromTree()
        {
            if (_root?["areas"] is not JsonObject) { MessageBox.Show(I18n.T("facimport.errNoContainers")); return; }
            var worlds = FacilityPortability.ListWorlds();
            if (worlds.Count == 0) { MessageBox.Show(I18n.T("facimport.empty", FacilityPortability.BaseDir())); return; }
            var tag = _tree.SelectedNode?.Tag as string[];
            string presetArea = null, presetConnect = null;
            if (tag != null && tag[0] == "facility") { presetArea = tag[1]; presetConnect = tag[3]; }
            else if (tag != null && tag[0] == "item" && tag[1] == "areas") presetArea = tag[2];
            if (!_form.Apply()) return;
            using var dlg = new FacilityImportDialog(_root, _worldDir, worlds, presetArea, presetConnect);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Populate();
            SelectByTag("facility", dlg.ImportedAreaId, dlg.ImportedNodeId, dlg.ImportedId);
            MessageBox.Show(dlg.ResultSummary, I18n.T("title.importDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 新しいエリア（拠点/ダンジョン）を作成して areas へ挿入する。
        private void CreateArea()
        {
            var areas = J.Obj(_root, "areas");
            var index = J.Obj(_root, "index");
            if (areas == null || index == null) { MessageBox.Show(I18n.T("msg.noAreasIndex")); return; }
            if (!_form.Apply()) return;
            using var d = new AreaCreateDialog();
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            var (id, area) = WorldRecordFactory.BuildArea(areas, index, d.AreaName, d.AreaSize);
            areas[id] = area;
            Populate();
            SelectByTag("item", "areas", id);
        }

        // 新しい NPC を作成して npcs へ挿入する（冒険者登録を選んだ場合は配置エリアの adventurer_npcs にも追加）。
        private void CreateNpc(string presetArea)
        {
            var npcs = J.Obj(_root, "npcs");
            var areas = J.Obj(_root, "areas");
            var index = J.Obj(_root, "index");
            if (npcs == null || areas == null || index == null) { MessageBox.Show(I18n.T("msg.noNpcsAreasIndex")); return; }
            if (!_form.Apply()) return;
            using var d = new NpcCreateDialog(areas, presetArea);
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            string id = NpcPortability.NextNpcId(index, npcs);
            npcs[id] = WorldRecordFactory.BuildNpc(id, d.NpcName, d.Category, d.Job, d.AreaId, d.FacilityId);
            if (d.AsAdventurer && areas[d.AreaId] is JsonObject area)
            {
                var arr = J.Arr(area, "adventurer_npcs");
                if (arr == null) { arr = new JsonArray(); area["adventurer_npcs"] = arr; }
                if (!arr.Any(x => x?.ToString() == id)) arr.Add(id);
            }
            Populate();
            SelectByTag("item", "npcs", id);
        }

        // クエスト作成ダイアログ（ツール→クエスト作成と同じ QuestCreator）を開いて挿入する。
        private void CreateQuestFromTree(string presetSettlement)
        {
            if (J.Obj(_root, "areas") == null || J.Obj(_root, "quests") == null)
            { MessageBox.Show(I18n.T("msg.noAreasQuests")); return; }
            if (!_form.Apply()) return;
            using var dlg = new QuestCreator(_root, presetSettlement);
            if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
            Populate();
            SelectByTag("item", "quests", dlg.CreatedQuestId);
            MessageBox.Show(dlg.CreatedSummary, I18n.T("title.created"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 新しい施設を作成して挿入する。施設ノードで呼ばれたらその施設と、area 項目で呼ばれたら
        // 入口ノードの入口施設と双方向に接続する（接続相手が無ければ未接続のまま挿入）。
        private void CreateFacility()
        {
            var tag = _tree.SelectedNode?.Tag as string[];
            var areas = J.Obj(_root, "areas");
            var index = J.Obj(_root, "index");
            if (tag == null || areas == null || index == null) { MessageBox.Show(I18n.T("msg.noAreasIndex")); return; }

            string areaId, nodeId, baseFid;
            if (tag[0] == "facility") { areaId = tag[1]; nodeId = tag[2]; baseFid = tag[3]; }
            else if (tag[0] == "item" && tag[1] == "areas")
            {
                areaId = tag[2];
                var ao = areas[areaId] as JsonObject;
                var nodes = J.Obj(ao, "nodes");
                if (nodes == null || nodes.Count == 0) { MessageBox.Show(I18n.T("msg.noNodes")); return; }
                nodeId = J.Str(ao, "entrance_node");
                if (nodes[nodeId] is not JsonObject) nodeId = nodes.First().Key;
                baseFid = J.Str(nodes[nodeId] as JsonObject, "entrance_facility");
            }
            else return;

            if (J.Obj(areas[areaId] as JsonObject, "nodes")?[nodeId] is not JsonObject node) return;
            var facs = J.Obj(node, "facilities");
            if (facs == null) { facs = new JsonObject(); node["facilities"] = facs; }

            if (!_form.Apply()) return;
            using var d = new FacilityCreateDialog();
            if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
            var (_, facIds) = WorldRecordFactory.CollectNodeFacilityIds(areas);
            string fid = QuestCreator.NextId(index, "facility", facIds.Contains);
            var fac = WorldRecordFactory.BuildFacility(fid, d.FacilityName, d.FacilityType);
            if (!string.IsNullOrEmpty(baseFid) && facs[baseFid] is JsonObject baseFac)
            {
                AddConnection(fac, baseFid);
                AddConnection(baseFac, fid);
            }
            facs[fid] = fac;
            Populate();
            SelectByTag("facility", areaId, nodeId, fid);
        }

        // free 施設のプログラムを開く。未設定なら "free_{施設ID}" で骨格を作って config.program_id を結び、
        // 設定済み（参照切れを含む）ならそのプログラムのツリーノードへ移動する。
        private void OpenOrCreateFreeProgram()
        {
            if (_curContainer?[_curKey] is not JsonObject fac || !FreeFacilityProgram.IsFree(fac)) return;
            if (!_form.Apply()) return;
            string pid = FreeFacilityProgram.ProgramIdOf(fac);
            var progs = FreeFacilityProgram.Programs(_root);
            if (progs?[pid] is not JsonObject)
            {
                progs = FreeFacilityProgram.Ensure(_root);
                pid = FreeFacilityProgram.NewId(progs, _curKey);
                progs[pid] = FreeFacilityProgram.NewProgram(J.Str(fac, "name"));
                FreeFacilityProgram.SetProgramId(fac, pid);
            }
            Populate();
            SelectByTag("item", FreeFacilityProgram.RootKey, pid);
        }

        // world 全体のどの施設からも参照されなくなったプログラムを削除する（施設削除の後始末）。
        // candidates は削除した施設が参照していたプログラム ID。
        private void PruneOrphanPrograms(IEnumerable<string> candidates)
        {
            var progs = FreeFacilityProgram.Programs(_root);
            if (progs == null) return;
            var used = new HashSet<string>(StringComparer.Ordinal);
            if (_root?["areas"] is JsonObject areas)
                foreach (var akv in areas)
                    foreach (var (_, fo) in EnumAreaFacilities(akv.Key))
                        if (FreeFacilityProgram.ProgramIdOf(fo) is string p && p.Length > 0) used.Add(p);
            foreach (var pid in candidates.Distinct())
                if (pid.Length > 0 && !used.Contains(pid)) progs.Remove(pid);
        }

        // Tag が一致するノードをツリー全体から探して選択する（新規作成した項目へフォーカスを移す用）。
        private void SelectByTag(params string[] tag)
        {
            TreeNode Find(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag is string[] t && t.SequenceEqual(tag)) return n;
                    if (Find(n.Nodes) is TreeNode c) return c;
                }
                return null;
            }
            if (Find(_tree.Nodes) is TreeNode found)
            {
                _tree.SelectedNode = found;
                found.EnsureVisible();
            }
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
            var tag = _tree.SelectedNode?.Tag as string[];
            var index = _root?["index"] as JsonObject;
            var areas = J.Obj(_root, "areas");

            // facility は ID がエリアを跨いで一意のため新規作成と同じグローバル採番を使い、
            // connections は相手側にも張って双方向に保つ（実データは非対称ゼロ）。
            if (tag != null && tag[0] == "facility")
            {
                if (_curContainer[_curKey]?.DeepClone() is not JsonObject fo) return;
                string fid = FacilityPortability.NextFacilityId(areas, index);
                fo["id"] = fid;
                _curContainer[fid] = fo;
                if (fo["connections"] is JsonArray conns)
                    foreach (var peerId in conns.Select(n => n?.ToString() ?? "").ToList())
                        if (EnumAreaFacilities(tag[1]).FirstOrDefault(p => p.id == peerId).fo is JsonObject peer)
                            AddConnection(peer, fid);
                CloneFacilityProgram(fo, fid);
                Populate(); SelectByTag("facility", tag[1], tag[2], fid);
                return;
            }

            if (tag != null && tag[0] == "item" && tag[1] == "areas") { DuplicateArea(index, areas); return; }

            string nk = DuplicateKeyFor(tag, index);
            var clone = _curContainer[_curKey]?.DeepClone();
            if (clone is JsonObject co && co.ContainsKey("id")) co["id"] = nk;
            // NPC はインベントリごと複製されるため、item ID を index.item で採番し直して
            // 複製元と同じ ID がセーブ内に二重に存在しないようにする（equipments も追従）。
            if (tag != null && tag[0] == "item" && tag[1] == "npcs" && clone is JsonObject npcClone)
                InventoryPanel.ReassignItemIds(_root, npcClone);
            _curContainer[nk] = clone;
            // 通常クエストは必ずどこかのエリアの quests に載る（実データ 78/78）。複製元と同じエリアへ登録する。
            if (tag != null && tag[0] == "item" && tag[1] == "quests") RegisterQuestLikeOriginal(_curKey, nk);
            Populate();
        }

        // 複製先のキー。ID を持つセクションは index カウンタで採番して既存 ID を飛ばす
        // （辞書内の最大+1 だとゲームが次に払い出す ID と衝突して上書きされる）。
        private string DuplicateKeyFor(string[] tag, JsonObject index)
        {
            if (index != null && tag != null && tag[0] == "item")
                switch (tag[1])
                {
                    case "npcs": return NpcPortability.NextNpcId(index, _curContainer);
                    case "quests": return QuestCreator.NextId(index, "quest", _curContainer.ContainsKey);
                }
            return NextKey(_curContainer);
        }

        // 複製元クエストを掲示しているエリアの quests に、複製したクエストも登録する。
        private void RegisterQuestLikeOriginal(string srcId, string newId)
        {
            if (_root?["areas"] is not JsonObject areas) return;
            foreach (var kv in areas)
            {
                if (kv.Value is not JsonObject a || J.Arr(a, "quests") is not JsonArray arr) continue;
                if (!arr.Any(n => (n?.ToString() ?? "") == srcId)) continue;
                if (!arr.Any(n => (n?.ToString() ?? "") == newId)) arr.Add(newId);
                return;
            }
        }

        // 複製した施設が free 施設なら、プログラム本体も複製して新しい ID で結び直す
        // （複製元と同じプログラムを指したままだと、片方の編集がもう片方にも及ぶ）。
        private void CloneFacilityProgram(JsonObject fac, string newFid)
        {
            string pid = FreeFacilityProgram.ProgramIdOf(fac);
            if (string.IsNullOrEmpty(pid)) return;
            var progs = FreeFacilityProgram.Ensure(_root);
            if (progs?[pid]?.DeepClone() is not JsonNode prog) return;   // 参照切れならそのまま
            string npid = FreeFacilityProgram.NewId(progs, newFid);
            progs[npid] = prog;
            FreeFacilityProgram.SetProgramId(fac, npid);
        }

        // エリアの複製。node/facility の ID はワールド全体で一意なので全て採番し直し、内部参照
        // （entrance_node / entrance_facility / 施設間 connections）を新 ID へ張り替える。
        // エリア間接続・掲示クエスト・NPC 一覧・施設の owner は複製元と共有できないため空にする。
        private void DuplicateArea(JsonObject index, JsonObject areas)
        {
            if (areas == null || index == null) { MessageBox.Show(I18n.T("msg.noAreasIndex")); return; }
            if (_curContainer[_curKey]?.DeepClone() is not JsonObject area) return;

            var (nodeIds, facIds) = WorldRecordFactory.CollectNodeFacilityIds(areas);
            string aid = QuestCreator.NextId(index, "area", areas.ContainsKey);
            area["id"] = aid;

            var facMap = new Dictionary<string, string>();
            var newNodes = new JsonObject();
            string entranceNode = null;
            foreach (var nkv in (J.Obj(area, "nodes") ?? new JsonObject()).ToList())
            {
                if (nkv.Value is not JsonObject nd) continue;
                string nid = QuestCreator.NextId(index, "node", nodeIds.Contains);
                nodeIds.Add(nid);
                nd["id"] = nid;
                entranceNode ??= nid;
                var newFacs = new JsonObject();
                foreach (var fkv in (J.Obj(nd, "facilities") ?? new JsonObject()).ToList())
                {
                    if (fkv.Value is not JsonObject fo) continue;
                    string fid = QuestCreator.NextId(index, "facility", facIds.Contains);
                    facIds.Add(fid);
                    facMap[fkv.Key] = fid;
                    fo["id"] = fid;
                    newFacs[fid] = fo;
                }
                nd["facilities"] = newFacs;
                newNodes[nid] = nd;
            }
            area["nodes"] = newNodes;

            // 旧 ID で書かれた内部参照を新 ID へ差し替える（対応の無い参照は落とす）。
            foreach (var nkv in newNodes)
            {
                if (nkv.Value is not JsonObject nd) continue;
                if (facMap.TryGetValue(J.Str(nd, "entrance_facility"), out var nef)) nd["entrance_facility"] = nef;
                if (J.Obj(nd, "facilities") is not JsonObject fs) continue;
                foreach (var fkv in fs)
                {
                    if (fkv.Value is not JsonObject fo) continue;
                    fo["owner"] = null;
                    CloneFacilityProgram(fo, fkv.Key);
                    if (fo["connections"] is not JsonArray conns) continue;
                    var mapped = new JsonArray();
                    foreach (var c in conns)
                        if (facMap.TryGetValue(c?.ToString() ?? "", out var nc)) mapped.Add(nc);
                    fo["connections"] = mapped;
                }
            }

            area["entrance_node"] = entranceNode;
            area["connections"] = new JsonArray();
            area["quests"] = new JsonArray();
            if (area.ContainsKey("resident_npcs")) area["resident_npcs"] = new JsonArray();
            if (area.ContainsKey("adventurer_npcs")) area["adventurer_npcs"] = new JsonArray();

            areas[aid] = area;
            Populate(); SelectByTag("item", "areas", aid);
        }

        // 選択中の項目を生 JSON で直接編集する。OK なら差し替えてフォームを再バインドし、ツリーの表示名も更新する。
        private void EditJson()
        {
            if (_curContainer == null || _curKey == null) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから開く
            var node = _tree.SelectedNode;
            using var d = new JsonEditDialog(I18n.T("title.editField", node?.Text ?? _curKey), _curContainer[_curKey]);
            if (d.ShowDialog(this) != DialogResult.OK) return;
            _curContainer[_curKey] = d.ResultNode;
            // ツリーの表示名を差し替え後の内容で更新する（グループ分け自体の変更はツリー再構築で反映）。
            if (node?.Tag is string[] tag)
            {
                if (tag[0] == "item") node.Text = SectionItemNode(tag[1], _curContainer, _curKey).Text;
                else if (tag[0] == "facility") node.Text = $"{_curKey}: {Label(_curContainer[_curKey])}";
            }
            OnSelect(node);   // 差し替え後のオブジェクトへフォームを再バインド
        }

        // 選択中の NPC を JSON＋画像の zip として npc\ ライブラリへエクスポートする。
        private void ExportNpc()
        {
            if (_curContainer == null || _curKey == null || _curContainer[_curKey] is not JsonObject npc) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから書き出す
            string name = J.Str(npc, "name", "npc");
            string source = J.Str(J.Obj(_root, "world_data"), "name");
            if (string.IsNullOrEmpty(source) && _worldDir != null) source = Path.GetFileName(_worldDir);

            string dest;
            try
            {
                dest = NpcPortability.FreeExportPath(name, NpcPortability.ExportWorldName(source));
                NpcPortability.Export(npc, _worldDir, source, _curKey, dest);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.exportFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(I18n.T("msg.npcExported", name, dest), I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 選択中の facility を JSON＋背景画像の zip として facility\ ライブラリへエクスポートする。
        // connections は移植先で無効な ID になるため保持しない（インポート時に接続先から張り直す）。
        // free 施設はプログラム本体（ルート直下の free_facility_programs）も同梱する。
        private void ExportFacility()
        {
            if (_curContainer == null || _curKey == null || _curContainer[_curKey] is not JsonObject fac) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから書き出す
            string name = J.Str(fac, "name", "facility");
            string source = J.Str(J.Obj(_root, "world_data"), "name");
            if (string.IsNullOrEmpty(source) && _worldDir != null) source = Path.GetFileName(_worldDir);

            string dest;
            try
            {
                dest = FacilityPortability.FreeExportPath(name, FacilityPortability.ExportWorldName(source));
                FacilityPortability.Export(fac, _worldDir, source, _curKey, dest, FreeFacilityProgram.Of(_root, fac));
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.exportFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(I18n.T("msg.facilityExported", name, dest), I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 選択中の NPC をキャラクタープリセットとして出力する（プレイヤーのプリセット化と同じ変換・出力先）。
        private void ExportNpcPreset()
        {
            if (_curContainer == null || _curKey == null || _curContainer[_curKey] is not JsonObject npc) return;
            if (!_form.Apply()) return;   // 表示中の編集を反映してから書き出す
            using var dlg = new PlayerToPresetDialog(npc, _filePath, _worldDir, isNpc: true);
            dlg.ShowDialog(FindForm());
        }

        // 選択レコード（item/facility）を確認の上で削除する。
        // area/facility はツリー上の下位ノードもまとめて削除する（確認文に注意書きを追加）。
        // facility の下位＝接続ツリーで配下に表示されている施設。area の下位（配下施設）は
        // area オブジェクト内に内包されているため本体削除で一緒に消える。
        // 削除後は、残る側の connections から削除済み ID への参照を取り除く。
        private void Delete()
        {
            if (_curContainer == null || _curKey == null) return;
            var node = _tree.SelectedNode;
            var tag = node?.Tag as string[];
            string msg = I18n.T("msg.deleteConfirm", _curKind, _curKey);
            if (node != null && node.Nodes.Count > 0 && tag != null
                && (tag[0] == "facility" || (tag[0] == "item" && tag[1] == "areas")))
            {
                msg += "\n" + I18n.T("msg.deleteCascadeNote");
                // 施設の接続ツリーは entrance_facility を根とする全域木なので、入口や
                // ハブを消すとエリアの施設がまとめて消える。件数と名前を出して気付けるようにする。
                if (tag[0] == "facility")
                {
                    var doomed = new List<string>();
                    CollectDescendantLabels(node, doomed);
                    if (doomed.Count > 0)
                        msg += "\n" + I18n.T("msg.deleteCascadeCount", (doomed.Count + 1).ToString(),
                            string.Join("\n", doomed.Take(15)) + (doomed.Count > 15 ? "\n…" : ""));
                }
            }
            if (MessageBox.Show(msg, I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            // 削除で参照されなくなる free 施設プログラムを拾っておく（削除後に PruneOrphanPrograms で始末する）。
            var programs = new List<string>();
            if (tag != null && tag[0] == "facility")
            {
                var removed = new List<string> { _curKey };
                programs.Add(FreeFacilityProgram.ProgramIdOf(_curContainer[_curKey] as JsonObject));
                CollectDescendantPrograms(node, programs);
                RemoveDescendantFacilities(node, removed);
                _curContainer.Remove(_curKey);
                foreach (var (_, fo) in EnumAreaFacilities(tag[1]))
                    foreach (var id in removed) RemoveConnection(fo, id);
                DetachFacilityRefs(tag[1], removed);
            }
            else
            {
                bool isArea = tag != null && tag[0] == "item" && tag[1] == "areas";
                // area 削除では配下の施設もまとめて消えるため、その全施設のプログラムを対象にする。
                if (isArea)
                    foreach (var (_, fo) in EnumAreaFacilities(_curKey))
                        programs.Add(FreeFacilityProgram.ProgramIdOf(fo));
                _curContainer.Remove(_curKey);
                if (isArea && _root?["areas"] is JsonObject areas)
                {
                    foreach (var kv in areas)
                        if (kv.Value is JsonObject o) RemoveConnection(o, _curKey);
                    DetachAreaRefs(_curKey);
                }
                else if (tag != null && tag[0] == "item" && tag[1] == "npcs") DetachNpcRefs(_curKey);
                // 通常クエストのみ。story_quests は areas[].quests から参照されず、ID 空間が
                // quests と重複するため、ここで消すと同番の通常クエストの掲示が失われる。
                else if (tag != null && tag[0] == "item" && tag[1] == "quests") DetachQuestRefs(_curKey);
            }
            PruneOrphanPrograms(programs);
            _form.Clear(); Populate();
        }

        // ---------------- 削除時の参照整理 ----------------
        // 実データでは NPC・施設・クエストへの参照は必ず実在する ID を指し、未設定は null で表される。
        // レコードを消したときに参照だけが残ると壊れたワールドになるため、配列からは要素を取り除き、
        // 単一値のフィールドは null に戻す。

        // 配列から id と一致する要素を（重複していても）すべて取り除く。
        private static void RemoveFromArray(JsonArray arr, string id)
        {
            if (arr == null) return;
            for (int i = arr.Count - 1; i >= 0; i--)
                if ((arr[i]?.ToString() ?? "") == id) arr.RemoveAt(i);
        }

        // 全エリアの facility を (ID, オブジェクト) で列挙する。
        private IEnumerable<(string id, JsonObject fo)> EnumAllFacilities()
        {
            if (_root?["areas"] is not JsonObject areas) yield break;
            foreach (var akv in areas)
                foreach (var pair in EnumAreaFacilities(akv.Key))
                    yield return pair;
        }

        // 削除した NPC への参照を外す（エリアの住民/冒険者一覧・施設の owner・パーティ）。
        private void DetachNpcRefs(string npcId)
        {
            if (_root?["areas"] is JsonObject areas)
                foreach (var kv in areas)
                {
                    if (kv.Value is not JsonObject a) continue;
                    RemoveFromArray(J.Arr(a, "resident_npcs"), npcId);
                    RemoveFromArray(J.Arr(a, "adventurer_npcs"), npcId);
                }
            foreach (var (_, fo) in EnumAllFacilities())
                if (J.Str(fo, "owner") == npcId) fo["owner"] = null;
            if (J.Obj(_root, "game_variables") is JsonObject gv)
            {
                RemoveFromArray(J.Arr(gv, "party"), npcId);
                RemoveFromArray(J.Arr(gv, "original_party"), npcId);
            }
        }

        // 削除したクエストへの参照を外す（エリアの掲示クエスト一覧）。
        private void DetachQuestRefs(string questId)
        {
            if (_root?["areas"] is not JsonObject areas) return;
            foreach (var kv in areas)
                if (kv.Value is JsonObject a) RemoveFromArray(J.Arr(a, "quests"), questId);
        }

        // 削除した施設への参照を外す（同エリア NPC の現在地/初期配置・ノードの入口施設）。
        private void DetachFacilityRefs(string areaId, IEnumerable<string> facIds)
        {
            var set = new HashSet<string>(facIds.Where(s => !string.IsNullOrEmpty(s)));
            if (set.Count == 0) return;
            if (_root?["npcs"] is JsonObject npcs)
                foreach (var kv in npcs)
                {
                    if (kv.Value is not JsonObject n) continue;
                    if (J.Str(n, "current_area") == areaId && set.Contains(J.Str(n, "current_location")))
                        n["current_location"] = null;
                    if (J.Obj(n, "initial_location") is JsonObject il
                        && J.Str(il, "area") == areaId && set.Contains(J.Str(il, "facility")))
                        il["facility"] = null;
                }
            if (J.Obj(J.Obj(_root, "areas")?[areaId] as JsonObject, "nodes") is JsonObject nodes)
                foreach (var nk in nodes)
                    if (nk.Value is JsonObject nd && set.Contains(J.Str(nd, "entrance_facility")))
                        nd["entrance_facility"] = null;
        }

        // 削除したエリアへの参照を外す（NPC の現在地・初期配置。配下施設も一緒に消えるため施設側も null）。
        private void DetachAreaRefs(string areaId)
        {
            if (_root?["npcs"] is not JsonObject npcs) return;
            foreach (var kv in npcs)
            {
                if (kv.Value is not JsonObject n) continue;
                if (J.Str(n, "current_area") == areaId)
                { n["current_area"] = null; n["current_location"] = null; }
                if (J.Obj(n, "initial_location") is JsonObject il && J.Str(il, "area") == areaId)
                { il["area"] = null; il["facility"] = null; }
            }
        }

        // ツリー上で node の配下に表示されている facility の表示名を再帰的に集める（削除確認の件数用）。
        private static void CollectDescendantLabels(TreeNode node, List<string> labels)
        {
            foreach (TreeNode c in node.Nodes)
            {
                if (c.Tag is string[] t && t[0] == "facility") labels.Add(c.Text);
                CollectDescendantLabels(c, labels);
            }
        }

        // ツリー上で node の配下に表示されている facility の program_id を再帰的に集める（削除前に呼ぶ）。
        private void CollectDescendantPrograms(TreeNode node, List<string> programs)
        {
            foreach (TreeNode c in node.Nodes)
            {
                if (c.Tag is string[] t && t[0] == "facility"
                    && J.Obj(J.Obj(J.Obj(_root, "areas")?[t[1]] as JsonObject, "nodes")?[t[2]] as JsonObject, "facilities") is JsonObject facs)
                    programs.Add(FreeFacilityProgram.ProgramIdOf(facs[t[3]] as JsonObject));
                CollectDescendantPrograms(c, programs);
            }
        }

        // ツリー上で node の配下に表示されている facility レコードを再帰的に削除し、削除 ID を removed へ積む。
        private void RemoveDescendantFacilities(TreeNode node, List<string> removed)
        {
            foreach (TreeNode c in node.Nodes)
            {
                if (c.Tag is string[] t && t[0] == "facility"
                    && J.Obj(J.Obj(J.Obj(_root, "areas")?[t[1]] as JsonObject, "nodes")?[t[2]] as JsonObject, "facilities") is JsonObject facs
                    && facs.ContainsKey(t[3]))
                { facs.Remove(t[3]); removed.Add(t[3]); }
                RemoveDescendantFacilities(c, removed);
            }
        }
    }
}
