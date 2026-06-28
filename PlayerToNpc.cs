// プレイヤーを NPC として出力する。
// player_data を npcs[id] と同形の NPC オブジェクトへ変換し、(1) npcs へ追加 /
// (2) NPC パッケージ(zip: npc.json + characters/{名前}/ の画像)としてエクスポートする
//     （ワールドタブの NPC エクスポートと同等。NpcPortability.Export を共用）。
//
// 変換方針（実データのスキーマに合わせる。リバースエンジニアリングのため完全性は保証しない）:
//   ・流用（同名・互換）: name / category / age / profile / personality / look_description /
//                         skills / inventory / image_src / memory / current_log /
//                         experience_level / experience_point（後者2つも NPC・player 双方にあり互換のため流用）
//   ・初期化（他NPCに合わせる）: equipments={} / life_log=[]
//   ・能力値: original_ability_scores(小数6項目)を四捨五入で整数化。全項目が正の整数なら
//             ability_scores(object)、0/空/不正が一つでもあれば null（ObjectForm の正規化規則に合わせる）。
//   ・HP: current_hp=player.current_hp。max_hp/original_max_hp は既定 current_hp（編集可）。
//   ・配置: 実 NPC は location(object {area,node,facility}) と current_area / current_location(施設ID 文字列)
//           initial_location(object) を併用する。player.location は施設ID 文字列のため、これを
//           current_location の既定に、player.current_area を current_area の既定に用いる。
//           location は稼働中 NPC と同じ {area:null,node:null,facility:null}、
//           initial_location は {area:current_area, node:null, facility:current_location} を組み立てる。
//           （task の「location=文字列をそのまま」案は実データでは無効になるため上記に倒した。）
//   ・NPC 固有: id(npcs の数値キー最大+1 を自動採番)/ job / weakness / speech_style / state / look /
//               relationship / config。relationship・config は既定を用意し JSON 編集で調整可。
//   ・除外（NPC に無い）: traits / body_parts / physical_integrity 系 / original_ability_scores /
//               gold / area_history / story_achievements / current_node。
//   ・NPC 固有で新設: knowledge=[] / display_position_in_battle=null。
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class PlayerToNpcDialog : Form
    {
        // 能力値6項目（player は original_ability_scores・小数で保持）。表示名は ability.<key>。
        private static readonly string[] AbilityKeys =
        { "strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma" };

        private readonly JsonObject _root;
        private readonly JsonObject _pd;     // player_data
        private readonly JsonObject _npcs;   // npcs（採番・追加先）
        private readonly JsonObject _areas;  // areas（エリア/施設プルダウン生成用）
        private readonly string _worldDir;   // worlds/{スロット}/ のパス。画像同梱に使う

        // 入力ウィジェット
        private TextBox _tbId, _tbState, _tbSpeech, _tbWeakness;
        private ComboBox _cbJob;             // 職業（候補は埋め込み、自由入力も可）
        private ComboBox _cbCurArea, _cbCurLoc;  // current_area / current_location（データ参照プルダウン）
        private TextBox _tbCurHp, _tbMaxHp, _tbOrigMaxHp;
        private TextBox _tbLook;
        private readonly Dictionary<string, TextBox> _abil = new();   // 換算後の能力値（読み取り専用表示）

        // JSON 編集で調整する複雑フィールド（既定値を保持）
        private JsonObject _relationship;
        private JsonObject _config;

        public bool Added { get; private set; }
        public string ResultSummary { get; private set; }

        public PlayerToNpcDialog(JsonObject root, string worldDir)
        {
            _root = root;
            _pd = J.Obj(root, "player_data");
            _npcs = J.Obj(root, "npcs") ?? new JsonObject();
            _areas = J.Obj(root, "areas");
            _worldDir = worldDir;

            Text = I18n.T("p2n.title");
            Width = 720; Height = 860; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            // 既定値を先に用意してから UI を組む。
            _relationship = NpcPortability.DefaultRelationship();   // 既存実装の妥当な既定（player・好感度0）
            _config = DefaultConfig();                              // 既存 NPC の config を参照した既定

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;

            // ── 流用項目（確認用・読み取り専用） ──
            Header(t, ref r, I18n.T("p2n.header.reuse"));
            RoText(t, ref r, I18n.T("p2n.name"), J.Str(_pd, "name"));
            RoText(t, ref r, I18n.T("p2n.category"), J.Str(_pd, "category"));
            RoText(t, ref r, I18n.T("p2n.age"), J.Int(_pd, "age").ToString());
            Note(t, ref r, I18n.T("p2n.note.reuse"));

            // ── 能力値（自動換算: original_ability_scores を四捨五入） ──
            Header(t, ref r, I18n.T("p2n.header.abilities"));
            var oas = J.Obj(_pd, "original_ability_scores");
            foreach (var key in AbilityKeys)
            {
                long v = (long)Math.Round(J.Dbl(oas, key), MidpointRounding.AwayFromZero);
                var tb = RoText(t, ref r, $"{I18n.T("ability." + key)} ({key})", v.ToString());
                _abil[key] = tb;
            }
            Note(t, ref r, I18n.T("p2n.note.abilities"));

            // ── HP ──
            Header(t, ref r, I18n.T("p2n.header.hp"));
            long curHp = J.Int(_pd, "current_hp");
            _tbCurHp = Edit(t, ref r, I18n.T("p2n.curHp"), curHp.ToString());
            _tbMaxHp = Edit(t, ref r, I18n.T("p2n.maxHp"), curHp.ToString());
            _tbOrigMaxHp = Edit(t, ref r, I18n.T("p2n.origMaxHp"), curHp.ToString());

            // ── NPC 固有 ──
            Header(t, ref r, I18n.T("p2n.header.npc"));
            _tbId = Edit(t, ref r, I18n.T("p2n.id"), NextNpcKey());
            _cbJob = JobCombo(t, ref r, I18n.T("p2n.job"), "adventure");
            _tbState = Edit(t, ref r, I18n.T("p2n.state"), "");
            _tbSpeech = Edit(t, ref r, I18n.T("p2n.speech"), "");
            _tbWeakness = Edit(t, ref r, I18n.T("p2n.weakness"), "");
            // current_area / current_location は areas を参照したプルダウン（エリア変更で施設候補を連動）。
            t.Controls.Add(Lbl(I18n.T("p2n.curArea")), 0, r);
            _cbCurArea = AreaComboHelper.MakeAreaCombo(_areas, J.Str(_pd, "current_area"));
            t.Controls.Add(_cbCurArea, 1, r); r++;
            t.Controls.Add(Lbl(I18n.T("p2n.curLoc")), 0, r);
            _cbCurLoc = AreaComboHelper.MakeFacilityCombo(_areas, J.Str(_pd, "current_area"), LocationFacility());
            t.Controls.Add(_cbCurLoc, 1, r); r++;
            LinkAreaLocation();
            // look（外見タグ・1行1タグ）。player には無いため既定は空。
            t.Controls.Add(Lbl(I18n.T("p2n.look")), 0, r);
            _tbLook = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 70, Dock = DockStyle.Fill };
            t.Controls.Add(_tbLook, 1, r); r++;
            Note(t, ref r, I18n.T("p2n.note.look"));

            // relationship / config は JSON 編集ボタンで調整（既定値あり）。
            JsonRow(t, ref r, I18n.T("p2n.relationship"), () => _relationship, o => _relationship = o);
            JsonRow(t, ref r, I18n.T("p2n.config"), () => _config, o => _config = o);

            scroll.Controls.Add(t);

            // ボタン: npcs に追加 / JSONエクスポート / キャンセル
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var btnAdd = new Button { Text = I18n.T("p2n.btnAdd"), Width = 130 };
            var btnExport = new Button { Text = I18n.T("p2n.btnExport"), Width = 180 };
            var btnCancel = new Button { Text = I18n.T("btn.cancel"), Width = 100, DialogResult = DialogResult.Cancel };
            btnAdd.Click += (_, _) => AddToNpcs();
            btnExport.Click += (_, _) => ExportPackage();
            bar.Controls.AddRange(new Control[] { btnAdd, btnExport, btnCancel });
            CancelButton = btnCancel;

            Controls.Add(scroll);
            Controls.Add(bar);
        }

        // ---------------- 生成 ----------------
        // 入力内容から NPC オブジェクト（npcs[id] と同形）を組み立てる。検証失敗時は null。
        private JsonObject BuildNpc()
        {
            string id = _tbId.Text.Trim();
            if (string.IsNullOrEmpty(id)) { Warn(I18n.T("p2n.warn.id")); return null; }
            if (!long.TryParse(_tbCurHp.Text, out long curHp)) { Warn(I18n.T("p2n.warn.curHp")); return null; }
            if (!long.TryParse(_tbMaxHp.Text, out long maxHp)) { Warn(I18n.T("p2n.warn.maxHp")); return null; }
            if (!long.TryParse(_tbOrigMaxHp.Text, out long origMaxHp)) { Warn(I18n.T("p2n.warn.origMaxHp")); return null; }

            // プルダウンは "ID: 名前" 形式のため ID 部分のみ取り出す。
            string curArea = AreaComboHelper.ExtractId(_cbCurArea.Text);
            string curLoc = AreaComboHelper.ExtractId(_cbCurLoc.Text);

            // 実 NPC のキー順に合わせて組み立てる。
            var npc = new JsonObject
            {
                ["name"] = Clone(_pd["name"]) ?? "",
                ["id"] = id,
                ["category"] = Clone(_pd["category"]) ?? "",
                ["profile"] = Clone(_pd["profile"]) ?? "",
                ["personality"] = Clone(_pd["personality"]) ?? "",
                ["look_description"] = Clone(_pd["look_description"]) ?? "",
                ["speech_style"] = EmptyToNull(_tbSpeech.Text),
                ["job"] = _cbJob.Text.Trim(),
                ["state"] = _tbState.Text,
                ["ability_scores"] = BuildAbilityScores(),
                ["experience_level"] = J.Int(_pd, "experience_level"),
                ["experience_point"] = J.Int(_pd, "experience_point"),
                ["original_max_hp"] = origMaxHp,
                ["max_hp"] = maxHp,
                ["current_hp"] = curHp,
                ["age"] = Clone(_pd["age"]) ?? (JsonNode)0,
                ["skills"] = Clone(_pd["skills"]) ?? new JsonObject(),
                ["equipments"] = new JsonObject(),   // 他NPCに合わせて空にする

                ["weakness"] = EmptyToNull(_tbWeakness.Text),
                ["location"] = new JsonObject { ["area"] = null, ["node"] = null, ["facility"] = null },
                ["inventory"] = Clone(_pd["inventory"]) ?? new JsonObject(),
                ["image_src"] = Clone(_pd["image_src"]) ?? new JsonObject(),
                ["look"] = LinesToArray(_tbLook.Text),
                ["memory"] = Clone(_pd["memory"]) ?? new JsonObject(),
                ["life_log"] = new JsonArray(),   // 他NPCに合わせて消去する

                ["current_log"] = Clone(_pd["current_log"]) ?? new JsonArray(),
                ["relationship"] = (JsonObject)_relationship.DeepClone(),
                ["initial_location"] = new JsonObject
                {
                    ["area"] = EmptyToNull(curArea),
                    ["node"] = null,
                    ["facility"] = EmptyToNull(curLoc),
                },
                ["config"] = (JsonObject)_config.DeepClone(),
                ["current_area"] = EmptyToNull(curArea),
                ["current_location"] = EmptyToNull(curLoc),
                ["knowledge"] = new JsonArray(),
                ["display_position_in_battle"] = null,
            };
            return npc;
        }

        // 換算済み能力値6欄から ability_scores を作る。全項目が正の整数なら object、1つでも不正なら null。
        private JsonNode BuildAbilityScores()
        {
            var o = new JsonObject();
            bool allOk = true;
            foreach (var key in AbilityKeys)
            {
                if (long.TryParse(_abil[key].Text, out long v) && v > 0) o[key] = v;
                else allOk = false;
            }
            return allOk ? (JsonNode)o : null;
        }

        // ---------------- ボタン処理 ----------------
        // npcs[新ID] = 生成NPC とし、DialogResult.OK で閉じる（呼び出し側がワールドタブを再構築）。
        private void AddToNpcs()
        {
            var npc = BuildNpc();
            if (npc == null) return;
            string id = npc["id"].ToString();
            if (_npcs.ContainsKey(id) &&
                MessageBox.Show(this, I18n.T("p2n.overwriteConfirm", id), I18n.T("title.confirm"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            // _root["npcs"] が無い（player_data のみ等）ファイルにも対応する。
            if (J.Obj(_root, "npcs") is not JsonObject npcsInRoot)
            { npcsInRoot = _npcs; _root["npcs"] = npcsInRoot; }
            npcsInRoot[id] = npc;

            Added = true;
            ResultSummary = I18n.T("p2n.summary", J.Str(npc, "name"), id, J.Str(npc, "current_area"), J.Str(npc, "current_location"));
            DialogResult = DialogResult.OK;
            Close();
        }

        // NPC パッケージ(zip: npc.json + characters/{名前}/ の画像)として書き出す。
        // ワールドタブの NPC エクスポートと同等（NpcPortability.Export を共用）。
        // 画像はプレイヤーのキャラフォルダ worlds/{スロット}/characters/{プレイヤー名}/ から取り込む。
        private void ExportPackage()
        {
            var npc = BuildNpc();
            if (npc == null) return;
            string name = J.Str(npc, "name", "npc");
            string source = J.Str(J.Obj(_root, "world_data"), "name");
            if (string.IsNullOrEmpty(source) && _worldDir != null) source = Path.GetFileName(_worldDir);

            string dest;
            try
            {
                dest = NpcPortability.FreeExportPath(name);
                NpcPortability.Export(npc, _worldDir, source, npc["id"].ToString(), dest);
            }
            catch (Exception ex)
            { MessageBox.Show(this, I18n.T("msg.exportFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            MessageBox.Show(this, I18n.T("p2n.exported", name, dest), I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- 既定値・ヘルパ ----------------
        // npcs の数値キー最大+1 を新 ID として返す（数値以外のキーは無視）。
        private string NextNpcKey()
        {
            int max = -1;
            foreach (var kv in _npcs)
                if (int.TryParse(kv.Key, out int n) && n > max) max = n;
            return (max + 1).ToString();
        }

        // player.location（施設ID 文字列）を current_location の既定として返す。
        private string LocationFacility() => J.Str(_pd, "location");

        // current_area の選択変更に追従して current_location（施設）の候補を作り直す。
        // 変更後のエリアに無い施設IDを指していた場合は選択をクリアする。
        private void LinkAreaLocation()
        {
            if (_cbCurArea == null || _cbCurLoc == null) return;
            _cbCurArea.SelectedIndexChanged += (_, _) =>
            {
                string area = AreaComboHelper.ExtractId(_cbCurArea.Text);
                string id = AreaComboHelper.ExtractId(_cbCurLoc.Text);
                AreaComboHelper.FillFacilityItems(_cbCurLoc, _areas, area, id);
                string match = _cbCurLoc.Items.Cast<string>()
                    .FirstOrDefault(it => AreaComboHelper.ExtractId(it) == id);
                _cbCurLoc.Text = match ?? "";
            };
        }

        // 既存 NPC の config を参照して妥当な既定を作る。無ければ最小構成。
        private JsonObject DefaultConfig()
        {
            foreach (var kv in _npcs)
                if (kv.Value is JsonObject o && J.Obj(o, "config") is JsonObject c)
                {
                    var clone = (JsonObject)c.DeepClone();
                    clone["is_player"] = false;   // プレイヤー由来でも NPC として扱う
                    clone["is_dead"] = false;
                    return clone;
                }
            return new JsonObject
            {
                ["level_of_detail"] = 2,
                ["is_player"] = false,
                ["is_dead"] = false,
                ["difficulty_level"] = 3,
            };
        }

        private static JsonNode Clone(JsonNode n) => n?.DeepClone();
        // 空文字は JSON null（実 NPC は未設定項目を null で持つ）。
        private static JsonNode EmptyToNull(string s) => string.IsNullOrEmpty(s) ? null : JsonValue.Create(s);

        // 複数行テキストを「1行1要素・前後空白除去・空行除外」で文字列配列へ変換する。
        private static JsonArray LinesToArray(string text)
        {
            var a = new JsonArray();
            foreach (var line in (text ?? "").Split('\n'))
            {
                string s = line.Trim();
                if (s.Length > 0) a.Add(s);
            }
            return a;
        }

        private void Warn(string msg) => MessageBox.Show(this, msg, I18n.T("title.inputError"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

        // --- UI 部品 ---
        private static void Header(TableLayoutPanel t, ref int r, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Padding = new Padding(0, 10, 0, 2) };
            t.Controls.Add(l, 0, r); t.SetColumnSpan(l, 2); r++;
        }
        private static void Note(TableLayoutPanel t, ref int r, string text)
        {
            var l = new Label { Text = "※ " + text, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(2, 2, 0, 6) };
            t.Controls.Add(l, 0, r); t.SetColumnSpan(l, 2); r++;
        }
        // 編集可能なテキスト行。
        private static TextBox Edit(TableLayoutPanel t, ref int r, string label, string val)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var tb = new TextBox { Dock = DockStyle.Fill, Text = val };
            t.Controls.Add(tb, 1, r); r++;
            return tb;
        }
        // 職業コンボ（候補は埋め込み、自由入力も可）。既定値を選択状態にする。
        private static ComboBox JobCombo(TableLayoutPanel t, ref int r, string label, string def)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var cb = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var j in FieldOptions.Get("job")) cb.Items.Add(j);
            cb.Text = def;
            t.Controls.Add(cb, 1, r); r++;
            return cb;
        }
        // 読み取り専用のテキスト行（確認用）。
        private static TextBox RoText(TableLayoutPanel t, ref int r, string label, string val)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var tb = new TextBox { Dock = DockStyle.Fill, Text = val, ReadOnly = true, BackColor = SystemColors.Control };
            t.Controls.Add(tb, 1, r); r++;
            return tb;
        }
        // JSON 編集ボタン行（getter で現在値を渡し、OK 時に setter へ反映）。
        private void JsonRow(TableLayoutPanel t, ref int r, string label, Func<JsonObject> getter, Action<JsonObject> setter)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var btn = new Button { Text = I18n.T("btn.jsonEdit"), Width = 120 };
            btn.Click += (_, _) =>
            {
                using var d = new JsonEditDialog(I18n.T("title.editField", label), getter());
                if (d.ShowDialog(this) == DialogResult.OK && d.ResultNode is JsonObject o) setter(o);
            };
            t.Controls.Add(btn, 1, r); r++;
        }
        private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Padding = new Padding(2, 6, 4, 0) };
    }
}
