// クエスト作成ダイアログ。
// テンプレ（種別・雰囲気・依頼主役職）を組み合わせて素案テキストを生成し、
// ダンジョン(area) と quest を連結して world データへ挿入する。
// 生成の細部(events/enemies/boss)は空テンプレのまま。後で LLM に埋めさせる前提。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class QuestCreator : Form
    {
        // クエスト種別テンプレ: タイトル / 概要 / 依頼文(箇条書き) のひな形。{place}{target}を置換。
        private sealed class Archetype
        {
            public string Label, TitlePat, SummaryPat, StatementPat, Atmosphere; public int Difficulty;
        }
        private static readonly Archetype[] Archetypes =
        {
            new(){ Label="討伐", Atmosphere="dangerous", Difficulty=4,
                   TitlePat="{place}の{target}討伐",
                   SummaryPat="{place}に巣食う{target}が周辺を脅かしている。討伐し、安全を取り戻してほしい。",
                   StatementPat="- {target}による被害が増えている。\n- 特に強力な個体が目撃されている。\n- {place}の奥に潜んでいるようだ。" },
            new(){ Label="回収", Atmosphere="eerie", Difficulty=3,
                   TitlePat="{place}からの{target}回収",
                   SummaryPat="{place}で失われた{target}の回収を依頼したい。危険を承知で向かってほしい。",
                   StatementPat="- {target}は{place}の最奥にあるとされる。\n- 先発した者は戻っていない。\n- 持ち帰れば相応の報酬を約束する。" },
            new(){ Label="探索", Atmosphere="eerie", Difficulty=3,
                   TitlePat="{place}の探索",
                   SummaryPat="未踏の{place}を調査し、内部の状況を報告してほしい。",
                   StatementPat="- {place}の地図が存在しない。\n- 異変の兆候が報告されている。\n- 生還を最優先に。" },
            new(){ Label="調査", Atmosphere="scary", Difficulty=4,
                   TitlePat="{place}の異変調査",
                   SummaryPat="{place}で起きている異変の原因を調べてほしい。",
                   StatementPat="- 原因不明の現象が続いている。\n- {target}の関与が疑われる。\n- 慎重に調査を進めてほしい。" },
            new(){ Label="浄化", Atmosphere="dangerous", Difficulty=5,
                   TitlePat="{place}の浄化",
                   SummaryPat="{place}に蔓延する瘴気の浄化を依頼する。汚染源を断ってほしい。",
                   StatementPat="- 瘴気により{target}が変質している。\n- 汚染は周辺へ広がりつつある。\n- 汚染源は最奥にあると見られる。" },
        };
        private static readonly (string label, string pat)[] Roles =
        {
            ("冒険者ギルド受付","{name}（冒険者ギルド受付）"), ("村長","{name}（村長）"),
            ("猟師長","{name}（猟師長）"), ("商会主","{name}（商会主）"),
            ("神官","{name}（神官）"), ("自由入力","{name}"),
        };
        private static readonly string[] Atmospheres = { "dangerous", "scary", "eerie" };
        private static readonly Dictionary<string, string> AtmoJp = new()
        { ["dangerous"] = "危険な", ["scary"] = "恐ろしい", ["eerie"] = "不気味な" };

        private readonly JsonObject _root;
        private readonly List<(string id, string name)> _settlements = new();

        private ComboBox _cbSettlement, _cbAtmo, _cbBgm, _cbArchetype, _cbRole;
        private TextBox _tbDungeon, _tbTarget, _tbClient, _tbLocations, _tbTitle;
        private NumericUpDown _numDiff;
        private ResizableTextBox _summary, _statement, _layout, _overview;

        public string CreatedSummary { get; private set; }   // 作成結果の説明（呼び出し側で表示）

        // 入力欄を縦に並べたダイアログを構築する。上段=各種指定、下段=生成テキスト、最下部=操作ボタン。
        public QuestCreator(JsonObject root)
        {
            _root = root;
            Text = "クエスト作成（素案）";
            Width = 720; Height = 760; StartPosition = FormStartPosition.CenterParent;

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;

            // 発注拠点
            foreach (var kv in J.Obj(_root, "areas") ?? new JsonObject())
                if (kv.Value is JsonObject a && J.Str(a, "size") is "town" or "village" or "city")
                    _settlements.Add((kv.Key, J.Str(a, "name", kv.Key)));
            _cbSettlement = Combo(t, ref r, "発注拠点", _settlements.Select(s => $"{s.id}: {s.name}"));

            _tbDungeon = TextRow(t, ref r, "ダンジョン名", "");
            _cbAtmo = Combo(t, ref r, "雰囲気", Atmospheres);
            _cbAtmo.SelectedIndexChanged += (_, _) => RefreshBgm();
            _cbBgm = Combo(t, ref r, "BGM", Enumerable.Empty<string>());
            _cbBgm.DropDownStyle = ComboBoxStyle.DropDown; // 自由入力可
            _cbArchetype = Combo(t, ref r, "クエスト種別", Archetypes.Select(a => a.Label));
            _tbTarget = TextRow(t, ref r, "対象/脅威", "");
            _tbClient = TextRow(t, ref r, "依頼主名", "");
            _cbRole = Combo(t, ref r, "依頼主の役職", Roles.Select(x => x.label));
            _numDiff = Num(t, ref r, "難易度", 1, 10, 4);
            _tbLocations = TextRow(t, ref r, "場所(カンマ区切り)", "");

            // 生成テキスト
            _tbTitle = TextRow(t, ref r, "クエストタイトル", "");
            _summary = Multi(t, ref r, "依頼概要 (request_summary)");
            _statement = Multi(t, ref r, "依頼主の発言 (client_statement)");
            _layout = Multi(t, ref r, "舞台の描写 (area.layout_description)");
            _overview = Multi(t, ref r, "ダンジョン概要 (descriptions.overview)");

            scroll.Controls.Add(t);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var btnCreate = new Button { Text = "作成", Width = 90 };
            var btnCancel = new Button { Text = "キャンセル", Width = 90 };
            var btnGen = new Button { Text = "テンプレ生成", Width = 110 };
            btnCreate.Click += (_, _) => Create();
            btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            btnGen.Click += (_, _) => Generate();
            bar.Controls.AddRange(new Control[] { btnCreate, btnCancel, btnGen });

            Controls.Add(scroll);
            Controls.Add(bar);

            if (_cbSettlement.Items.Count > 0) _cbSettlement.SelectedIndex = 0;
            _cbAtmo.SelectedIndex = 0;
            _cbArchetype.SelectedIndex = 0;
            _cbRole.SelectedIndex = 0;
            RefreshBgm();
        }

        // ---------------- テンプレ素案生成 ----------------
        // 選択中の種別・雰囲気・名称・対象を組み合わせ、各テキスト欄に素案を流し込む（後から手直し可）。
        private void Generate()
        {
            var arc = Archetypes[Math.Max(0, _cbArchetype.SelectedIndex)];
            string place = _tbDungeon.Text.Trim();
            string target = string.IsNullOrWhiteSpace(_tbTarget.Text) ? "魔物" : _tbTarget.Text.Trim();
            string atmo = (string)_cbAtmo.SelectedItem ?? "eerie";
            string atmoJp = AtmoJp.TryGetValue(atmo, out var j) ? j : "";

            if (string.IsNullOrWhiteSpace(place)) { MessageBox.Show("ダンジョン名を入力してください。"); return; }

            _tbTitle.Text = Fill(arc.TitlePat, place, target);
            _summary.Box.Text = Fill(arc.SummaryPat, place, target);
            _statement.Box.Text = Fill(arc.StatementPat, place, target);
            _layout.Box.Text = $"{place}は{atmoJp}空気に包まれた場所。{target}の気配が濃く、油断は許されない。";
            _overview.Box.Text = $"{place}。{atmoJp}雰囲気が漂う、危険なダンジョン。";
            if (_numDiff.Value == 4) _numDiff.Value = Math.Min(_numDiff.Maximum, arc.Difficulty);
        }

        // {place}/{target} をテンプレ文字列に差し込む。
        private static string Fill(string pat, string place, string target)
            => pat.Replace("{place}", place).Replace("{target}", target);

        // ---------------- 実際の挿入 ----------------
        // ダンジョン area と quest を生成して相互に紐付け、発注拠点の quests/connections も更新する。
        private void Create()
        {
            if (_cbSettlement.SelectedIndex < 0) { MessageBox.Show("発注拠点を選んでください。"); return; }
            string place = _tbDungeon.Text.Trim();
            if (string.IsNullOrWhiteSpace(place)) { MessageBox.Show("ダンジョン名を入力してください。"); return; }
            if (string.IsNullOrWhiteSpace(_tbTitle.Text)) { MessageBox.Show("クエストタイトルが空です。テンプレ生成を押すか入力してください。"); return; }

            var areas = J.Obj(_root, "areas"); var quests = J.Obj(_root, "quests");
            if (areas == null || quests == null) { MessageBox.Show("areas / quests が見つかりません。"); return; }

            string sid = _settlements[_cbSettlement.SelectedIndex].id;
            string did = NextKey(areas);
            string qid = NextKey(quests);
            string atmo = (string)_cbAtmo.SelectedItem ?? "eerie";

            // --- ダンジョン area ---
            var facility = new JsonObject
            {
                ["name"] = place + " - 入口",
                ["id"] = "11",
                ["description"] = "",
                ["facility_type"] = "dungeon_location",
                ["tier"] = null,
                ["owner"] = null,
                ["connections"] = new JsonArray(),
                ["config"] = new JsonObject { ["level_of_detail"] = 0 },
            };
            var area = new JsonObject
            {
                ["name"] = place,
                ["id"] = did,
                ["descriptions"] = new JsonObject
                {
                    ["overview"] = _overview.Box.Text,
                    ["facilities"] = "",
                    ["area_description"] = _layout.Box.Text,
                },
                ["size"] = "dungeon",
                ["resident_npcs"] = new JsonArray(),
                ["adventurer_npcs"] = new JsonArray(),
                ["connections"] = new JsonArray { sid },
                ["nodes"] = new JsonObject
                {
                    ["1"] = new JsonObject
                    {
                        ["name"] = "",
                        ["id"] = "1",
                        ["overview"] = "",
                        ["facilities"] = new JsonObject { ["11"] = facility },
                    },
                },
                ["quests"] = new JsonArray(),
                ["labors"] = new JsonObject { ["created_date"] = 0, ["posts"] = new JsonArray() },
                ["entrance_node"] = "11",
                ["config"] = new JsonObject { ["level_of_detail"] = "1" },
                ["bgm"] = _cbBgm.Text,
            };

            // --- quest ---
            var locations = new JsonArray();
            foreach (var loc in _tbLocations.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                locations.Add(loc);

            var clientName = Roles[Math.Max(0, _cbRole.SelectedIndex)].pat.Replace("{name}", _tbClient.Text.Trim());
            var quest = new JsonObject
            {
                ["quest_title"] = _tbTitle.Text,
                ["client_name"] = clientName,
                ["request_summary"] = _summary.Box.Text,
                ["client_statement"] = _statement.Box.Text,
                ["area"] = new JsonObject
                {
                    ["name"] = place,
                    ["layout_description"] = _layout.Box.Text,
                    ["locations"] = locations,
                    ["atomosphere"] = atmo,   // 元データの綴り(atomosphere)に合わせる
                },
                ["events"] = new JsonArray(),
                ["enemies"] = new JsonArray(),
                ["boss"] = new JsonObject(),
                ["difficulty"] = (int)_numDiff.Value,
                ["neighboring_settlement_id"] = sid,
                ["id"] = qid,
                ["quest_type"] = "normal_quest",
                ["config"] = new JsonObject { ["status"] = "incomplete", ["level_of_detail"] = 1 },
                ["quest_area_id"] = did,
            };

            // --- 挿入と連結 ---
            areas[did] = area;
            quests[qid] = quest;
            (areas[sid]?["quests"] as JsonArray)?.Add(qid);     // 拠点のクエスト一覧に登録
            (areas[sid]?["connections"] as JsonArray)?.Add(did); // 拠点⇄ダンジョン接続

            CreatedSummary = $"ダンジョン area[{did}]「{place}」と quest[{qid}]「{_tbTitle.Text}」を作成し、拠点 area[{sid}] に紐付けました。\n"
                           + "events / enemies / boss は空のままです（ゲーム内で LLM に生成させてください）。";
            DialogResult = DialogResult.OK;
            Close();
        }

        // 既存ダンジョンの BGM から、選択中の雰囲気に合うパスを候補として集める（無ければ仮パス）。
        private void RefreshBgm()
        {
            string atmo = (string)_cbAtmo.SelectedItem ?? "";
            var opts = new List<string>();
            foreach (var kv in J.Obj(_root, "areas") ?? new JsonObject())
                if (kv.Value is JsonObject a && J.Str(a, "size") == "dungeon")
                {
                    string b = J.Str(a, "bgm");
                    if (b.Length > 0 && (atmo.Length == 0 || b.Contains("/" + atmo + "/")) && !opts.Contains(b)) opts.Add(b);
                }
            if (opts.Count == 0)   // フォールバック
                opts.Add($"Assets/sounds/musics/dungeons/{(atmo.Length > 0 ? atmo : "eerie")}/.mp3");
            string cur = _cbBgm.Text;
            _cbBgm.Items.Clear();
            foreach (var o in opts) _cbBgm.Items.Add(o);
            _cbBgm.SelectedIndex = 0;
            if (!string.IsNullOrEmpty(cur)) _cbBgm.Text = cur;
        }

        // 辞書内の数値キー最大+1 を新ID文字列として返す。
        private static string NextKey(JsonObject o)
        {
            int max = -1;
            foreach (var kv in o) if (int.TryParse(kv.Key, out int n) && n > max) max = n;
            return (max + 1).ToString();
        }

        // ---------------- UI ヘルパ（ラベル＋入力の1行を表に追加して、その入力部品を返す） ----------------
        private ComboBox Combo(TableLayoutPanel t, ref int r, string label, IEnumerable<string> items)
        {
            t.Controls.Add(L(label), 0, r);
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            foreach (var it in items) cb.Items.Add(it);
            t.Controls.Add(cb, 1, r); r++;
            return cb;
        }
        private TextBox TextRow(TableLayoutPanel t, ref int r, string label, string val)
        {
            t.Controls.Add(L(label), 0, r);
            var tb = new TextBox { Dock = DockStyle.Fill, Text = val };
            t.Controls.Add(tb, 1, r); r++;
            return tb;
        }
        private NumericUpDown Num(TableLayoutPanel t, ref int r, string label, int min, int max, int val)
        {
            t.Controls.Add(L(label), 0, r);
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = val, Width = 80 };
            t.Controls.Add(n, 1, r); r++;
            return n;
        }
        private ResizableTextBox Multi(TableLayoutPanel t, ref int r, string label)
        {
            t.Controls.Add(L(label), 0, r);
            var rtb = new ResizableTextBox(460, 80) { Dock = DockStyle.Top, Margin = new Padding(3, 3, 3, 8) };
            t.Controls.Add(rtb, 1, r); r++;
            return rtb;
        }
        private static Label L(string s) => new() { Text = s, AutoSize = true, Padding = new Padding(2, 6, 4, 0) };
    }
}
