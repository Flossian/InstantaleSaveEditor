// プレイヤーをキャラクタープリセット（ゲームのキャラ作成画面で選べるテンプレート）として出力する。
// ゲームが生成するプリセットの構成: {Instantale データ基底}\characters\{名前}\
//   ・character_sheet.json …… XOR 難読化 JSON（schema_version / language / character_vm）
//   ・face_image.png / generated_image.png / no_bg_image.png /
//     pixelated_image_original.png / reduced_color_image.png / prompts.json
//     …… ワールド側 worlds\{スロット}\characters\{キャラ名}\ の同名ファイルの複製
//
// character_vm への変換方針（実プリセット2件からの逆算。完全性は保証しない）:
//   ・流用: name / profile / category / age / look_description / gold
//   ・能力値: original_ability_scores（小数）を四捨五入し attribute_<key> の6項目へ展開
//   ・skills: 辞書 → 配列。新規作成用テンプレートのため current_uses は max_uses に戻す。
//             キーはゲームの SkillVM 準拠
//             （name/description/max_uses/current_uses/element/skill_type/effects/usefulness）に限定
//   ・traits: TraitVM 準拠（name/description/severity）に限定
//   ・gender: player_data に存在しないため category の語（woman/girl 等）から ♂/♀ を自動判別
//   ・point_use: 能力値コスト（対応表）＋年齢コスト（対応表）＋usefulness合計＋severity合計
//                ＋ゴールド換算（10ゴールドごとに1ポイント・端数切り上げ）。
//                対応表はゲーム実装の累計才能値表（skill.txt / age.txt 提供値）。
//                ゲーム内で再計算されるため編集不可（確認用の表示のみ）
using System.Globalization;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class PlayerToPresetDialog : Form
    {
        private static readonly string[] AbilityKeys =
        { "strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma" };
        // ゲームの SkillVM / TraitVM が持つフィールドのみ書き出す（余計なキーで読込が壊れないように）。
        private static readonly string[] SkillKeys =
        { "name", "description", "max_uses", "current_uses", "element", "skill_type", "effects", "usefulness" };
        private static readonly string[] TraitKeys = { "name", "description", "severity" };
        // ワールド側キャラフォルダから複製するファイル一式。
        private static readonly string[] CompanionFiles =
        {
            "face_image.png", "generated_image.png", "no_bg_image.png",
            "pixelated_image_original.png", "reduced_color_image.png", "prompts.json",
        };

        private readonly JsonObject _pd;          // player_data
        private readonly string _worldDir;        // worlds/{スロット}/ のパス。画像複製元の推定に使う
        private readonly string _charactersRoot;  // 出力基底（{Instantale}\characters）

        private ComboBox _cbLanguage;
        private TextBox _tbGold, _tbPointUse;

        public string ResultSummary { get; private set; }

        public PlayerToPresetDialog(JsonObject root, string filePath, string worldDir)
        {
            _pd = J.Obj(root, "player_data");
            _worldDir = worldDir;
            _charactersRoot = ResolveCharactersRoot(filePath);

            Text = I18n.T("p2p.title");
            Width = 640; Height = 720; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;

            // ── 流用項目（確認用・読み取り専用） ──
            Header(t, ref r, I18n.T("p2p.header.reuse"));
            RoText(t, ref r, I18n.T("p2p.name"), J.Str(_pd, "name"));
            RoText(t, ref r, I18n.T("p2p.category"), J.Str(_pd, "category"));
            RoText(t, ref r, I18n.T("p2p.age"), J.Int(_pd, "age").ToString());
            Note(t, ref r, I18n.T("p2p.note.reuse"));

            // ── 能力値（自動換算: original_ability_scores を四捨五入） ──
            Header(t, ref r, I18n.T("p2p.header.abilities"));
            var oas = J.Obj(_pd, "original_ability_scores");
            foreach (var key in AbilityKeys)
                RoText(t, ref r, $"{I18n.T("ability." + key)} ({key})", RoundedAbility(oas, key).ToString());

            // ── プリセット固有（gender は category から自動判別。gold/point_use は編集可） ──
            Header(t, ref r, I18n.T("p2p.header.edit"));
            RoText(t, ref r, I18n.T("p2p.gender"), GuessGender());
            Note(t, ref r, I18n.T("p2p.note.gender"));
            _cbLanguage = Combo(t, ref r, I18n.T("p2p.language"), new[] { "ja", "en" }, "ja");
            _tbGold = Edit(t, ref r, I18n.T("p2p.gold"), J.Int(_pd, "gold").ToString());
            // point_use はゲーム内で再計算されるため編集不可（確認用の表示のみ）。
            _tbPointUse = RoText(t, ref r, I18n.T("p2p.pointUse"), EstimatePointUse(J.Int(_pd, "gold")).ToString());
            Note(t, ref r, I18n.T("p2p.note.pointUse"));
            // 所持金の変更に追従して point_use の表示を再計算する。
            _tbGold.TextChanged += (_, _) =>
            {
                if (long.TryParse(_tbGold.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long g))
                    _tbPointUse.Text = EstimatePointUse(g).ToString();
            };

            // ── 出力先 ──
            RoText(t, ref r, I18n.T("p2p.outDir"), Path.Combine(_charactersRoot, SafeName()));
            Note(t, ref r, I18n.T("p2p.note.images"));

            scroll.Controls.Add(t);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var btnExport = new Button { Text = I18n.T("p2p.btnExport"), Width = 160 };
            var btnCancel = new Button { Text = I18n.T("btn.cancel"), Width = 100, DialogResult = DialogResult.Cancel };
            btnExport.Click += (_, _) => Export();
            bar.Controls.AddRange(new Control[] { btnExport, btnCancel });
            CancelButton = btnCancel;

            Controls.Add(scroll);
            Controls.Add(bar);
        }

        // ---------------- 出力 ----------------
        // character_sheet.json（XOR 難読化）と画像・prompts.json の複製を characters\{名前}\ へ書き出す。
        private void Export()
        {
            string name = J.Str(_pd, "name");
            if (string.IsNullOrWhiteSpace(name)) { Warn(I18n.T("p2p.warn.name")); return; }
            if (!long.TryParse(_tbGold.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long gold)) { Warn(I18n.T("p2p.warn.gold")); return; }
            long pointUse = EstimatePointUse(gold);

            string destDir = Path.Combine(_charactersRoot, SafeName());
            if (Directory.Exists(destDir) &&
                MessageBox.Show(this, I18n.T("p2p.overwriteConfirm", name), I18n.T("title.confirm"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            var missing = new List<string>();
            try
            {
                Directory.CreateDirectory(destDir);
                Codec.Save(Path.Combine(destDir, "character_sheet.json"), BuildSheet(gold, pointUse));

                string srcDir = SourceCharDir();
                foreach (var f in CompanionFiles)
                {
                    string src = srcDir == null ? null : Path.Combine(srcDir, f);
                    if (src != null && File.Exists(src)) File.Copy(src, Path.Combine(destDir, f), overwrite: true);
                    else missing.Add(f);
                }
            }
            catch (Exception ex)
            { MessageBox.Show(this, I18n.T("msg.exportFailed") + "\n" + ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

            string msg = I18n.T("p2p.exported", name, destDir);
            if (missing.Count > 0) msg += I18n.T("p2p.exportedMissing", string.Join("\n", missing));
            MessageBox.Show(this, msg, I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            ResultSummary = msg;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---------------- 生成 ----------------
        // 実プリセットと同じキー順で character_sheet.json 全体を組み立てる。
        private JsonObject BuildSheet(long gold, long pointUse)
        {
            var oas = J.Obj(_pd, "original_ability_scores");
            var vm = new JsonObject
            {
                ["name"] = J.Str(_pd, "name"),
                ["profile"] = J.Str(_pd, "profile"),
                ["category"] = J.Str(_pd, "category"),
                ["age"] = J.Int(_pd, "age"),
                ["look_description"] = J.Str(_pd, "look_description"),
            };
            foreach (var key in AbilityKeys) vm["attribute_" + key] = RoundedAbility(oas, key);
            vm["gold"] = gold;
            vm["skills"] = BuildSkills();
            vm["traits"] = BuildTraits();
            vm["point_use"] = pointUse;
            vm["gender"] = GuessGender();

            return new JsonObject
            {
                ["schema_version"] = 1,
                ["language"] = _cbLanguage.Text.Trim(),
                ["character_vm"] = vm,
            };
        }

        // skills（名前→スキルの辞書）を SkillVM 準拠の配列へ変換する。current_uses は max_uses に戻す。
        private JsonArray BuildSkills()
        {
            var arr = new JsonArray();
            if (J.Obj(_pd, "skills") is not JsonObject skills) return arr;
            foreach (var kv in skills)
            {
                if (kv.Value is not JsonObject o) continue;
                var s = new JsonObject();
                foreach (var key in SkillKeys)
                    if (o.ContainsKey(key)) s[key] = o[key]?.DeepClone();
                if (!s.ContainsKey("name")) s["name"] = kv.Key;
                if (s.ContainsKey("max_uses")) s["current_uses"] = s["max_uses"]?.DeepClone();
                arr.Add(s);
            }
            return arr;
        }

        // traits 配列を TraitVM 準拠のキーに限定して複製する。
        private JsonArray BuildTraits()
        {
            var arr = new JsonArray();
            if (_pd?["traits"] is not JsonArray traits) return arr;
            foreach (var n in traits)
            {
                if (n is not JsonObject o) continue;
                var t = new JsonObject();
                foreach (var key in TraitKeys)
                    if (o.ContainsKey(key)) t[key] = o[key]?.DeepClone();
                arr.Add(t);
            }
            return arr;
        }

        // ---------------- 推定・解決 ----------------
        private static long RoundedAbility(JsonObject oas, string key)
            => (long)Math.Round(J.Dbl(oas, key), MidpointRounding.AwayFromZero);

        // 能力値スコア → 累計才能値コスト（ゲームの対応表）。6〜30 の範囲外は端の値へ丸める。
        private static readonly Dictionary<long, long> AbilityCost = new()
        {
            [6] = -5, [7] = -4, [8] = -3, [9] = -2, [10] = -1, [11] = 0, [12] = 1, [13] = 1,
            [14] = 2, [15] = 2, [16] = 3, [17] = 5, [18] = 8, [19] = 12, [20] = 17, [21] = 23,
            [22] = 30, [23] = 38, [24] = 47, [25] = 57, [26] = 68, [27] = 80, [28] = 93,
            [29] = 107, [30] = 122,
        };
        // 年齢 → 累計才能値コスト（ゲームの対応表: 20〜50）。20 未満は 20 の値、
        // 50 超は表の末尾傾向（-2/歳）で外挿する。
        private static readonly Dictionary<long, long> AgeCost = new()
        {
            [20] = 69, [21] = 57, [22] = 47, [23] = 39, [24] = 32, [25] = 26, [26] = 21,
            [27] = 16, [28] = 12, [29] = 9, [30] = 7, [31] = 6, [32] = 5, [33] = 4, [34] = 4,
            [35] = 3, [36] = 3, [37] = 2, [38] = 2, [39] = 2, [40] = 1, [41] = 1, [42] = 1,
            [43] = 1, [44] = 0, [45] = -2, [46] = -4, [47] = -6, [48] = -8, [49] = -10, [50] = -12,
        };

        // point_use = 能力値コスト合計 + 年齢コスト + usefulness合計 + severity合計
        //           + ゴールド換算（10ゴールドごとに1ポイント・端数切り上げ）。
        private long EstimatePointUse(long gold)
        {
            var oas = J.Obj(_pd, "original_ability_scores");
            long total = 0;
            foreach (var key in AbilityKeys)
                total += AbilityCost[Math.Clamp(RoundedAbility(oas, key), 6, 30)];

            long age = J.Int(_pd, "age");
            total += age > 50 ? -12 - 2 * (age - 50) : AgeCost[Math.Clamp(age, 20, 50)];

            double usefulness = 0;
            if (J.Obj(_pd, "skills") is JsonObject skills)
                foreach (var kv in skills)
                    if (kv.Value is JsonObject o) usefulness += J.Dbl(o, "usefulness");
            total += (long)Math.Ceiling(usefulness);

            if (_pd?["traits"] is JsonArray traits)
                foreach (var n in traits)
                    if (n is JsonObject o) total += J.Int(o, "severity");

            if (gold > 0) total += (gold + 9) / 10;

            return total;
        }

        // category の語から性別を自動判別する（woman/girl 等を先に判定。"woman" は "man" を含むため）。
        private string GuessGender()
        {
            string c = J.Str(_pd, "category").ToLowerInvariant();
            if (c.Contains("woman") || c.Contains("girl") || c.Contains("lady") || c.Contains("female")) return "♀";
            return "♂";
        }

        // 画像・prompts.json の複製元キャラフォルダ。image_src.face のパスを優先し、
        // 無ければ worlds\{スロット}\characters\{名前}\ を試す。見つからなければ null。
        private string SourceCharDir()
        {
            string face = J.Str(J.Obj(_pd, "image_src"), "face");
            try
            {
                if (!string.IsNullOrEmpty(face))
                {
                    string d = Path.GetDirectoryName(face);
                    if (Directory.Exists(d)) return d;
                }
            }
            catch { }
            if (_worldDir != null)
            {
                string d = Path.Combine(_worldDir, "characters", J.Str(_pd, "name"));
                if (Directory.Exists(d)) return d;
            }
            return null;
        }

        // 開いたファイルパスからプリセット基底 {Instantale}\characters を導く。
        // saves\{スロット}\savedata.json / worlds\{スロット}\world_data.json のどちらでも
        // 2階層上を基底とみなす。導けない場合はゲーム既定の %LocalAppData% 配下へ倒す。
        internal static string ResolveCharactersRoot(string filePath)
        {
            try
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    string slotDir = Path.GetDirectoryName(filePath);   // saves\{スロット} 等
                    string parent = Path.GetDirectoryName(slotDir);      // saves / worlds
                    string leaf = Path.GetFileName(parent) ?? "";
                    if (leaf.Equals("saves", StringComparison.OrdinalIgnoreCase) ||
                        leaf.Equals("worlds", StringComparison.OrdinalIgnoreCase))
                        return Path.Combine(Path.GetDirectoryName(parent), "characters");
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Darmabeko", "Instantale", "characters");
        }

        // プリセットのフォルダ名（キャラ名からパスに使えない文字を除去）。
        private string SafeName()
        {
            string name = J.Str(_pd, "name", "player");
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
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
        private static TextBox Edit(TableLayoutPanel t, ref int r, string label, string val)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var tb = new TextBox { Dock = DockStyle.Fill, Text = val };
            t.Controls.Add(tb, 1, r); r++;
            return tb;
        }
        private static TextBox RoText(TableLayoutPanel t, ref int r, string label, string val)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var tb = new TextBox { Dock = DockStyle.Fill, Text = val, ReadOnly = true, BackColor = SystemColors.Control };
            t.Controls.Add(tb, 1, r); r++;
            return tb;
        }
        // 候補付きコンボ（自由入力も可）。既定値を選択状態にする。
        private static ComboBox Combo(TableLayoutPanel t, ref int r, string label, string[] items, string def)
        {
            t.Controls.Add(Lbl(label), 0, r);
            var cb = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var it in items) cb.Items.Add(it);
            cb.Text = def;
            t.Controls.Add(cb, 1, r); r++;
            return cb;
        }
        private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Padding = new Padding(2, 6, 4, 0) };
    }
}
