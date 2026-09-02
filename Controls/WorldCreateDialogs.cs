// ワールドタブのツリー右クリック「新規作成」で使う入力ダイアログと、レコード骨格の生成。
// - AreaCreateDialog: 名前＋規模（village/town/city/dungeon）＋接続先エリアで新エリアを作る（拠点は未生成の形。ゲームが到着時に中身を作る）
// - NpcCreateDialog: 名前・カテゴリ・職業・配置（エリア/施設）・冒険者登録で新NPCを作る
// - FacilityCreateDialog: 名前＋施設種別で新施設を作る
// 骨格は WorldGenerator / QuestCreator が生成する実データと同じフィールド構成に揃える。
// ID 採番はワールドの index カウンタ（area/node/facility）を使い、既存 ID との衝突を避ける。
using System.Linq;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // ---------------- レコード骨格の生成 ----------------
    internal static class WorldRecordFactory
    {
        // 既存の全 area を走査して node / facility の使用中 ID 集合を作る（新 ID の衝突回避用）。
        internal static (HashSet<string> nodeIds, HashSet<string> facIds) CollectNodeFacilityIds(JsonObject areas)
        {
            var nodeIds = new HashSet<string>();
            var facIds = new HashSet<string>();
            foreach (var aKv in areas ?? new JsonObject())
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
            return (nodeIds, facIds);
        }

        // 新しい area（拠点 or ダンジョン）を index で採番して組み立てる。挿入は呼び出し側で行う。
        // 拠点は入口＋出口の2施設、ダンジョンは dungeon_location 1施設の最小構成
        // （施設は右クリックの「施設を新規作成」で足せる）。
        // 拠点（village/town/city）はノード・施設を持たない「未生成」の形で作る。実データでゲームがまだ生成していない
        // エリアと同形で、ゲームが到着時に中身（入口・出口・各施設）を生成して savedata / world_data の両方へ書く。
        // ツール側で入口・出口を付けると、ゲーム生成の施設と二重になる。index の node / facility は進めない。
        // ダンジョンは実データに未生成の形が無い（常に dungeon_location 施設 1 つのノードを持つ）ため従来どおり作る。
        internal static (string id, JsonObject area) BuildArea(JsonObject areas, JsonObject index, string name, string size)
        {
            string areaId = QuestCreator.NextId(index, "area", areas.ContainsKey);
            if (size != "dungeon") return (areaId, BuildUngeneratedArea(areaId, name, size));
            var (nodeIds, facIds) = CollectNodeFacilityIds(areas);
            string nodeId = QuestCreator.NextId(index, "node", nodeIds.Contains);
            string fid = QuestCreator.NextId(index, "facility", facIds.Contains);

            var node = new JsonObject
            {
                ["name"] = "",
                ["id"] = nodeId,
                ["overview"] = "",
                ["facilities"] = new JsonObject { [fid] = BuildFacility(fid, name, "dungeon_location") },
                ["connections"] = new JsonArray(),
                ["entrance_facility"] = fid,
                ["config"] = new JsonObject { ["level_of_detail"] = 0 },
            };
            var area = new JsonObject
            {
                ["name"] = name,
                ["id"] = areaId,
                // 実データ準拠: ダンジョンは overview / facilities が null。
                ["descriptions"] = new JsonObject { ["overview"] = null, ["facilities"] = null, ["area_description"] = "" },
                ["size"] = size,
                ["resident_npcs"] = new JsonArray(),
                ["adventurer_npcs"] = new JsonArray(),
                ["connections"] = new JsonArray(),
                ["nodes"] = new JsonObject { [nodeId] = node },
                ["quests"] = new JsonArray(),
                ["labors"] = new JsonObject { ["created_date"] = 0, ["posts"] = new JsonArray() },
                ["entrance_node"] = nodeId,
                ["bgm"] = "",
                // 実データ準拠: ダンジョンの level_of_detail は文字列 "1"。
                ["config"] = new JsonObject { ["level_of_detail"] = "1" },
            };
            return (areaId, area);
        }

        // 未生成エリアの骨格。実データでゲームがまだ生成していないエリアと同形（項目の並びも同じ）:
        // nodes は空、entrance_node は null、config.level_of_detail は 0。
        private static JsonObject BuildUngeneratedArea(string areaId, string name, string size) => new()
        {
            ["name"] = name,
            ["id"] = areaId,
            ["descriptions"] = new JsonObject { ["overview"] = "", ["facilities"] = "", ["area_description"] = "" },
            ["size"] = size,
            ["resident_npcs"] = new JsonArray(),
            ["adventurer_npcs"] = new JsonArray(),
            ["connections"] = new JsonArray(),
            ["nodes"] = new JsonObject(),
            ["quests"] = new JsonArray(),
            ["labors"] = new JsonObject { ["created_date"] = 0, ["posts"] = new JsonArray() },
            ["entrance_node"] = null,
            ["bgm"] = "",
            ["config"] = new JsonObject { ["level_of_detail"] = 0 },
        };

        // 施設の骨格（WorldGenerator.Facility と同じフィールド構成。tier / owner は未設定）。
        internal static JsonObject BuildFacility(string id, string name, string type) => new()
        {
            ["name"] = name,
            ["id"] = id,
            ["description"] = "",
            ["facility_type"] = string.IsNullOrEmpty(type) ? null : type,
            ["tier"] = null,
            ["owner"] = null,
            ["connections"] = new JsonArray(),
            ["config"] = new JsonObject { ["level_of_detail"] = 0 },
        };

        // NPC の骨格（WorldGenerator.BuildNpc と同じフィールド構成。文章類は空でフォームから編集する）。
        internal static JsonObject BuildNpc(string id, string name, string category, string job, string areaId, string facilityId)
        {
            string area = string.IsNullOrEmpty(areaId) ? null : areaId;
            string fac = string.IsNullOrEmpty(facilityId) ? null : facilityId;
            var look = new JsonArray();
            if (!string.IsNullOrEmpty(category)) look.Add(category);
            return new JsonObject
            {
                ["name"] = name,
                ["id"] = id,
                ["category"] = category,
                // profile / personality / look_description が空だと、ゲームが立ち絵生成などを
                // 完了できず読込が無限ロードになる（実データは全NPCがこの3項目を非空で持つ）。
                // そのため作成時に必ず非空の既定文で埋める。作成後にフォームで書き換え可能。
                ["profile"] = DefaultProfile(),
                ["personality"] = DefaultPersonality(),
                ["look_description"] = DefaultLookDescription(category, look),
                ["speech_style"] = null,
                ["job"] = job,
                ["state"] = "",
                ["ability_scores"] = new JsonObject
                {
                    ["strength"] = null,
                    ["dexterity"] = null,
                    ["constitution"] = null,
                    ["intelligence"] = null,
                    ["wisdom"] = null,
                    ["charisma"] = null,
                },
                ["experience_level"] = 1,
                ["experience_point"] = 0,
                ["original_max_hp"] = null,
                ["max_hp"] = null,
                ["current_hp"] = null,
                ["age"] = AgeFor(category),
                ["skills"] = new JsonObject(),
                ["equipments"] = new JsonObject(),
                ["weakness"] = null,
                ["location"] = new JsonObject { ["area"] = null, ["node"] = null, ["facility"] = null },
                ["inventory"] = new JsonObject(),
                ["image_src"] = new JsonObject
                {
                    ["base_normal"] = null,
                    ["base_upscaled"] = null,
                    ["fullbody"] = null,
                    ["opponent"] = null,
                    ["face"] = null,
                },
                ["look"] = look,
                ["memory"] = new JsonObject
                {
                    ["life_log"] = "",
                    ["memory_archive"] = new JsonArray(),
                    ["session_log"] = new JsonArray(),
                    ["prior_area_summary"] = "",
                    ["brief_summary"] = "ゲーム開始",
                },
                ["life_log"] = new JsonArray(),
                ["current_log"] = new JsonArray(),
                // 実データのプレイ済みNPCと同形。null や欠落だとゲーム側の読込で弾かれるため
                // player 向けの初期 relationship を明示的に持たせる。
                ["relationship"] = new JsonObject
                {
                    ["player"] = new JsonObject
                    {
                        ["affinity"] = 0,
                        ["affinity_text"] = "警戒心がある",
                        ["relationship"] = new JsonArray { "初対面" },
                        ["conversation_count"] = 0,
                    },
                },
                ["current_area"] = area,
                ["current_location"] = fac,
                ["initial_location"] = new JsonObject { ["area"] = area, ["node"] = null, ["facility"] = fac },
                ["config"] = new JsonObject
                {
                    ["level_of_detail"] = 1,
                    ["is_player"] = false,
                    ["is_dead"] = false,
                    ["difficulty_level"] = 1,
                },
                ["knowledge"] = new JsonArray(),
                ["display_position_in_battle"] = null,
            };
        }

        // 記述フィールドの既定文（空だとゲームの読込が無限ロードになるため必ず非空にする）。
        // profile / personality はゲーム内容用の汎用文（作成後に上書き前提）。
        // プレイヤーの NPC 化（PlayerToNpc）でも同じ既定文を使うため internal。
        internal static string DefaultProfile() => "この地で暮らす人物。詳しい経歴はまだ語られていない。";
        internal static string DefaultPersonality() => "落ち着いた物腰で、接する相手に応じて態度を変える。";

        // look_description は立ち絵生成のプロンプトに使われるため、外見タグ（look）があればそれを
        // 列挙して充てる。タグが無ければ category を使い、それも無ければ最低限の非空文字にする。
        internal static string DefaultLookDescription(string category, JsonArray look)
        {
            var tags = look.Select(t => t?.ToString())
                           .Where(s => !string.IsNullOrWhiteSpace(s))
                           .ToList();
            if (tags.Count > 0) return string.Join(", ", tags);
            return string.IsNullOrWhiteSpace(category) ? "a person of this world" : category;
        }

        // カテゴリから年齢の目安を決める（WorldGenerator.PickAgeForCategory の中央値相当）。
        private static int AgeFor(string category) => category switch
        {
            "young_boy" or "young_girl" => 10,
            "teenage boy" or "teenage girl" => 15,
            "young man" or "young woman" => 22,
            "middle-aged man" or "middle-aged woman" => 40,
            "old man" or "old woman" => 60,
            _ => 25,
        };
    }

    // ---------------- 入力ダイアログの共通骨格 ----------------
    // ラベル＋入力の行を積み、OK/キャンセルで閉じる小さなダイアログ。
    internal abstract class CreateDialogBase : Form
    {
        private readonly TableLayoutPanel _t;
        private int _row;

        protected CreateDialogBase(string title, int height)
        {
            Text = title;
            Width = 460; Height = height;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            _t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
            _t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            _t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var ok = new Button { Text = I18n.T("btn.ok"), Width = 90 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Width = 90, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => { if (Confirm()) { DialogResult = DialogResult.OK; Close(); } };
            bar.Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok; CancelButton = cancel;

            Controls.Add(_t);
            Controls.Add(bar);
        }

        // OK 押下時の検証。false ならダイアログを閉じない。
        protected abstract bool Confirm();

        // ラベル＋入力ウィジェットの行を1つ追加する。
        protected T AddRow<T>(string label, T c) where T : Control
        {
            _t.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(2, 6, 4, 0) }, 0, _row);
            c.Dock = DockStyle.Fill;
            _t.Controls.Add(c, 1, _row); _row++;
            return c;
        }

        // 候補付きの自由入力プルダウン（候補に無い値も入力できる）。
        protected static ComboBox OptionsCombo(IEnumerable<string> options, string current = "")
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems,
            };
            foreach (var o in options) cb.Items.Add(o);
            cb.Text = current ?? "";
            return cb;
        }

        // 名前欄の必須チェック（空ならメッセージを出して false）。
        protected bool RequireName(TextBox tb)
        {
            if (!string.IsNullOrWhiteSpace(tb.Text)) return true;
            MessageBox.Show(this, I18n.T("msg.nameRequired"));
            return false;
        }
    }

    // ---------------- エリアの新規作成 ----------------
    internal sealed class AreaCreateDialog : CreateDialogBase
    {
        private readonly TextBox _tbName = new();
        private readonly ComboBox _cbSize;
        private readonly CheckedListBox _lbConn;
        private readonly List<string> _connIds = new();

        public string AreaName => _tbName.Text.Trim();
        public string AreaSize => _cbSize.Text.Trim();
        // 接続先に選んだ既存エリアの id（ダンジョンは実データで connections が常に空のため、選択不可＝空）。
        public IReadOnlyList<string> ConnectAreaIds =>
            !_lbConn.Enabled ? Array.Empty<string>()
            : _lbConn.CheckedIndices.Cast<int>().Select(i => _connIds[i]).ToList();

        // areas: 接続先候補（拠点のみ列挙）。presetAreaId: 右クリック位置のエリアを接続先の初期値にする。
        public AreaCreateDialog(JsonObject areas, string presetAreaId) : base(I18n.T("createArea.title"), 330)
        {
            AddRow(I18n.T("createArea.name"), _tbName);
            _cbSize = AddRow(I18n.T("createArea.size"), OptionsCombo(new[] { "village", "town", "city", "dungeon" }, "town"));
            _lbConn = AddRow(I18n.T("createArea.connections"), new CheckedListBox { CheckOnClick = true, Height = 140, IntegralHeight = false });
            if (areas != null)
                foreach (var kv in areas.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                {
                    if (kv.Value is not JsonObject o || J.Str(o, "size", "") == "dungeon") continue;
                    _connIds.Add(kv.Key);
                    _lbConn.Items.Add($"{kv.Key}: {J.Str(o, "name", kv.Key)}", kv.Key == presetAreaId);
                }
            void Sync() => _lbConn.Enabled = AreaSize != "dungeon";
            _cbSize.SelectedIndexChanged += (_, _) => Sync();
            _cbSize.TextChanged += (_, _) => Sync();
        }

        protected override bool Confirm() => RequireName(_tbName);
    }

    // ---------------- NPC の新規作成 ----------------
    internal sealed class NpcCreateDialog : CreateDialogBase
    {
        private readonly TextBox _tbName = new();
        private readonly ComboBox _cbCategory, _cbJob, _cbArea, _cbFac;
        private readonly CheckBox _chkAdventurer;
        private readonly JsonObject _areas;

        public string NpcName => _tbName.Text.Trim();
        public string Category => _cbCategory.Text.Trim();
        public string Job => _cbJob.Text.Trim();
        public string AreaId => AreaComboHelper.ExtractId(_cbArea.Text);
        public string FacilityId => AreaComboHelper.ExtractId(_cbFac.Text);
        public bool AsAdventurer => _chkAdventurer.Checked;

        public NpcCreateDialog(JsonObject areas, string presetAreaId) : base(I18n.T("createNpc.title"), 320)
        {
            _areas = areas;
            AddRow(I18n.T("createNpc.name"), _tbName);
            _cbCategory = AddRow(I18n.T("createNpc.category"), OptionsCombo(FieldOptions.Get("category")));
            _cbJob = AddRow(I18n.T("createNpc.job"), OptionsCombo(FieldOptions.Get("job")));
            _cbArea = AddRow(I18n.T("createNpc.area"), AreaComboHelper.MakeAreaCombo(areas, presetAreaId));
            _cbFac = AddRow(I18n.T("createNpc.facility"), AreaComboHelper.MakeFacilityCombo(areas, presetAreaId, null));
            _chkAdventurer = AddRow("", new CheckBox { Text = I18n.T("createNpc.adventurer"), AutoSize = true });
            // エリア変更に追従して施設候補を作り直す（変更後のエリアに無い施設はクリア）。
            _cbArea.SelectedIndexChanged += (_, _) =>
            {
                string id = AreaComboHelper.ExtractId(_cbFac.Text);
                AreaComboHelper.FillFacilityItems(_cbFac, _areas, AreaId, id);
                string match = _cbFac.Items.Cast<string>().FirstOrDefault(it => AreaComboHelper.ExtractId(it) == id);
                _cbFac.Text = match ?? "";
            };
        }

        protected override bool Confirm() => RequireName(_tbName);
    }

    // ---------------- 施設の新規作成 ----------------
    internal sealed class FacilityCreateDialog : CreateDialogBase
    {
        private readonly TextBox _tbName = new();
        private readonly ComboBox _cbType;

        public string FacilityName => _tbName.Text.Trim();
        public string FacilityType => _cbType.Text.Trim();

        public FacilityCreateDialog() : base(I18n.T("createFac.title"), 180)
        {
            AddRow(I18n.T("createFac.name"), _tbName);
            _cbType = AddRow(I18n.T("createFac.type"), OptionsCombo(FieldOptions.Get("facility_type")));
        }

        protected override bool Confirm() => RequireName(_tbName);
    }
}
