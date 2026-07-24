// facility のエクスポート/インポート。
// エクスポート: facility レコード(JSON)＋ backgrounds/{名前}/image.png を 1 つの zip にまとめる。
//   connections は移植先で無効な ID になるため保持しない（インポート時に接続先から張り直す）。
//   owner(NPC ID) も移植先では別の NPC を指してしまうため null にする。
// インポート: zip から取り込み、配置先(ダンジョン以外の area)と接続先施設を指定して、
// 接続先と同じノードの facilities へ挿入し、双方向の connections を張る。背景画像は backgrounds/ へ展開する。
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // zip から読み出した facility パッケージ。
    internal sealed class FacilityPackage
    {
        public JsonObject Facility;         // facility 本体（クローン済み）
        public string OriginalName = "";   // 出力時の名前（背景画像フォルダ名）
        public string SourceWorld = "";    // 出典ワールド名
        public byte[] Image;                // 背景画像 image.png（無ければ null）
    }

    // facility\ ライブラリ一覧の表示用メタ情報（facility 本体は読まず、名前・出典・画像のみ）。
    internal sealed class FacilityPackageInfo
    {
        public string Path = "";          // zip のフルパス
        public string Name = "";          // 表示名
        public string SourceWorld = "";   // 出典ワールド名
        public string Type = "";          // facility_type
        public byte[] Image;              // 背景画像（あれば）
    }

    // facility 移植（エクスポート/インポート）の入出力ロジック。UI は FacilityImportDialog 側。
    internal static class FacilityPortability
    {
        public const string Format = "instantale_facility";

        // エクスポート先のフォルダ名を設定に従って決める（NPC と同じ「ワールド毎に分ける」設定を使う）。
        public static string ExportWorldName(string source)
            => (Settings.Current?.NpcExportPerWorld ?? true) ? source : NpcPortability.AllWorld;

        // exe 隣の facility ディレクトリ（エクスポート先＝インポート一覧の親）。配下を facility\{ワールド名}\ で分ける。
        public static string BaseDir()
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "facility");
        }

        // facility\{ワールド名}\ のフルパス（空なら unknown フォルダ）。
        public static string WorldDir(string worldName)
            => Path.Combine(BaseDir(), NpcPortability.SafeFileName(string.IsNullOrWhiteSpace(worldName) ? NpcPortability.UnknownWorld : worldName));

        // facility\{ワールド名}\ 配下の重複しないエクスポート先パスを返す（name.zip, name(2).zip, ...）。
        public static string FreeExportPath(string name, string worldName)
        {
            string dir = WorldDir(worldName);
            Directory.CreateDirectory(dir);
            string safe = NpcPortability.SafeFileName(name);
            string path = Path.Combine(dir, safe + ".zip");
            for (int i = 2; File.Exists(path) && i < 1000; i++)
                path = Path.Combine(dir, $"{safe}({i}).zip");
            return path;
        }

        // facility\ 直下の、zip を1つ以上含むワールドフォルダ名を列挙する（名前順）。
        public static List<string> ListWorlds()
        {
            var list = new List<string>();
            string dir = BaseDir();
            if (!Directory.Exists(dir)) return list;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                try { if (Directory.EnumerateFiles(sub, "*.zip").Any()) list.Add(Path.GetFileName(sub)); }
                catch { /* 走査失敗フォルダは飛ばす */ }
            return list.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // facility\{ワールド名}\ 配下の全 zip の概要を列挙する（壊れた zip は飛ばす。名前順）。
        public static List<FacilityPackageInfo> ListLibrary(string worldName)
        {
            var list = new List<FacilityPackageInfo>();
            string dir = WorldDir(worldName);
            if (!Directory.Exists(dir)) return list;
            foreach (var zipPath in Directory.EnumerateFiles(dir, "*.zip"))
                try { list.Add(ReadInfo(zipPath)); } catch { /* 壊れた zip は無視 */ }
            return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // zip のメタ情報＋背景画像だけを読む（一覧表示用。facility 本体は読み込まない）。
        public static FacilityPackageInfo ReadInfo(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var wrapper = ReadWrapper(zip);
            var fac = J.Obj(wrapper, "facility");
            return new FacilityPackageInfo
            {
                Path = zipPath,
                Name = J.Str(wrapper, "original_name", fac != null ? J.Str(fac, "name") : ""),
                SourceWorld = J.Str(wrapper, "source_world"),
                Type = fac != null ? J.Str(fac, "facility_type") : "",
                Image = ReadImage(zip),
            };
        }

        // facility.json を読んで形式検証済みのラッパーを返す。
        private static JsonObject ReadWrapper(ZipArchive zip)
        {
            var jsonEntry = zip.GetEntry("facility.json")
                ?? throw new InvalidDataException(I18n.T("facpkg.err.noJson"));
            JsonObject wrapper;
            using (var r = new StreamReader(jsonEntry.Open(), Encoding.UTF8))
                wrapper = JsonNode.Parse(r.ReadToEnd())?.AsObject();
            if (wrapper == null || J.Str(wrapper, "format") != Format)
                throw new InvalidDataException(I18n.T("facpkg.err.badFormat"));
            return wrapper;
        }

        // images/image.png を読み出す（無ければ null）。
        private static byte[] ReadImage(ZipArchive zip)
        {
            var e = zip.GetEntry("images/image.png");
            if (e == null) return null;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        // ---------------- エクスポート ----------------
        // facility 本体 + backgrounds/{名前}/image.png を destZip に書き出す。
        public static void Export(JsonObject fac, string worldDir, string sourceWorld, string originalId, string destZip)
        {
            string name = J.Str(fac, "name");

            var clone = (JsonObject)fac.DeepClone();
            // connections は移植先で無効な ID になるため空にする（インポート時の配置場所から張り直す）。
            clone["connections"] = new JsonArray();
            // owner(NPC ID) も移植先では別の NPC を指してしまうため外す。
            if (clone.ContainsKey("owner")) clone["owner"] = null;

            var wrapper = new JsonObject
            {
                ["format"] = Format,
                ["version"] = 1,
                ["source_world"] = sourceWorld ?? "",
                ["original_id"] = originalId ?? "",
                ["original_name"] = name,
                ["facility"] = clone,
            };

            if (File.Exists(destZip)) File.Delete(destZip);
            using var zip = ZipFile.Open(destZip, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("facility.json");
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(wrapper.ToJsonString(Codec.Pretty));

            string bg = (worldDir != null && name.Length > 0)
                ? Path.Combine(worldDir, "backgrounds", name, "image.png") : null;
            if (bg != null && File.Exists(bg))
                zip.CreateEntryFromFile(bg, "images/image.png");
        }

        // ---------------- インポート(読込) ----------------
        // zip を解析して FacilityPackage を返す。形式が不正なら例外。
        public static FacilityPackage ReadPackage(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var wrapper = ReadWrapper(zip);
            if (wrapper["facility"] is not JsonObject fac)
                throw new InvalidDataException(I18n.T("facpkg.err.noData"));
            return new FacilityPackage
            {
                Facility = (JsonObject)fac.DeepClone(),
                OriginalName = J.Str(wrapper, "original_name", J.Str(fac, "name")),
                SourceWorld = J.Str(wrapper, "source_world"),
                Image = ReadImage(zip),
            };
        }

        // area 配下（全ノード）の facilities の数値キー最大+1 を新しい施設 ID として返す。
        // connections はノードを跨いで同一エリア内の施設を参照するため、ノード単位ではなくエリア全体で採番する。
        public static string NextFacilityId(JsonObject area)
        {
            int max = -1;
            if (J.Obj(area, "nodes") is JsonObject nodes)
                foreach (var nk in nodes)
                    if (J.Obj(nk.Value as JsonObject, "facilities") is JsonObject facs)
                        foreach (var kv in facs)
                            if (int.TryParse(kv.Key, out int n) && n > max) max = n;
            return (max + 1).ToString();
        }
    }

    // ---------------- インポート設定ダイアログ ----------------
    // 上部のプルダウンで facility\ 配下のワールドを選び、そのワールドの施設一覧から1件を選んで、
    // 配置先(ダンジョン以外の area)・接続先施設・名前を指定し、OK で world へ挿入する。
    // connections はエクスポート時に保持していないため、ここで選んだ接続先と双方向に張る。
    internal sealed class FacilityImportDialog : Form
    {
        private readonly string _worldDir;
        private readonly List<string> _worlds;              // facility\ 配下のワールド名一覧
        private List<FacilityPackageInfo> _infos = new();   // 選択中ワールドの施設一覧
        private FacilityPackage _pkg;                        // 選択中パッケージ（未選択なら null）
        private readonly JsonObject _areas;

        private readonly ComboBox _cbWorld = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ListBox _lstPackages = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        private PictureBox _pbImage;                         // プレビュー背景画像
        private TextBox _tbPreview;                          // プレビュー本文

        private readonly ComboBox _cbArea = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _cbConnect = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox _tbName = new() { Dock = DockStyle.Fill };

        public string ResultSummary { get; private set; }
        // 取り込んだ施設の位置（ワールドタブがツリーで新規ノードを選択するのに使う）。
        public string ImportedAreaId { get; private set; }
        public string ImportedNodeId { get; private set; }
        public string ImportedId { get; private set; }

        // presetAreaId / presetConnectId を指定すると配置エリア・接続先施設をその値で初期化する
        //（ワールドタブのツリー右クリックから、右クリックした area/施設を引き継ぐ用）。
        public FacilityImportDialog(JsonObject root, string worldDir, List<string> worlds,
            string presetAreaId = null, string presetConnectId = null)
        {
            _worldDir = worldDir; _worlds = worlds ?? new List<string>();
            _areas = J.Obj(root, "areas");

            Text = I18n.T("facimport.title");
            Width = 860; Height = 560; StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false; MaximizeBox = false;

            // 左：一覧 / 右：プレビュー＋設定＋ボタン
            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10) };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.Controls.Add(BuildList(), 0, 0);

            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8, 0, 0, 0) };
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));   // プレビュー
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));    // 設定
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));    // ボタン
            right.Controls.Add(BuildPreview(), 0, 0);
            right.Controls.Add(BuildSettings(), 0, 1);
            right.Controls.Add(BuildButtons(), 0, 2);
            main.Controls.Add(right, 1, 0);
            Controls.Add(main);

            // 非ダンジョン area を列挙（ダンジョンは connections を使わないため対象外）。
            if (_areas != null)
                foreach (var kv in _areas.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                    if (kv.Value is JsonObject a && J.Str(a, "size") != "dungeon")
                        _cbArea.Items.Add($"{kv.Key}: {J.Str(a, "name", kv.Key)}");
            _cbArea.SelectedIndexChanged += (_, _) => RefillConnect();
            int presetIdx = AreaComboHelper.FindIndexById(_cbArea, presetAreaId);
            if (_cbArea.Items.Count > 0) _cbArea.SelectedIndex = presetIdx >= 0 ? presetIdx : 0;
            // 接続先の事前選択はエリア選択（RefillConnect）後に行う。
            int connectIdx = AreaComboHelper.FindIndexById(_cbConnect, presetConnectId);
            if (connectIdx >= 0) _cbConnect.SelectedIndex = connectIdx;

            // 一覧の選択ハンドラを先に張る（ワールド選択→一覧再構築で発火するため）。
            _lstPackages.SelectedIndexChanged += (_, _) => OnSelectPackage();

            // ワールド一覧を載せ、前回のワールド（無ければ先頭）を選ぶ。
            foreach (var w in _worlds) _cbWorld.Items.Add(w);
            _cbWorld.SelectedIndexChanged += (_, _) => ReloadList();
            int initial = 0;
            string last = Settings.Current?.LastImportFacilityWorld;
            if (!string.IsNullOrEmpty(last))
            {
                int idx = _worlds.FindIndex(w => string.Equals(w, last, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) initial = idx;   // 見つからなければ先頭(0)
            }
            if (_cbWorld.Items.Count > 0) _cbWorld.SelectedIndex = initial; else ReloadList();
        }

        // 選択中ワールドの施設一覧を読み直して反映する（先頭を選択）。
        private void ReloadList()
        {
            string world = _cbWorld.SelectedItem as string;
            _infos = world != null ? FacilityPortability.ListLibrary(world) : new List<FacilityPackageInfo>();
            _lstPackages.Items.Clear();
            foreach (var info in _infos) _lstPackages.Items.Add(info.Name);
            if (_lstPackages.Items.Count > 0) _lstPackages.SelectedIndex = 0; else OnSelectPackage();
        }

        // ---- 一覧（ワールド選択＋施設一覧） ----
        private Control BuildList()
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));   // ワールドラベル
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // ワールドプルダウン
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // 一覧ヘッダ
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 施設一覧
            p.Controls.Add(new Label { Text = I18n.T("facimport.world"), AutoSize = true, Padding = new Padding(2, 2, 0, 0) }, 0, 0);
            p.Controls.Add(_cbWorld, 0, 1);
            p.Controls.Add(new Label { Text = I18n.T("facimport.listHeader"), AutoSize = true, Padding = new Padding(2, 2, 0, 2) }, 0, 2);
            p.Controls.Add(_lstPackages, 0, 3);
            return p;
        }

        // 終了時：開いていたワールドを settings.json に保存する（OK/キャンセル問わず）。
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                if (Settings.Current != null && _cbWorld.SelectedItem is string w)
                {
                    Settings.Current.LastImportFacilityWorld = w;
                    Settings.Save(Settings.Current);
                }
            }
            catch { /* 設定保存失敗は致命的にしない */ }
            base.OnFormClosed(e);
        }

        // ---- プレビュー（背景画像＋テキスト要約） ----
        private Control BuildPreview()
        {
            var p = new Panel { Dock = DockStyle.Fill };
            _pbImage = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom, Width = 260, Height = 130,
                Dock = DockStyle.Left, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(230, 230, 230),
            };
            _tbPreview = new TextBox
            {
                Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
                ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = SystemColors.Window,
            };
            // Dock.Fill は残り領域を占めるよう Dock.Left より先に追加する（後だと画像の裏に回り先頭行が隠れる）。
            p.Controls.Add(_tbPreview);
            p.Controls.Add(_pbImage);
            return p;
        }

        // 選択中パッケージの内容をプレビューへ反映する（未選択なら空表示）。
        private void RefreshPreview()
        {
            var old = _pbImage.Image;
            _pbImage.Image = null;
            old?.Dispose();
            if (_pkg?.Image != null)
                try { _pbImage.Image = Image.FromStream(new MemoryStream(_pkg.Image)); } catch { }

            var sb = new StringBuilder();
            if (_pkg != null)
            {
                sb.AppendLine("■ " + J.Str(_pkg.Facility, "name"));
                if (_pkg.SourceWorld.Length > 0) sb.AppendLine(I18n.T("facimport.sourcePrefix") + _pkg.SourceWorld);
                string type = J.Str(_pkg.Facility, "facility_type");
                if (type.Length > 0) sb.AppendLine(I18n.T("facimport.typePrefix") + type);
                sb.AppendLine();
                sb.Append(J.Str(_pkg.Facility, "description"));
            }
            _tbPreview.Text = sb.ToString();
        }

        // 一覧の選択が変わったとき：パッケージ本体を読み込み、プレビューと名前欄を更新する。
        private void OnSelectPackage()
        {
            int i = _lstPackages.SelectedIndex;
            _pkg = null;
            if (i >= 0 && i < _infos.Count)
            {
                try { _pkg = FacilityPortability.ReadPackage(_infos[i].Path); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, I18n.T("msg.loadFailed") + "\n\n" + ex.Message,
                        I18n.T("title.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            RefreshPreview();
            _tbName.Text = _pkg?.OriginalName ?? "";
        }

        // ---- 配置設定 ----
        private Control BuildSettings()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;

            t.Controls.Add(Lbl(I18n.T("facimport.area")), 0, r); t.Controls.Add(_cbArea, 1, r++);
            t.Controls.Add(Lbl(I18n.T("facimport.connect")), 0, r); t.Controls.Add(_cbConnect, 1, r++);
            t.Controls.Add(Lbl(I18n.T("facimport.name")), 0, r); t.Controls.Add(_tbName, 1, r++);

            if (_worldDir == null)
            {
                t.Controls.Add(new Label
                {
                    Text = I18n.T("facimport.noWorldDir"),
                    ForeColor = Color.Firebrick, AutoSize = true, Padding = new Padding(2, 6, 0, 0),
                }, 1, r++);
            }
            return t;
        }

        private Control BuildButtons()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            var ok = new Button { Text = I18n.T("facimport.import"), Width = 110 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Width = 110, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => Create();
            bar.Controls.AddRange(new Control[] { ok, cancel });
            CancelButton = cancel;
            return bar;
        }

        // 選択エリアの接続先候補（全ノードの全施設。入口/区画などの構造施設も接続先になるため除外しない）を作り直す。
        private void RefillConnect()
        {
            _cbConnect.Items.Clear();
            string aid = AreaComboHelper.ExtractId(_cbArea.Text);
            foreach (var (fid, fo) in EnumAreaFacilities(aid))
                _cbConnect.Items.Add($"{fid}: {J.Str(fo, "name", fid)}");
            if (_cbConnect.Items.Count > 0) _cbConnect.SelectedIndex = 0; else _cbConnect.Text = "";
        }

        // 指定エリア配下の全ノードの facility を (ID, オブジェクト) で列挙する。
        private IEnumerable<(string id, JsonObject fo)> EnumAreaFacilities(string areaId)
        {
            if (string.IsNullOrEmpty(areaId) || J.Obj(_areas?[areaId] as JsonObject, "nodes") is not JsonObject nodes) yield break;
            foreach (var nk in nodes)
                if (J.Obj(nk.Value as JsonObject, "facilities") is JsonObject facs)
                    foreach (var fk in facs.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                        if (fk.Value is JsonObject fo) yield return (fk.Key, fo);
        }

        // 入力を検証し、facility を world へ挿入する。
        private void Create()
        {
            if (_pkg == null)
            { MessageBox.Show(this, I18n.T("facimport.errSelectPackage")); return; }
            if (_areas == null)
            { MessageBox.Show(this, I18n.T("facimport.errNoContainers")); return; }
            string areaId = AreaComboHelper.ExtractId(_cbArea.Text);
            if (string.IsNullOrEmpty(areaId) || _areas[areaId] is not JsonObject area)
            { MessageBox.Show(this, I18n.T("facimport.errSelectArea")); return; }
            string name = _tbName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            { MessageBox.Show(this, I18n.T("facimport.errName")); return; }

            // 接続先施設と、それが属するノードの facilities 辞書を特定する（新施設は同じノードへ入れる）。
            string targetFid = AreaComboHelper.ExtractId(_cbConnect.Text);
            JsonObject facilities = null, target = null;
            string nodeKey = null;
            if (!string.IsNullOrEmpty(targetFid) && J.Obj(area, "nodes") is JsonObject nodes)
                foreach (var nk in nodes)
                    if (J.Obj(nk.Value as JsonObject, "facilities") is JsonObject f && f[targetFid] is JsonObject t)
                    { facilities = f; target = t; nodeKey = nk.Key; break; }
            if (facilities == null)
            { MessageBox.Show(this, I18n.T("facimport.errSelectConnect")); return; }

            // 取込施設の組み立て。connections はエクスポートで空にしているため、接続先と双方向に張る。
            var fac = _pkg.Facility;
            string newFid = FacilityPortability.NextFacilityId(area);
            fac["id"] = newFid;
            fac["name"] = name;
            fac["connections"] = new JsonArray { targetFid };
            if (target["connections"] is not JsonArray tconns) target["connections"] = tconns = new JsonArray();
            if (!tconns.Any(n => (n?.ToString() ?? "") == newFid)) tconns.Add(newFid);
            facilities[newFid] = fac;

            // 背景画像を backgrounds/{名前}/image.png へ展開する（同名の既存画像は残す）。
            if (_pkg.Image != null && _worldDir != null)
            {
                try
                {
                    string dir = Path.Combine(_worldDir, "backgrounds", name);
                    string path = Path.Combine(dir, "image.png");
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllBytes(path, _pkg.Image);
                    }
                }
                catch (Exception ex)
                { MessageBox.Show(this, I18n.T("facimport.errImageExtract") + "\n" + ex.Message); }
            }

            ImportedAreaId = areaId; ImportedNodeId = nodeKey; ImportedId = newFid;
            ResultSummary = I18n.T("facimport.summary",
                newFid, name, areaId, J.Str(area, "name", areaId), targetFid, J.Str(target, "name", targetFid));
            DialogResult = DialogResult.OK;
            Close();
        }

        private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Padding = new Padding(2, 6, 4, 0) };
    }
}
