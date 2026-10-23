// MOD タブ。InstantaleModLoader の MOD がセーブの外（`state\`）に持つ周回ごとの記録を一覧で見せ、編集する。
//   ・家（330_real_estate）           state\real_estate\<世界>×<主人公>.json の contracts
//   ・出資した施設（331_facility_investment） state\facility_investment\<世界>×<主人公>.json の holdings
//   ・評判と二つ名（317_reputation）   state\reputation\<世界>×<主人公>.json の areas / epithet
//   ・勢力（339_town_factions）        state\town_factions_ledger\<世界>×<主人公>.json
// どれも MOD がゲームの最中に書く控えで、セーブには入らない（MOD の家や施設は遊んでいる間だけ建つ）。
//
// 編集:
//   ・表に出している項目は、その場で直せる（淡い黄色）。控えのファイルは「JSON編集...」「ファイルを開く」で
//     どのみち直せるので、表で止めることはしない。止めているのは「街」（id と名前を組み立てた表示で、
//     家・施設の街を変えるには MOD の配置も要るため。JSON編集で直す）と、数から作る派生の欄だけ。
//   ・名前・種類のように MOD の別の控え（建物 modfacility・人物 modnpc の snapshot）に写しがある項目は、写しも一緒に直す。
//   ・数の欄は数でない値を受け付けない（型が変わると MOD が読めなくなる）。範囲のある欄（見られ方・仲）は範囲も見る。
//   ・書き込みはセーブの上書き保存のとき（ModLoaderLink.SetPendingState → CommitPendingState）。
// 日付はゲームの経過日数（world_data.days_elapsed）で、今の日との差も添える。
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class ModTab : UserControl
    {
        private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
        private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false };
        private readonly Label _note = new()
        {
            Dock = DockStyle.Top, Height = 40, ForeColor = SystemColors.GrayText,
            Padding = new Padding(8, 6, 8, 6), AutoSize = false,
        };
        private JsonObject _root;
        private long _today;
        private readonly List<DataGridView> _grids = new();   // 幅を表示域に合わせる表
        private readonly Dictionary<string, Label> _pendingMarks = new(StringComparer.OrdinalIgnoreCase);   // 欄ごとの「編集中」の印

        // 編集できるセルの背景（どこが直せるかを見せる）。
        private static readonly Color EditableBack = Color.FromArgb(255, 252, 228);

        // MOD の別の控え（写しの置き場）。
        private const string FacilityCopies = "modfacility", NpcCopies = "modnpc";
        private const string RealEstateMod = "330_real_estate", InvestmentMod = "331_facility_investment";

        public ModTab()
        {
            Controls.Add(_scroll);
            Controls.Add(_empty);
            Controls.Add(_note);
            _scroll.Resize += (_, _) => FitWidths();
            Localize();
            Bind(null);   // ファイルを開くまでの案内
            I18n.LanguageChanged += OnLanguageChanged;
        }

        private void Localize() => _note.Text = I18n.T("mod.note");
        private void OnLanguageChanged() { Localize(); Bind(_root); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= OnLanguageChanged;
            base.Dispose(disposing);
        }

        public void Bind(JsonObject root)
        {
            _root = root;
            _grids.Clear();
            _pendingMarks.Clear();
            Ui.DisposeChildren(_scroll);
            string why = root == null ? I18n.T("mod.empty.noFile")
                       : ModLoaderLink.Root == null ? I18n.T("mod.empty.noLoader")
                       : J.Obj(root, "player_data") == null ? I18n.T("mod.empty.noPlayer")
                       : null;
            _empty.Text = why ?? "";
            _empty.Visible = why != null;
            _scroll.Visible = why == null;
            if (why != null) return;
            _today = J.Int(J.Obj(root, "world_data"), "days_elapsed", -1);

            // Dock=Top は後から足したものが上に来るので、表示順と逆に足す。
            AddSection("mod.group.factions", "town_factions", "town_factions_ledger", false, BuildFactions);
            AddSection("mod.group.reputation", "reputation", "reputation", true, BuildReputation);
            AddSection("mod.group.facilities", "facility_investment", "facility_investment", true, BuildFacilities);
            AddSection("mod.group.houses", "real_estate", "real_estate", true, BuildHouses);
            FitWidths();
        }

        // 表の幅を表示域いっぱいに揃える（FlowLayout の中では Dock が効かないため）。
        private void FitWidths()
        {
            int w = Math.Max(400, _scroll.ClientSize.Width - 40);
            foreach (var g in _grids) if (!g.IsDisposed) g.Width = w;
        }

        // 1 MOD ぶんの折りたたみ。上に「JSON編集...」「ファイルを開く」、下に中身。
        // MOD が無い・控えが無いときは案内の1行だけ（控えのファイルがあれば開くボタンは出す）。
        private void AddSection(string titleKey, string modName, string folder, bool allowWorldOnly, Func<string, JsonObject, Control> build)
        {
            var group = new CollapsibleGroup(I18n.T(titleKey), true);
            var box = Stack();
            if (ModLoaderLink.FindMod(modName) == null) box.Controls.Add(Info(I18n.T("mod.noMod", modName)));
            else
            {
                var (path, st) = ModLoaderLink.OpenPlaythroughState(_root, folder, allowWorldOnly);
                box.Controls.Add(SectionBar(path, st));
                Control body;
                try { body = st == null ? null : build(path, st); }
                catch (Exception ex) { body = Info(I18n.T("mod.readFailed", ex.Message)); }
                box.Controls.Add(body ?? Info(I18n.T("mod.noRecord")));
            }
            group.Content.Controls.Add(box);
            _scroll.Controls.Add(group);
        }

        // 欄の操作列: JSON編集 / ファイルを開く / 編集中（未保存）の印 / 控えの場所。
        private Control SectionBar(string path, JsonObject st)
        {
            var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            var edit = new Button { Text = I18n.T("btn.jsonEdit"), AutoSize = true, Enabled = st != null };
            edit.Click += (_, _) =>
            {
                using var d = new JsonEditDialog(Path.GetFileName(path), st);
                if (d.ShowDialog(FindForm()) != DialogResult.OK || d.ResultNode is not JsonObject edited) return;
                ModLoaderLink.SetPendingState(path, edited);
                Ui.Defer(this, () => Bind(_root));
            };
            var open = new Button { Text = I18n.T("mod.openFile"), AutoSize = true, Enabled = path != null && File.Exists(path) };
            open.Click += (_, _) => OpenFile(path);
            bar.Controls.Add(edit);
            bar.Controls.Add(open);
            var mark = new Label
            {
                Text = I18n.T("mod.pending"), AutoSize = true, ForeColor = Color.DarkGoldenrod, Padding = new Padding(6, 7, 0, 0),
                Visible = ModLoaderLink.IsPendingState(path),
            };
            bar.Controls.Add(mark);
            if (path != null)
            {
                _pendingMarks[path] = mark;
                // 場所は ModLoader のフォルダからの相対で見せる（state\<MOD>\<世界>×<主人公>.json）。
                string shown = ModLoaderLink.Root is string r ? Path.GetRelativePath(r, path) : path;
                bar.Controls.Add(new Label { Text = shown, AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(6, 7, 0, 0) });
            }
            return bar;
        }

        // 控えのファイルを既定のアプリで開く。編集中（未保存）の内容はまだファイルに無いことを先に知らせる。
        private void OpenFile(string path)
        {
            if (path == null || !File.Exists(path)) return;
            if (ModLoaderLink.IsPendingState(path))
                MessageBox.Show(FindForm(), I18n.T("mod.openPendingNote"), I18n.T("title.confirm"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(FindForm(), ex.Message, I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        // 控えを書き換えた（保存時に書く）。表は作り直さず（スクロールや入力位置を保つ）、欄の「編集中」の印だけ出す。
        private void Touched(string path, JsonObject st)
        {
            ModLoaderLink.SetPendingState(path, st);
            if (path != null && _pendingMarks.TryGetValue(path, out var mark) && !mark.IsDisposed) mark.Visible = true;
        }

        // MOD の別の控え（建物 modfacility・人物 modnpc）にある写し（<MOD>.<id>.snapshot.<key>）を直す。写しが無ければ何もしない。
        private void SetCopy(string folder, string mod, string id, string key, string value)
        {
            if (string.IsNullOrEmpty(id)) return;
            var (path, st) = ModLoaderLink.OpenPlaythroughState(_root, folder, false);
            if (st?[mod]?[id]?["snapshot"] is not JsonObject snap) return;
            snap[key] = value;
            ModLoaderLink.SetPendingState(path, st);
        }

        // ---------------- 家（330_real_estate） ----------------
        private Control BuildHouses(string path, JsonObject st)
        {
            var contracts = J.Arr(st, "contracts");
            if (contracts == null || contracts.Count == 0) return null;
            var grid = Grid(path, st, new[] { "mod.col.name", "mod.col.town", "mod.col.contract", "mod.col.rentG", "mod.col.termDays", "mod.col.dueDay", "mod.col.left", "mod.col.keeper", "mod.col.storage" });
            var kinds = new List<(string code, string label)> { ("owned", I18n.T("mod.house.owned")), ("rent", I18n.T("mod.house.rent")) };
            foreach (var k in contracts.OfType<JsonObject>().Select(c => J.Str(c, "kind")).Where(k => k.Length > 0 && kinds.All(x => x.code != k)).Distinct())
                kinds.Add((k, k));
            ComboColumn(grid, 2, kinds.Select(k => k.label).ToList());
            Editable(grid, 0, (t, s) =>
            {
                var c = (JsonObject)t;
                c["name"] = s;
                SetCopy(FacilityCopies, RealEstateMod, J.Str(c, "facility"), "name", s);   // 街に建つ建物の名前
                return true;
            });
            Editable(grid, 2, (t, s) =>
            {
                string code = kinds.FirstOrDefault(k => k.label == s).code;
                if (code == null) return false;
                ((JsonObject)t)["kind"] = code;
                return true;
            });
            Editable(grid, 3, (t, s) => SetLong((JsonObject)t, "rent", s, 0, long.MaxValue));
            Editable(grid, 4, (t, s) => SetLong((JsonObject)t, "term", s, 0, long.MaxValue));
            Editable(grid, 5, (t, s) => SetLongOrNull((JsonObject)t, "due", s));
            Editable(grid, 7, (t, s) =>
            {
                var c = (JsonObject)t;
                if (J.Obj(c, "keeper") is not JsonObject keeper) return false;
                keeper["name"] = s;
                if (J.Obj(keeper, "fields") is JsonObject fields) fields["name"] = s;
                SetCopy(NpcCopies, RealEstateMod, J.Str(keeper, "id"), "name", s);   // 管理人の実体
                return true;
            });
            Derived(grid, (row, t) => row.Cells[6].Value = Relative(((JsonObject)t)["due"]));
            foreach (var c in contracts.OfType<JsonObject>())
            {
                string kind = J.Str(c, "kind");
                int i = grid.Rows.Add(
                    J.Str(c, "name"), Town(c),
                    kinds.FirstOrDefault(k => k.code == kind).label ?? kind,
                    J.Int(c, "rent").ToString(), J.Int(c, "term").ToString(), Raw(c["due"]), Relative(c["due"]),
                    J.Str(J.Obj(c, "keeper"), "name"),
                    (J.Obj(c, "storage")?.Count ?? 0).ToString());
                grid.Rows[i].Tag = c;
                if (J.Obj(c, "keeper") == null) Lock(grid.Rows[i].Cells[7]);   // 管理人の居ない古い契約（最初の滞在で決まる）
            }
            var box = Stack(grid);
            int shared = J.Obj(st, "shared")?.Count ?? 0;
            int seized = J.Obj(st, "seized")?.Count ?? 0;
            if (shared > 0) box.Controls.Add(Info(I18n.T("mod.house.shared", shared)));
            if (seized > 0) box.Controls.Add(Info(I18n.T("mod.house.seized", seized)));
            return box;
        }

        // ---------------- 出資した施設（331_facility_investment） ----------------
        private Control BuildFacilities(string path, JsonObject st)
        {
            var holdings = J.Arr(st, "holdings");
            if (holdings == null || holdings.Count == 0) return null;
            var grid = Grid(path, st, new[] { "mod.col.name", "mod.col.town", "mod.col.kind", "mod.col.tier", "mod.col.keeper", "mod.col.priceG", "mod.col.builtDay", "mod.col.collectedDay", "mod.col.since", "mod.col.receivedG" });
            Editable(grid, 0, (t, s) =>
            {
                var h = (JsonObject)t;
                h["name"] = s;
                SetCopy(FacilityCopies, InvestmentMod, J.Str(h, "facility"), "name", s);
                return true;
            });
            Editable(grid, 2, (t, s) =>
            {
                var h = (JsonObject)t;
                h["kind"] = s;
                // 施設の種類は建物の facility_type、主の job と揃っている（ゲームが作る主と同じ）。
                SetCopy(FacilityCopies, InvestmentMod, J.Str(h, "facility"), "facility_type", s);
                if (J.Obj(h, "keeper_fields") is JsonObject kf) kf["job"] = s;
                SetCopy(NpcCopies, InvestmentMod, J.Str(h, "keeper"), "job", s);
                return true;
            });
            Editable(grid, 3, (t, s) => { ((JsonObject)t)["tier"] = s; return true; });
            Editable(grid, 4, (t, s) =>
            {
                var h = (JsonObject)t;
                h["keeper_name"] = s;
                if (J.Obj(h, "keeper_fields") is JsonObject kf) kf["name"] = s;
                SetCopy(NpcCopies, InvestmentMod, J.Str(h, "keeper"), "name", s);
                return true;
            });
            Editable(grid, 5, (t, s) => SetLong((JsonObject)t, "price", s, 0, long.MaxValue));
            Editable(grid, 6, (t, s) => SetLong((JsonObject)t, "built", s, 0, long.MaxValue));
            Editable(grid, 7, (t, s) => SetLong((JsonObject)t, "collected", s, 0, long.MaxValue));
            Editable(grid, 9, (t, s) => SetLong((JsonObject)t, "received", s, 0, long.MaxValue));
            Derived(grid, (row, t) => row.Cells[8].Value = Relative(((JsonObject)t)["collected"]));
            foreach (var h in holdings.OfType<JsonObject>())
            {
                int i = grid.Rows.Add(
                    J.Str(h, "name"), Town(h), J.Str(h, "kind"), J.Str(h, "tier"), J.Str(h, "keeper_name"),
                    J.Int(h, "price").ToString(), Raw(h["built"]), Raw(h["collected"]), Relative(h["collected"]),
                    J.Int(h, "received").ToString());
                grid.Rows[i].Tag = h;
            }
            return Stack(grid);
        }

        // ---------------- 評判と二つ名（317_reputation） ----------------
        private Control BuildReputation(string path, JsonObject st)
        {
            var box = Stack();
            // 二つ名（世界に1つ）。名前と由来をその場で直せる。まだ無ければ入力すると作る。
            {
                var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 2, 0, 6) };
                t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 760));
                JsonObject Ep() => J.Obj(st, "epithet") ?? (JsonObject)(st["epithet"] = new JsonObject());
                var name = new TextBox { Text = J.Str(J.Obj(st, "epithet"), "epithet"), Width = 300, BackColor = EditableBack };
                var desc = new TextBox { Text = J.Str(J.Obj(st, "epithet"), "description"), Width = 750, Height = 48, Multiline = true, BackColor = EditableBack, ScrollBars = ScrollBars.Vertical };
                name.Leave += (_, _) => { if (name.Text != J.Str(J.Obj(st, "epithet"), "epithet")) { Ep()["epithet"] = name.Text; Touched(path, st); } };
                desc.Leave += (_, _) => { if (desc.Text != J.Str(J.Obj(st, "epithet"), "description")) { Ep()["description"] = desc.Text; Touched(path, st); } };
                t.Controls.Add(new Label { Text = I18n.T("mod.rep.epithetLabel"), AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, 0, 0);
                t.Controls.Add(name, 1, 0);
                t.Controls.Add(new Label { Text = I18n.T("mod.rep.epithetDesc"), AutoSize = true, Padding = new Padding(0, 6, 4, 0) }, 0, 1);
                t.Controls.Add(desc, 1, 1);
                box.Controls.Add(t);
            }
            if (J.Obj(st, "areas") is JsonObject areas && areas.Count > 0)
            {
                var grid = Grid(path, st, new[] { "mod.col.town", "mod.col.reputation", "mod.col.updatedDay", "mod.col.since" }, wrapColumn: 1, height: 300);
                Editable(grid, 1, (t, s) => { ((JsonObject)t)["reputation"] = s; return true; });
                Editable(grid, 2, (t, s) => SetLong((JsonObject)t, "day", s, 0, long.MaxValue));
                Derived(grid, (row, t) => row.Cells[3].Value = Relative(((JsonObject)t)["day"]));
                foreach (var kv in areas.OrderBy(k => long.TryParse(k.Key, out var n) ? n : long.MaxValue))
                    if (kv.Value is JsonObject a)
                    {
                        int i = grid.Rows.Add(AreaName(J.Str(a, "area", kv.Key), J.Str(a, "name")), J.Str(a, "reputation"), Raw(a["day"]), Relative(a["day"]));
                        grid.Rows[i].Tag = a;
                    }
                box.Controls.Add(grid);
            }
            box.Controls.Add(Info(I18n.T("mod.rep.note")));
            return box;
        }

        // ---------------- 勢力（339_town_factions） ----------------
        private sealed class FactionRow { public JsonObject Faction; public string Id; }

        private Control BuildFactions(string path, JsonObject st)
        {
            var factions = J.Arr(st, "factions");
            // 勢力を設けなかった世界（世界の設定に勢力が無い・組織を設ける設定が切）は status が none。
            if ((factions == null || factions.Count == 0) && J.Str(st, "status") == "none") return Info(I18n.T("mod.fac.none"));
            if (factions == null || factions.Count == 0) return null;
            var list = factions.OfType<JsonObject>().ToList();
            string Fn(string id) => list.FirstOrDefault(f => J.Str(f, "id") == id) is JsonObject f && J.Str(f, "name").Length > 0 ? J.Str(f, "name") : id;
            var towns = J.Obj(st, "towns");
            // 主人公の見られ方（standing[勢力]）。まだ無い勢力は、直したときに作る。
            JsonObject Standing(string id)
            {
                if (st["standing"] is not JsonObject all) st["standing"] = all = new JsonObject();
                if (all[id] is not JsonObject s) all[id] = s = new JsonObject { ["score"] = 0, ["title"] = "", ["reason"] = "" };
                return s;
            }

            var box = Stack();
            // 勢力ごとの見られ方（-100〜100）と、勢力内での主人公の呼び名・治める街。
            var g1 = Grid(path, st, new[] { "mod.col.faction", "mod.col.kind", "mod.col.score", "mod.col.stage", "mod.col.title", "mod.col.towns", "mod.col.reason" }, wrapColumn: 6, height: 160);
            Editable(g1, 0, (t, s) => { ((FactionRow)t).Faction["name"] = s; return true; });
            Editable(g1, 1, (t, s) => { ((FactionRow)t).Faction["kind"] = s; return true; });
            Editable(g1, 2, (t, s) => SetLong(Standing(((FactionRow)t).Id), "score", s, -100, 100));
            Editable(g1, 4, (t, s) => { Standing(((FactionRow)t).Id)["title"] = s; return true; });
            Editable(g1, 6, (t, s) => { Standing(((FactionRow)t).Id)["reason"] = s; return true; });
            Derived(g1, (row, t) => row.Cells[3].Value = I18n.T(StandingKey(J.Int(J.Obj(J.Obj(st, "standing"), ((FactionRow)t).Id), "score"))));
            foreach (var f in list)
            {
                string id = J.Str(f, "id");
                var s = J.Obj(J.Obj(st, "standing"), id);
                var ruled = towns?.Where(x => J.Str(x.Value as JsonObject, "faction") == id).Select(x => AreaName(x.Key, null)) ?? Enumerable.Empty<string>();
                int i = g1.Rows.Add(J.Str(f, "name"), J.Str(f, "kind"),
                    J.Int(s, "score").ToString(), I18n.T(StandingKey(J.Int(s, "score"))),
                    J.Str(s, "title"), string.Join(", ", ruled), J.Str(s, "reason"));
                g1.Rows[i].Tag = new FactionRow { Faction = f, Id = id };
            }
            box.Controls.Add(g1);

            // 勢力どうしの仲（-2〜2）。
            if (J.Obj(st, "relations") is JsonObject rel && rel.Count > 0)
            {
                var g2 = Grid(path, st, new[] { "mod.col.factions", "mod.col.relationStage", "mod.col.stage", "mod.col.reason" }, wrapColumn: 3, height: 140);
                Editable(g2, 1, (t, s) => SetLong((JsonObject)t, "stage", s, -2, 2));
                Editable(g2, 3, (t, s) => { ((JsonObject)t)["evidence"] = s; return true; });
                Derived(g2, (row, t) => row.Cells[2].Value = I18n.T(RelationKey(J.Int((JsonObject)t, "stage"))));
                foreach (var kv in rel)
                {
                    if (kv.Value is not JsonObject r) continue;
                    long stage = J.Int(r, "stage");
                    int i = g2.Rows.Add(string.Join(" × ", kv.Key.Split('|').Select(Fn)), stage.ToString(), I18n.T(RelationKey(stage)), J.Str(r, "evidence"));
                    g2.Rows[i].Tag = r;
                }
                box.Controls.Add(Info(I18n.T("mod.fac.relations")));
                box.Controls.Add(g2);
            }

            // 住人の立場（NPC ごとに街・勢力・関わり方）。勢力と立場は一覧から選ぶ。街は id を入れる。
            if (J.Obj(st, "people") is JsonObject people && people.Count > 0)
            {
                var npcs = J.Obj(_root, "npcs");
                var g3 = Grid(path, st, new[] { "mod.col.npc", "mod.col.townId", "mod.col.faction", "mod.col.role", "mod.col.reason" }, wrapColumn: 4, height: 260);
                var roles = new List<string> { "member", "follower", "dissenter", "indifferent", "other" };
                foreach (var v in people.Select(p => J.Str(p.Value as JsonObject, "role")).Where(r => r.Length > 0 && !roles.Contains(r)).Distinct()) roles.Add(v);
                string RoleText(string r) => I18n.Opt("mod.role." + r) ?? r;
                var factionIds = list.Select(f => J.Str(f, "id")).ToList();
                ComboColumn(g3, 2, factionIds.Select(Fn).ToList());
                ComboColumn(g3, 3, roles.Select(RoleText).ToList());
                Editable(g3, 1, (t, s) =>
                {
                    string id = s.Split(':')[0].Trim();   // 「3」でも「3: 名前」でも
                    if (id.Length == 0) return false;
                    ((JsonObject)t)["town"] = id;
                    return true;
                });
                Editable(g3, 2, (t, s) =>
                {
                    string id = factionIds.FirstOrDefault(x => Fn(x) == s);
                    if (id == null) return false;
                    ((JsonObject)t)["faction"] = id;
                    return true;
                });
                Editable(g3, 3, (t, s) =>
                {
                    string code = roles.FirstOrDefault(r => RoleText(r) == s);
                    if (code == null) return false;
                    ((JsonObject)t)["role"] = code;
                    return true;
                });
                Editable(g3, 4, (t, s) => { ((JsonObject)t)["reason"] = s; return true; });
                Derived(g3, (row, t) => row.Cells[1].Value = AreaName(J.Str((JsonObject)t, "town"), null));
                foreach (var kv in people.OrderBy(k => long.TryParse(k.Key, out var n) ? n : long.MaxValue))
                {
                    if (kv.Value is not JsonObject p) continue;
                    string npcName = npcs?[kv.Key] is JsonObject n ? J.Str(n, "name") : I18n.T("mod.fac.goneNpc");
                    int i = g3.Rows.Add($"{kv.Key}: {npcName}", AreaName(J.Str(p, "town"), null), Fn(J.Str(p, "faction")), RoleText(J.Str(p, "role")), J.Str(p, "reason"));
                    g3.Rows[i].Tag = p;
                }
                box.Controls.Add(Info(I18n.T("mod.fac.people", people.Count)));
                box.Controls.Add(g3);
            }
            box.Controls.Add(Info(I18n.T("mod.fac.note")));
            return box;
        }

        // 見られ方の段階（-100〜100）。339_ の区切り: 敵対 ≦-61 / 緊張 ≦-21 / 中立 / 友好 ≧20 / 盟友 ≧60。
        private static string StandingKey(long s) => s <= -61 ? "mod.stage.hostile" : s <= -21 ? "mod.stage.tense"
            : s >= 60 ? "mod.stage.ally" : s >= 20 ? "mod.stage.friendly" : "mod.stage.neutral";

        // 勢力どうしの仲（-2〜2）。一番上は「同盟」。
        private static string RelationKey(long s) => s <= -2 ? "mod.stage.hostile" : s == -1 ? "mod.stage.tense"
            : s >= 2 ? "mod.stage.alliance" : s == 1 ? "mod.stage.friendly" : "mod.stage.neutral";

        // ---------------- 値の書き込み ----------------

        private static bool SetLong(JsonObject o, string key, string text, long min, long max)
        {
            if (!long.TryParse(text?.Trim(), out var v) || v < min || v > max) return false;
            o[key] = v;
            return true;
        }

        // 空欄なら null（期限なし）。
        private static bool SetLongOrNull(JsonObject o, string key, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { o[key] = null; return true; }
            return SetLong(o, key, text, 0, long.MaxValue);
        }

        // ---------------- 部品 ----------------

        // 控えの area / area_name から「id: 名前」。名前はセーブの areas を優先する（控えの名前は契約した時点のもの）。
        private string Town(JsonObject o) => AreaName(J.Str(o, "area"), J.Str(o, "area_name"));

        private string AreaName(string id, string fallback)
        {
            if (string.IsNullOrEmpty(id)) return fallback ?? "";
            string name = J.Str(J.Obj(J.Obj(_root, "areas"), id), "name");
            if (name.Length == 0) name = fallback ?? "";
            return name.Length > 0 ? $"{id}: {name}" : id;
        }

        private static long? DayOf(JsonNode n)
        {
            if (n is not JsonValue v) return null;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var x)) return (long)x;
            return null;
        }

        // 日の数（編集欄用）。null は空欄。
        private static string Raw(JsonNode n) => DayOf(n)?.ToString() ?? "";

        // 今日との差（あと何日／何日前／今日）。
        private string Relative(JsonNode n)
        {
            if (DayOf(n) is not long d || _today < 0) return "";
            long diff = d - _today;
            return diff == 0 ? I18n.T("mod.dayToday") : diff > 0 ? I18n.T("mod.dayAfter", diff) : I18n.T("mod.dayAgo", -diff);
        }

        private static Label Info(string text) => new()
        {
            Text = text, AutoSize = true, MaximumSize = new Size(900, 0),
            Padding = new Padding(2, 4, 2, 4), Dock = DockStyle.Top,
        };

        // 上から積む入れ物（Dock=Top の逆順を気にしないよう FlowLayout で縦に並べる）。
        private static FlowLayoutPanel Stack(params Control[] items)
        {
            var p = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.TopDown, WrapContents = false,
            };
            foreach (var c in items) p.Controls.Add(c);
            return p;
        }

        // 表。列は既定で読むだけ。Editable で列ごとに書き込み先を足す（行の Tag を書き込み関数へ渡す）。
        // wrapColumn の列だけ折り返し、残りの幅を受け持つ。高さは行に合わせて縮め、height を上限にする。
        private DataGridView Grid(string path, JsonObject st, string[] headerKeys, int wrapColumn = -1, int height = 120)
        {
            var g = new DataGridView
            {
                Width = 1000, Height = height,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                BackgroundColor = SystemColors.Window,
                Margin = new Padding(2, 2, 2, 8),
            };
            for (int i = 0; i < headerKeys.Length; i++)
            {
                var col = new DataGridViewTextBoxColumn { HeaderText = I18n.T(headerKeys[i]), SortMode = DataGridViewColumnSortMode.NotSortable, ReadOnly = true };
                if (i == wrapColumn)
                {
                    col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                    col.MinimumWidth = 240;
                    col.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
                }
                g.Columns.Add(col);
            }
            var edit = new GridEdit();
            g.Tag = edit;

            // 編集の確定: 書き込み先へ入れ、通らなければ元の値に戻す。通れば派生の欄を更新して「保存時に書く」へ回す。
            object before = null;
            g.CellBeginEdit += (_, e) => before = g.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
            g.CellEndEdit += (_, e) =>
            {
                var row = g.Rows[e.RowIndex];
                var cell = row.Cells[e.ColumnIndex];
                if (row.Tag == null || !edit.Setters.TryGetValue(e.ColumnIndex, out var set)) return;
                string text = cell.Value?.ToString() ?? "";
                if (text == (before?.ToString() ?? "")) return;
                if (!set(row.Tag, text))
                {
                    cell.Value = before;
                    MessageBox.Show(FindForm(), I18n.T("mod.edit.invalid", text), I18n.T("title.confirm"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                edit.Derive?.Invoke(row, row.Tag);
                Touched(path, st);
            };
            g.DataError += (_, e) => e.ThrowException = false;

            // 行の高さは表示されてから決まるので、そこで高さを詰め、最初のセルが選ばれた見た目を消す。
            void Fit()
            {
                if (g.IsDisposed) return;
                g.AutoResizeRows(DataGridViewAutoSizeRowsMode.AllCells);
                int rows = g.Rows.Cast<DataGridViewRow>().Sum(r => r.Height);
                int bar = g.Columns.Cast<DataGridViewColumn>().Sum(c => c.Width) > g.ClientSize.Width ? SystemInformation.HorizontalScrollBarHeight : 0;
                g.Height = Math.Min(height, g.ColumnHeadersHeight + rows + bar + 3);
                g.ClearSelection();
            }
            g.HandleCreated += (_, _) => g.BeginInvoke(Fit);
            g.Resize += (_, _) => { if (g.IsHandleCreated) g.BeginInvoke(Fit); };
            _grids.Add(g);
            return g;
        }

        private sealed class GridEdit
        {
            public readonly Dictionary<int, Func<object, string, bool>> Setters = new();
            public Action<DataGridViewRow, object> Derive;
        }

        // 列を編集できるようにする（淡い黄色）。set は行の Tag へ書き、受け付けない値なら false。
        private static void Editable(DataGridView g, int col, Func<object, string, bool> set)
        {
            g.Columns[col].ReadOnly = false;
            g.Columns[col].DefaultCellStyle.BackColor = EditableBack;
            ((GridEdit)g.Tag).Setters[col] = set;
        }

        // 編集した後に作り直す派生の欄（段階・あと何日など）。
        private static void Derived(DataGridView g, Action<DataGridViewRow, object> derive) => ((GridEdit)g.Tag).Derive = derive;

        // 行ごとに編集を止める（書き込み先が無い行）。
        private static void Lock(DataGridViewCell c)
        {
            c.ReadOnly = true;
            c.Style.BackColor = SystemColors.Window;
        }

        // 列を選択式にする（決まった語のどれか）。Editable より先に呼ぶ（列を作り替えるため）。
        private static void ComboColumn(DataGridView g, int col, List<string> items)
        {
            var old = g.Columns[col];
            var combo = new DataGridViewComboBoxColumn
            {
                HeaderText = old.HeaderText, SortMode = DataGridViewColumnSortMode.NotSortable,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox, FlatStyle = FlatStyle.Flat, ReadOnly = true,
            };
            combo.Items.AddRange(items.Distinct().Cast<object>().ToArray());
            g.Columns.RemoveAt(col);
            g.Columns.Insert(col, combo);
            g.EditMode = DataGridViewEditMode.EditOnEnter;
        }
    }
}
