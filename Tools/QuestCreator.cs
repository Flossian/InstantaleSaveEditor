// クエスト作成（合成ビルダー）。
// 文章類（タイトル・依頼主・概要・発言・舞台描写・場所）は自由入力で、各欄の「テンプレ」ボタンから
// 任意のクエスト・テンプレの該当値を選んで流し込める。enemies / boss / events は部品ライブラリ
// （templates/enemies・bosses・events）から選んで追加し、各要素は JSON 編集で微調整できる。
// 「作成」時に quest と、場所一覧から生成したダンジョン area を world へ挿入する。
// ID 採番はワールドの index カウンタ（area/node/facility/quest）を使い、ゲームと同じ規則で割り当てる。
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class QuestCreator : Form
    {
        // クエスト・テンプレ1件（文章の流用元・一括読込元）。
        private sealed class Template
        {
            public string Name, Source;
            public int? Difficulty;
            public JsonObject Quest, Dungeon;
        }

        private readonly JsonObject _root;                    // 挿入先のワールド（areas/quests/index を持つ）
        private readonly List<(string id, string name)> _settlements = new();  // 発注先候補（拠点 area の id/名前）
        private readonly List<Template> _templates = new();   // 文章流用・一括読込元のクエスト・テンプレ
        private readonly List<JsonObject> _enemyLib = new();   // 部品ライブラリ
        private readonly List<JsonObject> _bossLib = new();
        private readonly List<JsonObject> _eventLib = new();

        // 編集中のクエスト構成（UI 操作で随時更新し、Create() で quest へ書き出す）
        private readonly JsonArray _enemies = new();
        private readonly JsonArray _events = new();
        private JsonObject _boss;

        // 入力ウィジェット（コンストラクタで生成）
        private ComboBox _cbSettlement, _cbAtmo, _cbBgm;
        private NumericUpDown _numDiff;
        private TextBox _tbTitle, _tbClient, _tbDungeon, _tbLocations;
        private ResizableTextBox _summary, _statement, _layout;
        private ListBox _lstEnemies, _lstEvents;
        private Label _lblBoss;

        // 作成結果の要約。呼び出し元が成否メッセージとして表示する。
        public string CreatedSummary { get; private set; }

        public QuestCreator(JsonObject root)
        {
            _root = root;
            Text = I18n.T("quest.title");
            Width = 860; Height = 1000; StartPosition = FormStartPosition.CenterScreen;

            // 発注先になり得る拠点（町・村・都市）を一覧化
            foreach (var kv in J.Obj(_root, "areas") ?? new JsonObject())
                if (kv.Value is JsonObject a && J.Str(a, "size") is "town" or "village" or "city")
                    _settlements.Add((kv.Key, J.Str(a, "name", kv.Key)));

            LoadTemplates();
            LoadComponentLibs();

            // 3列レイアウト（ラベル / 入力 / テンプレ・操作ボタン）を縦スクロール内に積む
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            int r = 0;

            // 発注先・難易度・一括読込（一括読込ボタンは難易度行の右端に置く）
            _cbSettlement = Combo(t, ref r, I18n.T("quest.settlement"), _settlements.Select(s => $"{s.id}: {s.name}"));
            int diffRow = r;
            _numDiff = Num(t, ref r, I18n.T("quest.difficulty"), 1, 99, 4);
            var btnBulkLoad = new Button { Text = I18n.T("quest.bulkLoad"), Dock = DockStyle.Fill, AutoEllipsis = true, Margin = new Padding(3, 2, 3, 2) };
            btnBulkLoad.Click += (_, _) => BulkLoad();
            t.Controls.Add(btnBulkLoad, 2, diffRow);
            AddSectionHeader(t, ref r, I18n.T("quest.section.basic"));
            _tbTitle = TextRow(t, ref r, I18n.T("quest.fieldTitle"), q => J.Str(q, "quest_title"));
            _tbClient = TextRow(t, ref r, I18n.T("quest.clientName"), q => J.Str(q, "client_name"));
            _summary = MultiRow(t, ref r, I18n.T("quest.summary"), q => J.Str(q, "request_summary"));
            _statement = MultiRow(t, ref r, I18n.T("quest.statement"), q => J.Str(q, "client_statement"));

            AddSectionHeader(t, ref r, I18n.T("quest.section.stage"));
            _tbDungeon = TextRow(t, ref r, I18n.T("quest.dungeonName"), q => J.Str(J.Obj(q, "area"), "name"));
            _layout = MultiRow(t, ref r, I18n.T("quest.layout"), q => J.Str(J.Obj(q, "area"), "layout_description"));
            _tbLocations = TextRow(t, ref r, I18n.T("quest.locations"), q => LocationsCsv(J.Obj(q, "area")));
            _cbAtmo = Combo(t, ref r, I18n.T("quest.atmosphere"), Atmospheres());
            _cbAtmo.DropDownStyle = ComboBoxStyle.DropDown;
            _cbAtmo.SelectedIndexChanged += (_, _) => RefreshBgm();
            _cbAtmo.TextChanged += (_, _) => RefreshBgm();
            _cbBgm = Combo(t, ref r, I18n.T("quest.bgm"), Enumerable.Empty<string>());  // 候補は雰囲気に応じて RefreshBgm が絞り込む
            _cbBgm.DropDownStyle = ComboBoxStyle.DropDown;

            // 部品セクション
            AddComponentSection(t, ref r, I18n.T("quest.section.enemies"), isEvent: false, out _lstEnemies, _enemies, _enemyLib);
            AddBossSection(t, ref r);
            AddComponentSection(t, ref r, I18n.T("quest.section.events"), isEvent: true, out _lstEvents, _events, _eventLib);

            scroll.Controls.Add(t);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var btnCreate = new Button { Text = I18n.T("quest.create"), Width = 100 };
            var btnCancel = new Button { Text = I18n.T("btn.cancel"), Width = 100 };
            btnCreate.Click += (_, _) => Create();
            btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            bar.Controls.AddRange(new Control[] { btnCreate, btnCancel });

            Controls.Add(scroll);
            Controls.Add(bar);

            if (_cbSettlement.Items.Count > 0) _cbSettlement.SelectedIndex = 0;
            if (_cbAtmo.Items.Count > 0) _cbAtmo.SelectedIndex = 0;
            RefreshBgm();
        }

        // ---------------- テンプレ・部品ライブラリの読み込み ----------------
        private void LoadTemplates()
        {
            string dir = QuestsDir;
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                JsonObject o;
                try { o = JsonNode.Parse(File.ReadAllText(path))?.AsObject(); } catch { continue; }
                if (o?["quest"] is not JsonObject q) continue;
                _templates.Add(new Template
                {
                    Name = J.Str(o, "template_name", Path.GetFileNameWithoutExtension(path)),
                    Source = J.Str(o, "source_world"),
                    Difficulty = o["difficulty"] is JsonValue dv && dv.TryGetValue<int>(out var di) ? di : null,
                    Quest = q,
                    Dungeon = o["dungeon_area"] as JsonObject,
                });
            }
            _templates.Sort((a, b) =>
            {
                int c = string.Compare(a.Source, b.Source, StringComparison.Ordinal);
                return c != 0 ? c : (a.Difficulty ?? 0).CompareTo(b.Difficulty ?? 0);
            });
        }

        // 部品ライブラリ（enemies/bosses/events）の各 JSON ファイルを読み込む。
        private void LoadComponentLibs()
        {
            ReadLib(EnemiesDir, _enemyLib);
            ReadLib(BossesDir, _bossLib);
            ReadLib(EventsDir, _eventLib);
        }
        private static void ReadLib(string dir, List<JsonObject> into)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
            {
                try { if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) into.Add(o); } catch { }
            }
        }

        // ---------------- パス解決 ----------------
        // templates ルート（実行ファイル隣 → カレント → 開発時のソースツリーを遡って探す）。
        private static string FindTemplatesRoot()
        {
            var cands = new List<string>
            {
                Path.Combine(AppContext.BaseDirectory, "templates"),
                Path.Combine(Directory.GetCurrentDirectory(), "templates"),
            };
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                cands.Add(Path.Combine(dir, "templates"));
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return cands.FirstOrDefault(Directory.Exists);
        }
        public static string TemplatesRoot()
            => FindTemplatesRoot() ?? Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "templates")).FullName;
        private static string QuestsDir => Path.Combine(TemplatesRoot(), "quests");
        private static string EnemiesDir => Path.Combine(TemplatesRoot(), "enemies");
        private static string BossesDir => Path.Combine(TemplatesRoot(), "bosses");
        private static string EventsDir => Path.Combine(TemplatesRoot(), "events");

        // ---------------- テンプレ・部品抽出（world_data / savedata → 各 JSON） ----------------
        // quest テンプレと enemy/boss/event 部品を一括で書き出す。戻り値=各件数。
        public static (int quests, int enemies, int bosses, int events, int skipped) ExtractAll(JsonObject root, string source)
        {
            var areas = J.Obj(root, "areas");
            var quests = J.Obj(root, "quests");
            if (quests == null) return (0, 0, 0, 0, 0);
            Directory.CreateDirectory(QuestsDir);
            Directory.CreateDirectory(EnemiesDir);
            Directory.CreateDirectory(BossesDir);
            Directory.CreateDirectory(EventsDir);

            int qn = 0, en = 0, bn = 0, vn = 0, skipped = 0;
            var seenQ = new HashSet<string>();
            var seenE = new HashSet<string>();
            var seenB = new HashSet<string>();
            var seenV = new HashSet<string>();

            foreach (var kv in quests)
            {
                if (kv.Value is not JsonObject q) continue;

                // 部品（敵・ボス・イベント）を名前単位で書き出し
                foreach (var el in J.Arr(q, "enemies") ?? new JsonArray())
                    if (el is JsonObject eo && J.Str(J.Obj(eo, "data"), "name") is { Length: > 0 } nm && seenE.Add(nm))
                    { File.WriteAllText(Path.Combine(EnemiesDir, Sanitize(nm) + ".json"), eo.ToJsonString(Codec.Pretty)); en++; }
                if (J.Obj(q, "boss") is JsonObject bo && J.Str(J.Obj(bo, "data"), "name") is { Length: > 0 } bnm && seenB.Add(bnm))
                { File.WriteAllText(Path.Combine(BossesDir, Sanitize(bnm) + ".json"), bo.ToJsonString(Codec.Pretty)); bn++; }
                foreach (var el in J.Arr(q, "events") ?? new JsonArray())
                    if (el is JsonObject vo && J.Str(vo, "event_name") is { Length: > 0 } vnm && seenV.Add(vnm))
                    { File.WriteAllText(Path.Combine(EventsDir, Sanitize(vnm) + ".json"), vo.ToJsonString(Codec.Pretty)); vn++; }

                // quest テンプレ（ダンジョン area が紐づくもののみ）
                string title = J.Str(q, "quest_title", "quest_" + kv.Key);
                if (!seenQ.Add(title)) continue;
                if (areas?[J.Str(q, "quest_area_id")] is not JsonObject darea) { skipped++; continue; }
                var quest = (JsonObject)q.DeepClone();
                quest["id"] = ""; quest["quest_area_id"] = ""; quest["neighboring_settlement_id"] = "";
                var cfg = J.Obj(quest, "config") ?? new JsonObject(); cfg["status"] = "incomplete"; quest["config"] = cfg;
                var tmpl = new JsonObject
                {
                    ["template_name"] = title,
                    ["source_world"] = source,
                    ["difficulty"] = q["difficulty"]?.DeepClone(),
                    ["atmosphere"] = J.Str(J.Obj(q, "area"), "atomosphere"),
                    ["quest"] = quest,
                    ["dungeon_area"] = (JsonObject)darea.DeepClone(),
                };
                File.WriteAllText(Path.Combine(QuestsDir, $"{Sanitize(source)}__{kv.Key}__{Sanitize(title)}.json"), tmpl.ToJsonString(Codec.Pretty));
                qn++;
            }
            return (qn, en, bn, vn, skipped);
        }

        // 作成したクエストを quest テンプレ（templates/quests/*.json）として書き出す。
        // ExtractAll と同じ構造で、ID 類は空にして流用元に向く形に整える。失敗しても作成は止めない。
        private void SaveAsTemplate(JsonObject quest, JsonObject area)
        {
            try
            {
                Directory.CreateDirectory(QuestsDir);
                string title = J.Str(quest, "quest_title", "quest");
                var q = (JsonObject)quest.DeepClone();
                q["id"] = ""; q["quest_area_id"] = ""; q["neighboring_settlement_id"] = "";
                var cfg = J.Obj(q, "config") ?? new JsonObject(); cfg["status"] = "incomplete"; q["config"] = cfg;
                var tmpl = new JsonObject
                {
                    ["template_name"] = title,
                    ["source_world"] = "作成",
                    ["difficulty"] = (int)_numDiff.Value,
                    ["atmosphere"] = _cbAtmo.Text.Trim(),
                    ["quest"] = q,
                    ["dungeon_area"] = (JsonObject)area.DeepClone(),
                };
                string json = tmpl.ToJsonString(Codec.Pretty);
                File.WriteAllText(Path.Combine(QuestsDir, $"作成__{Sanitize(title)}__{ShortHash(json)}.json"), json);
            }
            catch { }
        }

        // 文字列の MD5 を取り、先頭8桁(16進)を返す。テンプレ名の重複回避用。
        private static string ShortHash(string s)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(s));
            return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        }

        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "item";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, ' ');
            s = string.Join("_", s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        // ---------------- 一括読込（テンプレ→全欄） ----------------
        // 選んだクエスト・テンプレの全項目（文章・舞台・敵/ボス/イベント）を各欄へ一気に流し込む。
        private void BulkLoad()
        {
            if (_templates.Count == 0) { MessageBox.Show(I18n.T("quest.noTemplates")); return; }
            var labels = _templates.Select(t => I18n.T("quest.bulkLabel", t.Source, t.Name, t.Difficulty)).ToList();
            var previews = _templates.Select(t => BuildPreview(t.Quest)).ToList();
            int i = ListPicker.PickOne(this, I18n.T("quest.bulkPickTitle"), labels, previews);
            if (i < 0) return;
            var q = _templates[i].Quest;
            var area = J.Obj(q, "area");
            _tbTitle.Text = J.Str(q, "quest_title");
            _tbClient.Text = J.Str(q, "client_name");
            _summary.Box.Text = J.Str(q, "request_summary");
            _statement.Box.Text = J.Str(q, "client_statement");
            _tbDungeon.Text = J.Str(area, "name");
            _layout.Box.Text = J.Str(area, "layout_description");
            _tbLocations.Text = LocationsCsv(area);
            _cbAtmo.Text = J.Str(area, "atomosphere");
            if (_templates[i].Difficulty is int d) _numDiff.Value = Math.Min(_numDiff.Maximum, Math.Max(_numDiff.Minimum, d));
            if (_templates[i].Dungeon is JsonObject dg) _cbBgm.Text = J.Str(dg, "bgm");

            _enemies.Clear();
            foreach (var e in J.Arr(q, "enemies") ?? new JsonArray()) _enemies.Add(e?.DeepClone());
            _events.Clear();
            foreach (var e in J.Arr(q, "events") ?? new JsonArray()) _events.Add(e?.DeepClone());
            _boss = J.Obj(q, "boss")?.DeepClone() as JsonObject;
            if (_boss != null && J.Obj(_boss, "data") == null) _boss = null;
            RefreshList(_lstEnemies, _enemies, e => EnemyName(e));
            RefreshList(_lstEvents, _events, e => J.Str(e as JsonObject, "event_name", I18n.T("label.unnamed")));
            RefreshBoss();
        }

        // ---------------- 生成・挿入 ----------------
        // 入力内容から quest と ダンジョン area を組み立て、index で採番して world へ挿入する。
        private void Create()
        {
            // 必須項目の検証
            if (_cbSettlement.SelectedIndex < 0) { MessageBox.Show(I18n.T("quest.errSettlement")); return; }
            if (string.IsNullOrWhiteSpace(_tbTitle.Text)) { MessageBox.Show(I18n.T("quest.errTitle")); return; }
            if (string.IsNullOrWhiteSpace(_tbDungeon.Text)) { MessageBox.Show(I18n.T("quest.errDungeon")); return; }

            var areas = J.Obj(_root, "areas"); var quests = J.Obj(_root, "quests"); var index = J.Obj(_root, "index");
            if (areas == null || quests == null) { MessageBox.Show(I18n.T("quest.errNoAreasQuests")); return; }
            if (index == null) { MessageBox.Show(I18n.T("quest.errNoIndex")); return; }

            string sid = _settlements[_cbSettlement.SelectedIndex].id;   // 発注拠点 area の ID
            string areaId = NextId(index, "area", areas.ContainsKey);
            string questId = NextId(index, "quest", quests.ContainsKey);

            // 場所欄（カンマ区切り）を配列化。area の locations と facility 生成の両方に使う。
            var locations = new JsonArray();
            var locList = new List<string>();
            foreach (var loc in _tbLocations.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            { locations.Add(loc); locList.Add(loc); }

            // ダンジョン area を場所一覧から生成し、index で採番
            var area = BuildDungeonArea(_tbDungeon.Text.Trim(), _layout.Box.Text, locList, _cbBgm.Text.Trim());
            area["id"] = areaId;
            RenumberDungeon(area, index, areas);

            var quest = new JsonObject
            {
                ["quest_title"] = _tbTitle.Text,
                ["client_name"] = _tbClient.Text,
                ["request_summary"] = _summary.Box.Text,
                ["client_statement"] = _statement.Box.Text,
                ["area"] = new JsonObject
                {
                    ["name"] = _tbDungeon.Text.Trim(),
                    ["layout_description"] = _layout.Box.Text,
                    ["locations"] = locations,
                    ["atomosphere"] = _cbAtmo.Text.Trim(),   // 元データの綴り
                },
                ["events"] = (JsonArray)_events.DeepClone(),
                ["enemies"] = (JsonArray)_enemies.DeepClone(),
                ["boss"] = _boss != null ? (JsonObject)_boss.DeepClone() : new JsonObject(),
                ["difficulty"] = (int)_numDiff.Value,
                ["neighboring_settlement_id"] = sid,
                ["id"] = questId,
                ["quest_type"] = "normal_quest",
                ["config"] = new JsonObject { ["status"] = "incomplete", ["level_of_detail"] = 1 },
                ["quest_area_id"] = areaId,
            };

            // world へ反映：area と quest を登録し、発注拠点の quests に新クエストを追加
            // （拠点に quests 配列が無ければ新設する。黙って登録漏れにしない）。connections は触らない。
            areas[areaId] = area;
            quests[questId] = quest;
            if (areas[sid] is JsonObject settlement)
            {
                var qarr = J.Arr(settlement, "quests");
                if (qarr == null) { qarr = new JsonArray(); settlement["quests"] = qarr; }
                qarr.Add(questId);
            }

            SaveAsTemplate(quest, area);   // 作成したクエストをテンプレとしても保存（次回以降の流用元になる）

            CreatedSummary = I18n.T("quest.createdSummary", questId, _tbTitle.Text, areaId, _tbDungeon.Text.Trim(), sid,
                                    _enemies.Count, _events.Count, _boss != null ? I18n.T("quest.bossYes") : I18n.T("quest.bossNo"));
            DialogResult = DialogResult.OK;
            Close();
        }

        // 場所一覧（入口＋各場所）からダンジョン area を生成する。ID は仮値で、RenumberDungeon が採番し直す。
        private static JsonObject BuildDungeonArea(string name, string layout, List<string> locs, string bgm)
        {
            var facilities = new JsonObject();
            int fid = 1;
            string entrance = fid.ToString();
            facilities[entrance] = Facility(entrance, name + " - Entrance"); fid++;
            foreach (var loc in locs) { string id = fid.ToString(); facilities[id] = Facility(id, loc); fid++; }

            var node = new JsonObject
            {
                ["name"] = "",
                ["id"] = "1",
                ["overview"] = "",
                ["facilities"] = facilities,
                ["connections"] = new JsonArray(),
                ["entrance_facility"] = entrance,
                ["config"] = new JsonObject { ["level_of_detail"] = 0 },
            };
            return new JsonObject
            {
                ["name"] = name,
                ["id"] = "",
                ["descriptions"] = new JsonObject { ["overview"] = null, ["facilities"] = null, ["area_description"] = layout },
                ["size"] = "dungeon",
                ["resident_npcs"] = new JsonArray(),
                ["adventurer_npcs"] = new JsonArray(),
                ["connections"] = new JsonArray(),
                ["nodes"] = new JsonObject { ["1"] = node },
                ["quests"] = new JsonArray(),
                ["labors"] = new JsonObject { ["created_date"] = 0, ["posts"] = new JsonArray() },
                ["entrance_node"] = "1",
                ["config"] = new JsonObject { ["level_of_detail"] = "1" },
                ["bgm"] = bgm,
            };
        }
        private static JsonObject Facility(string id, string name) => new()
        {
            ["name"] = name,
            ["id"] = id,
            ["description"] = "",
            ["facility_type"] = "dungeon_location",
            ["tier"] = null,
            ["owner"] = null,
            ["connections"] = new JsonArray(),
            ["config"] = new JsonObject { ["level_of_detail"] = 0 },
        };

        // ダンジョン area 内の node / facility に新 ID を採番し、参照を貼り替える。
        private static void RenumberDungeon(JsonObject area, JsonObject index, JsonObject allAreas)
        {
            // node / facility は全 area 横断の通し番号。既存 ID を集めて衝突を避ける。
            var nodeIds = new HashSet<string>();
            var facIds = new HashSet<string>();
            foreach (var aKv in allAreas)
            {
                if (aKv.Value is not JsonObject a) continue;
                foreach (var nKv in J.Obj(a, "nodes") ?? new JsonObject())
                {
                    nodeIds.Add(nKv.Key);
                    if (nKv.Value is JsonObject nObj)
                        foreach (var fKv in J.Obj(nObj, "facilities") ?? new JsonObject())
                            facIds.Add(fKv.Key);
                }
            }

            var facMap = new Dictionary<string, string>();
            var newNodes = new JsonObject();
            string entranceNode = null;

            foreach (var nodeKv in (J.Obj(area, "nodes") ?? new JsonObject()).ToList())
            {
                var node = (JsonObject)nodeKv.Value.DeepClone();
                string newNodeId = NextId(index, "node", nodeIds.Contains);
                node["id"] = newNodeId;
                entranceNode ??= newNodeId;
                var newFacs = new JsonObject();
                foreach (var facKv in (J.Obj(node, "facilities") ?? new JsonObject()).ToList())
                {
                    var fac = (JsonObject)facKv.Value.DeepClone();
                    string newFacId = NextId(index, "facility", facIds.Contains);
                    facMap[facKv.Key] = newFacId;
                    fac["id"] = newFacId;
                    newFacs[newFacId] = fac;
                }
                node["facilities"] = newFacs;
                newNodes[newNodeId] = node;
            }
            foreach (var nodeKv in newNodes)
            {
                if (nodeKv.Value is not JsonObject node) continue;
                if (node["entrance_facility"]?.ToString() is string ef && facMap.TryGetValue(ef, out var nef))
                    node["entrance_facility"] = nef;
                RemapArray(node["connections"] as JsonArray, facMap);
                foreach (var facKv in (J.Obj(node, "facilities") ?? new JsonObject()))
                    if (facKv.Value is JsonObject fac) RemapArray(fac["connections"] as JsonArray, facMap);
            }
            area["nodes"] = newNodes;
            if (entranceNode != null) area["entrance_node"] = entranceNode;
        }
        private static void RemapArray(JsonArray arr, Dictionary<string, string> map)
        {
            if (arr == null) return;
            for (int i = 0; i < arr.Count; i++)
                if (arr[i]?.ToString() is string s && map.TryGetValue(s, out var nv)) arr[i] = nv;
        }
        // index カウンタから新 ID を採番する。taken（既存 ID 判定）が指定された場合、
        // 既に使われている ID は飛ばすので、カウンタがデータとずれていても上書きしない。
        private static string NextId(JsonObject index, string key, Func<string, bool> taken = null)
        {
            int cur = (int)J.Int(index, key, 0);
            while (taken != null && taken(cur.ToString())) cur++;
            index[key] = cur + 1;
            return cur.ToString();
        }

        // ---------------- 部品セクション UI ----------------
        // 追加（ライブラリ）/ 削除 / JSON編集 ボタン付きのリスト欄を1ブロック追加する。
        // isEvent はイベント/敵の種別を明示する（表示名の翻訳に依存しない）。
        private void AddComponentSection(TableLayoutPanel t, ref int r, string title, bool isEvent, out ListBox list, JsonArray model, List<JsonObject> lib)
        {
            AddSectionHeader(t, ref r, "── " + title + " ──");
            var lst = new ListBox { Dock = DockStyle.Fill, Height = 90, IntegralHeight = false };
            list = lst;
            t.Controls.Add(lst, 0, r); t.SetColumnSpan(lst, 2);
            var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            var add = new Button { Text = I18n.T("btn.add"), Width = 92 };
            var del = new Button { Text = I18n.T("btn.delete"), Width = 92 };
            var edit = new Button { Text = I18n.T("quest.btnJsonEdit"), Width = 92 };
            Func<JsonObject, string> namer = isEvent ? (e => J.Str(e, "event_name", I18n.T("label.unnamed"))) : (e => EnemyName(e));
            add.Click += (_, _) =>
            {
                foreach (var sel in PickComponents(title, lib, isEvent, multi: true)) model.Add(sel);
                RefreshList(lst, model, namer);
            };
            del.Click += (_, _) =>
            {
                int i = lst.SelectedIndex;
                if (i >= 0 && i < model.Count) { model.RemoveAt(i); RefreshList(lst, model, namer); }
            };
            edit.Click += (_, _) =>
            {
                int i = lst.SelectedIndex;
                if (i < 0 || i >= model.Count) return;
                using var d = new JsonEditDialog(I18n.T("title.editField", title), model[i]);
                if (d.ShowDialog(this) == DialogResult.OK && d.ResultNode != null)
                { model[i] = d.ResultNode; RefreshList(lst, model, namer); }
            };
            btns.Controls.AddRange(new Control[] { add, del, edit });
            t.Controls.Add(btns, 2, r); r++;
        }

        private void AddBossSection(TableLayoutPanel t, ref int r)
        {
            AddSectionHeader(t, ref r, "── " + I18n.T("quest.section.boss") + " ──");
            _lblBoss = new Label { Dock = DockStyle.Fill, Text = I18n.T("label.none"), AutoEllipsis = true, Padding = new Padding(4, 6, 0, 0), BorderStyle = BorderStyle.FixedSingle };
            t.Controls.Add(_lblBoss, 0, r); t.SetColumnSpan(_lblBoss, 2);
            var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
            var sel = new Button { Text = I18n.T("quest.select"), Width = 92 };
            var none = new Button { Text = I18n.T("quest.none"), Width = 92 };
            var edit = new Button { Text = I18n.T("quest.btnJsonEdit"), Width = 92 };
            sel.Click += (_, _) =>
            {
                var picked = PickComponents(I18n.T("quest.section.boss"), _bossLib, isEvent: false, multi: false).FirstOrDefault();
                if (picked != null) { _boss = picked; RefreshBoss(); }
            };
            none.Click += (_, _) => { _boss = null; RefreshBoss(); };
            edit.Click += (_, _) =>
            {
                using var d = new JsonEditDialog(I18n.T("quest.editBoss"), _boss ?? new JsonObject());
                if (d.ShowDialog(this) == DialogResult.OK && d.ResultNode is JsonObject o) { _boss = o; RefreshBoss(); }
            };
            btns.Controls.AddRange(new Control[] { sel, none, edit });
            t.Controls.Add(btns, 2, r); r++;
        }

        private void RefreshBoss() => _lblBoss.Text = _boss != null ? EnemyName(_boss) : I18n.T("label.none");
        private static string EnemyName(JsonNode el) => J.Str(J.Obj(el as JsonObject, "data"), "name", I18n.T("label.unnamed"));
        private static void RefreshList(ListBox lst, JsonArray model, Func<JsonObject, string> namer)
        {
            lst.Items.Clear();
            foreach (var e in model) lst.Items.Add(namer(e as JsonObject ?? new JsonObject()));
        }

        // ライブラリから要素を選ぶ（multi=true なら複数選択）。選んだ要素はクローンして返す。
        // isEvent はイベント/敵の種別を明示する（表示名の翻訳に依存しない）。
        private List<JsonObject> PickComponents(string title, List<JsonObject> lib, bool isEvent, bool multi)
        {
            var result = new List<JsonObject>();
            if (lib.Count == 0) { MessageBox.Show(I18n.T("quest.libEmpty", title)); return result; }
            var labels = lib.Select(e => isEvent ? J.Str(e, "event_name", I18n.T("label.unnamed")) : EnemyName(e)).ToList();
            var previews = lib.Select(e => e.ToJsonString(Codec.Pretty)).ToList();
            foreach (int i in ListPicker.Pick(this, I18n.T("quest.pickTitle", title), labels, previews, multi))
                if (i >= 0 && i < lib.Count) result.Add((JsonObject)lib[i].DeepClone());
            return result;
        }

        // ---------------- 文章欄の小物 ----------------
        private static string LocationsCsv(JsonObject area)
            => string.Join(", ", (J.Arr(area, "locations") ?? new JsonArray()).Select(x => x?.ToString()));

        private IEnumerable<string> Atmospheres()
        {
            var set = new List<string> { "dangerous", "scary", "eerie", "mystic" };
            foreach (var t in _templates)
            {
                string a = J.Str(J.Obj(t.Quest, "area"), "atomosphere");
                if (a.Length > 0 && !set.Contains(a)) set.Add(a);
            }
            return set;
        }
        // 現在の雰囲気に合う BGM だけをテンプレのダンジョンから集めて候補を作り直す。
        private void RefreshBgm()
        {
            string atmo = _cbAtmo.Text.Trim();
            var opts = new List<string>();
            foreach (var t in _templates)
            {
                if (t.Dungeon == null) continue;
                string b = J.Str(t.Dungeon, "bgm");
                if (b.Length == 0) continue;
                if (atmo.Length > 0 && !b.Contains("/" + atmo + "/")) continue;
                if (!opts.Contains(b)) opts.Add(b);
            }
            string cur = _cbBgm.Text;
            _cbBgm.Items.Clear();
            foreach (var o in opts) _cbBgm.Items.Add(o);
            if (!string.IsNullOrEmpty(cur)) _cbBgm.Text = cur;
            else if (opts.Count > 0) _cbBgm.SelectedIndex = 0;
        }

        // テンプレの要点を1枚にまとめる（一括読込ピッカーのプレビュー用）。
        private static string BuildPreview(JsonObject q)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("■ " + J.Str(q, "quest_title"));
            sb.AppendLine(I18n.T("quest.preview.client") + J.Str(q, "client_name"));
            sb.AppendLine();
            sb.AppendLine(J.Str(q, "request_summary"));
            sb.AppendLine();
            if (J.Obj(q, "area") is JsonObject area)
            {
                sb.AppendLine(I18n.T("quest.preview.stage") + J.Str(area, "name"));
                sb.AppendLine(J.Str(area, "layout_description"));
                if (J.Arr(area, "locations") is JsonArray locs && locs.Count > 0)
                    sb.AppendLine(I18n.T("quest.preview.locations") + LocationsCsv(area));
                sb.AppendLine();
            }
            if (J.Arr(q, "enemies") is JsonArray en && en.Count > 0)
                sb.AppendLine(I18n.T("quest.preview.enemies") + string.Join(", ", en.Select(EnemyName)));
            if (J.Obj(q, "boss") is JsonObject boss && J.Obj(boss, "data") != null)
                sb.AppendLine(I18n.T("quest.preview.boss") + EnemyName(boss));
            if (J.Arr(q, "events") is JsonArray evs && evs.Count > 0)
                sb.AppendLine(I18n.T("quest.preview.events") + string.Join(", ", evs.Select(e => J.Str(e as JsonObject, "event_name"))));
            return sb.ToString();
        }

        // ---------------- UI ヘルパ ----------------
        private static void AddSectionHeader(TableLayoutPanel t, ref int r, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Padding = new Padding(0, 10, 0, 2) };
            t.Controls.Add(l, 0, r); t.SetColumnSpan(l, 3); r++;
        }
        private ComboBox Combo(TableLayoutPanel t, ref int r, string label, IEnumerable<string> items)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach (var it in items) cb.Items.Add(it);
            t.Controls.Add(cb, 1, r); r++;
            return cb;
        }
        private NumericUpDown Num(TableLayoutPanel t, ref int r, string label, int min, int max, int val)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = val, Width = 80 };
            t.Controls.Add(n, 1, r); r++;
            return n;
        }
        // テキスト欄＋右に「テンプレ」ボタン（getter で各テンプレの該当値を選び流し込む）。
        private TextBox TextRow(TableLayoutPanel t, ref int r, string label, Func<JsonObject, string> getter)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var tb = new TextBox { Dock = DockStyle.Fill };
            t.Controls.Add(tb, 1, r);
            t.Controls.Add(TemplateBtn(label, getter, v => tb.Text = v), 2, r); r++;
            return tb;
        }
        private ResizableTextBox MultiRow(TableLayoutPanel t, ref int r, string label, Func<JsonObject, string> getter)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var rtb = new ResizableTextBox(460, 70) { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 8) };
            t.Controls.Add(rtb, 1, r);
            t.Controls.Add(TemplateBtn(label, getter, v => rtb.Box.Text = v), 2, r); r++;
            return rtb;
        }
        private Button TemplateBtn(string label, Func<JsonObject, string> getter, Action<string> setter)
        {
            var b = new Button { Text = I18n.T("quest.templateBtn"), Width = 92, Margin = new Padding(3, 2, 3, 2) };
            b.Click += (_, _) =>
            {
                if (_templates.Count == 0) { MessageBox.Show(I18n.T("quest.noTemplates")); return; }
                var labels = _templates.Select(t => $"[{t.Source}] {t.Name}").ToList();
                var vals = _templates.Select(t => getter(t.Quest) ?? "").ToList();
                int i = ListPicker.PickOne(this, I18n.T("quest.templatePickTitle", label), labels, vals);
                if (i >= 0) setter(vals[i]);
            };
            return b;
        }
        private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Padding = new Padding(2, 6, 4, 0) };
    }

    // ---------------- 一覧から選ぶ汎用ダイアログ（左=項目、右=プレビュー） ----------------
    internal sealed class ListPicker : Form
    {
        private readonly ListBox _single;
        private readonly CheckedListBox _multi;
        private readonly TextBox _prev;
        private readonly IList<string> _previews;

        private ListPicker(string title, IList<string> labels, IList<string> previews, bool multi)
        {
            _previews = previews;
            Text = title; Width = 760; Height = 540; StartPosition = FormStartPosition.CenterParent;

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
            Load += (_, _) => { try { split.SplitterDistance = 320; } catch { } };

            if (multi)
            {
                _multi = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
                foreach (var l in labels) _multi.Items.Add(l);
                _multi.SelectedIndexChanged += (_, _) => ShowPrev(_multi.SelectedIndex);
                split.Panel1.Controls.Add(_multi);
            }
            else
            {
                _single = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
                foreach (var l in labels) _single.Items.Add(l);
                _single.SelectedIndexChanged += (_, _) => ShowPrev(_single.SelectedIndex);
                _single.DoubleClick += (_, _) => { if (_single.SelectedIndex >= 0) { DialogResult = DialogResult.OK; Close(); } };
                split.Panel1.Controls.Add(_single);
            }

            _prev = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window, WordWrap = true };
            split.Panel2.Controls.Add(_prev);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var ok = new Button { Text = I18n.T("btn.ok"), Width = 90, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Width = 90, DialogResult = DialogResult.Cancel };
            bar.Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok; CancelButton = cancel;

            Controls.Add(split);
            Controls.Add(bar);
        }

        private void ShowPrev(int i) => _prev.Text = (i >= 0 && i < _previews.Count) ? _previews[i] : "";

        // 複数選択。チェックした項目のインデックス列を返す（multi=false なら選択1件）。
        public static IEnumerable<int> Pick(IWin32Window owner, string title, IList<string> labels, IList<string> previews, bool multi)
        {
            using var d = new ListPicker(title, labels, previews, multi);
            if (d.ShowDialog(owner) != DialogResult.OK) return Enumerable.Empty<int>();
            if (multi) return d._multi.CheckedIndices.Cast<int>().ToList();
            return d._single.SelectedIndex >= 0 ? new[] { d._single.SelectedIndex } : Enumerable.Empty<int>();
        }
        // 単一選択。選んだインデックス、未選択なら -1。
        public static int PickOne(IWin32Window owner, string title, IList<string> labels, IList<string> previews)
            => Pick(owner, title, labels, previews, false).DefaultIfEmpty(-1).First();
    }
}
