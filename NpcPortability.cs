// NPC のエクスポート/インポート。
// エクスポート: NPC レコード(JSON)＋ characters/{名前}/ の画像を 1 つの zip にまとめる。
// インポート: zip から取り込み、配置先(ダンジョン以外の area + facility)・登録先(住人/冒険者)を
// 指定して world へ挿入する。画像は対象ワールドの characters/ へ展開し、image_src を貼り替える。
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // zip から読み出した NPC パッケージ。
    internal sealed class NpcPackage
    {
        public JsonObject Npc;                              // NPC 本体（クローン済み）
        public string OriginalName = "";                   // 出力時の名前（画像フォルダ名）
        public string SourceWorld = "";                    // 出典ワールド名
        public Dictionary<string, byte[]> Images = new();  // 画像ファイル名 → 中身
    }

    // npc\ ライブラリ一覧の表示用メタ情報（NPC 本体は読まず、名前・出典・顔画像のみ）。
    internal sealed class NpcPackageInfo
    {
        public string Path = "";          // zip のフルパス
        public string Name = "";          // 表示名
        public string SourceWorld = "";   // 出典ワールド名
        public string Job = "";           // 職業
        public byte[] FaceImage;          // 顔画像（あれば）
    }

    // NPC 移植（エクスポート/インポート）の入出力ロジック。UI は NpcImportDialog 側。
    internal static class NpcPortability
    {
        public const string Format = "instantale_npc";

        // ---------------- npc\ ライブラリ ----------------
        // ワールド名が不明なときに使うフォルダ名。
        public const string UnknownWorld = "unknown";

        // ワールド毎に分けない設定のときの集約フォルダ名。
        public const string AllWorld = "ALL";

        // エクスポート先のフォルダ名を設定に従って決める（ワールド毎=source / まとめる=ALL）。
        public static string ExportWorldName(string source)
            => (Settings.Current?.NpcExportPerWorld ?? true) ? source : AllWorld;

        // exe 隣の npc ディレクトリ（エクスポート先＝インポート一覧の親）。配下を npc\{ワールド名}\ で分ける。
        // 単一ファイル発行では Assembly.Location が空になるため ProcessPath を使う。
        public static string BaseDir()
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "npc");
        }

        // npc\{ワールド名}\ のフルパス（空なら unknown フォルダ）。
        public static string WorldDir(string worldName)
            => Path.Combine(BaseDir(), SafeFileName(string.IsNullOrWhiteSpace(worldName) ? UnknownWorld : worldName));

        // npc\{ワールド名}\ 配下の重複しないエクスポート先パスを返す（name.zip, name(2).zip, ...）。
        public static string FreeExportPath(string name, string worldName)
        {
            string dir = WorldDir(worldName);
            Directory.CreateDirectory(dir);
            string safe = SafeFileName(name);
            string path = Path.Combine(dir, safe + ".zip");
            for (int i = 2; File.Exists(path) && i < 1000; i++)
                path = Path.Combine(dir, $"{safe}({i}).zip");
            return path;
        }

        // npc\ 直下の、zip を1つ以上含むワールドフォルダ名を列挙する（名前順）。
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

        // npc\{ワールド名}\ 配下の全 zip の概要を列挙する（壊れた zip は飛ばす。名前順）。
        public static List<NpcPackageInfo> ListLibrary(string worldName)
        {
            var list = new List<NpcPackageInfo>();
            string dir = WorldDir(worldName);
            if (!Directory.Exists(dir)) return list;
            foreach (var zipPath in Directory.EnumerateFiles(dir, "*.zip"))
                try { list.Add(ReadInfo(zipPath)); } catch { /* 壊れた zip は無視 */ }
            return list.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // zip のメタ情報＋顔画像だけを読む（一覧表示用。NPC 本体は読み込まない）。
        public static NpcPackageInfo ReadInfo(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var jsonEntry = zip.GetEntry("npc.json")
                ?? throw new InvalidDataException(I18n.T("npcpkg.err.noJson"));
            JsonObject wrapper;
            using (var r = new StreamReader(jsonEntry.Open(), Encoding.UTF8))
                wrapper = JsonNode.Parse(r.ReadToEnd())?.AsObject();
            if (wrapper == null || J.Str(wrapper, "format") != Format)
                throw new InvalidDataException(I18n.T("npcpkg.err.badFormat"));
            var npc = J.Obj(wrapper, "npc");
            var info = new NpcPackageInfo
            {
                Path = zipPath,
                Name = J.Str(wrapper, "original_name", npc != null ? J.Str(npc, "name") : ""),
                SourceWorld = J.Str(wrapper, "source_world"),
                Job = npc != null ? J.Str(npc, "job") : "",
            };
            var face = zip.GetEntry("images/face_image.png");
            if (face != null)
            {
                using var s = face.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                info.FaceImage = ms.ToArray();
            }
            return info;
        }

        // ファイル名に使えない文字を '_' に置き換える。
        public static string SafeFileName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrWhiteSpace(s) ? "npc" : s;
        }

        // ---------------- エクスポート ----------------
        // NPC 本体 + characters/{名前}/*.png を destZip に書き出す。
        public static void Export(JsonObject npc, string worldDir, string sourceWorld, string originalId, string destZip)
        {
            string name = J.Str(npc, "name");
            string charDir = (worldDir != null && name.Length > 0)
                ? Path.Combine(worldDir, "characters", name) : null;

            var clone = (JsonObject)npc.DeepClone();
            SanitizeForExport(clone);

            var wrapper = new JsonObject
            {
                ["format"] = Format,
                ["version"] = 1,
                ["source_world"] = sourceWorld ?? "",
                ["original_id"] = originalId ?? "",
                ["original_name"] = name,
                ["npc"] = clone,
            };

            if (File.Exists(destZip)) File.Delete(destZip);
            using var zip = ZipFile.Open(destZip, ZipArchiveMode.Create);
            var entry = zip.CreateEntry("npc.json");
            using (var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                w.Write(wrapper.ToJsonString(Codec.Pretty));

            if (charDir != null && Directory.Exists(charDir))
                foreach (var file in Directory.EnumerateFiles(charDir, "*.png"))
                    zip.CreateEntryFromFile(file, "images/" + Path.GetFileName(file));
        }

        // エクスポート用にNPCを加工する（取込先で持ち越したくない固有状態を初期化する）。
        // relationship / life_log は保持したまま出力し、引き継ぐかはインポート時に選ぶ。
        // display_position_in_battle のみ null にする。
        private static void SanitizeForExport(JsonObject npc)
        {
            if (npc.ContainsKey("display_position_in_battle")) npc["display_position_in_battle"] = null;
        }

        // relationship を引き継がない場合に使う既定値（player のみ・好感度0）。
        public static JsonObject DefaultRelationship() => new JsonObject
        {
            ["player"] = new JsonObject
            {
                ["affinity"] = 0,
                ["affinity_text"] = new JsonArray { "警戒している" },
                ["relationship"] = new JsonArray(),
                ["conversation_count"] = 0,
            },
        };

        // ---------------- インポート(読込) ----------------
        // zip を解析して NpcPackage を返す。形式が不正なら例外。
        public static NpcPackage ReadPackage(string zipPath)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var jsonEntry = zip.GetEntry("npc.json")
                ?? throw new InvalidDataException(I18n.T("npcpkg.err.noJson"));
            JsonObject wrapper;
            using (var r = new StreamReader(jsonEntry.Open(), Encoding.UTF8))
                wrapper = JsonNode.Parse(r.ReadToEnd())?.AsObject();
            if (wrapper == null || J.Str(wrapper, "format") != Format)
                throw new InvalidDataException(I18n.T("npcpkg.err.badFormat"));
            if (wrapper["npc"] is not JsonObject npc)
                throw new InvalidDataException(I18n.T("npcpkg.err.noNpcData"));

            var pkg = new NpcPackage
            {
                Npc = (JsonObject)npc.DeepClone(),
                OriginalName = J.Str(wrapper, "original_name", J.Str(npc, "name")),
                SourceWorld = J.Str(wrapper, "source_world"),
            };
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("images/") || e.Name.Length == 0) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                pkg.Images[e.Name] = ms.ToArray();
            }
            return pkg;
        }

        // 画像を対象キャラフォルダへ書き出し、image_src を貼り替える。charDir が null なら image_src を消す。
        public static void PlaceImages(JsonObject npc, string charDir, Dictionary<string, byte[]> images)
        {
            if (charDir != null)
            {
                Directory.CreateDirectory(charDir);
                foreach (var kv in images)
                    File.WriteAllBytes(Path.Combine(charDir, kv.Key), kv.Value);
            }
            RewriteImageSrc(npc, charDir);
        }

        // image_src の各パス(文字列値)を charDir/{元ファイル名} へ貼り替える。charDir が null なら null にする。
        public static void RewriteImageSrc(JsonObject npc, string charDir)
        {
            if (J.Obj(npc, "image_src") is not JsonObject src) return;
            foreach (var key in src.Select(p => p.Key).ToList())
                if (src[key] is JsonValue v && v.TryGetValue<string>(out var path) && !string.IsNullOrEmpty(path))
                    src[key] = charDir != null ? Path.Combine(charDir, Path.GetFileName(path)) : null;
        }

        // index.npc から未使用の NPC ID を採番する(既存キーは飛ばす)。
        public static string NextNpcId(JsonObject index, JsonObject npcs)
        {
            int cur = (int)J.Int(index, "npc", 0);
            while (npcs.ContainsKey(cur.ToString())) cur++;
            index["npc"] = cur + 1;
            return cur.ToString();
        }

        // 既存 NPC をリネームする(name 変更・画像フォルダ改名・image_src 貼り替え)。
        public static void RenameNpcOnDisk(JsonObject npc, string worldDir, string newName)
        {
            string oldName = J.Str(npc, "name");
            npc["name"] = newName;
            if (worldDir == null) return;
            string baseDir = Path.Combine(worldDir, "characters");
            string oldDir = Path.Combine(baseDir, oldName);
            string newDir = Path.Combine(baseDir, newName);
            try
            {
                if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                    Directory.Move(oldDir, newDir);
            }
            catch { }
            RewriteImageSrc(npc, newDir);
        }

        // 対象ワールドに同名 NPC が存在するか(npcs の name 一致のみで判定)。
        // 画像フォルダは NPC が居なければ参照されないため、フォルダの有無は判定に含めない。
        public static bool NameExists(JsonObject npcs, string name, string exceptId = null)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var kv in npcs)
                if (kv.Key != exceptId && kv.Value is JsonObject o && J.Str(o, "name") == name)
                    return true;
            return false;
        }
    }

    // ---------------- インポート設定ダイアログ ----------------
    // 上部のプルダウンで npc\ 配下のワールドを選び、そのワールドの NPC 一覧から1体を選んで、
    // 配置先(ダンジョン以外の area + facility)・登録先(住人/冒険者)・名前重複の解決を指定し、
    // OK で world へ挿入する。開いていたワールドは settings.json に保存し次回復元する。
    internal sealed class NpcImportDialog : Form
    {
        private readonly JsonObject _root;
        private readonly string _worldDir;
        private readonly List<string> _worlds;          // npc\ 配下のワールド名一覧
        private List<NpcPackageInfo> _infos = new();    // 選択中ワールドの NPC 一覧
        private NpcPackage _pkg;                         // 選択中パッケージ（未選択なら null）
        private readonly JsonObject _areas, _npcs, _index;

        private readonly ComboBox _cbWorld = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ListBox _lstPackages = new() { Dock = DockStyle.Fill, IntegralHeight = false };
        private PictureBox _pbFace;                      // プレビュー顔画像
        private TextBox _tbPreview;                      // プレビュー本文

        private readonly ComboBox _cbArea = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly ComboBox _cbFac = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly CheckBox _chkAdventurer = new() { Text = I18n.T("npcimport.adventurer"), AutoSize = true, Checked = true };
        private readonly CheckBox _chkRelationship = new() { Text = I18n.T("npcimport.inheritRel"), AutoSize = true, Checked = true };
        private readonly CheckBox _chkLifeLog = new() { Text = I18n.T("npcimport.inheritLog"), AutoSize = true, Checked = true };

        private readonly GroupBox _grpCollision = new() { Text = I18n.T("npcimport.collision"), Dock = DockStyle.Fill, Visible = false, Height = 160 };
        private readonly RadioButton _rbRenameNew = new() { Text = I18n.T("npcimport.renameNew"), AutoSize = true, Checked = true };
        private readonly RadioButton _rbRenameExisting = new() { Text = I18n.T("npcimport.renameExisting"), AutoSize = true };
        private readonly RadioButton _rbOverwrite = new() { Text = I18n.T("npcimport.overwrite"), AutoSize = true };
        private readonly TextBox _tbName = new();          // 取込NPCの名前
        private readonly TextBox _tbExistingNew = new();   // 既存NPCの新しい名前
        private readonly Label _lblOverwrite = new() { ForeColor = Color.DimGray, AutoSize = true, Padding = new Padding(2, 4, 0, 0) };
        private readonly Label _lblWarn = new() { ForeColor = Color.Firebrick, AutoSize = false, Dock = DockStyle.Fill };

        public string ResultSummary { get; private set; }

        public NpcImportDialog(JsonObject root, string worldDir, List<string> worlds)
        {
            _root = root; _worldDir = worldDir; _worlds = worlds ?? new List<string>();
            _areas = J.Obj(_root, "areas"); _npcs = J.Obj(_root, "npcs"); _index = J.Obj(_root, "index");

            Text = I18n.T("npcimport.title");
            Width = 860; Height = 600; StartPosition = FormStartPosition.CenterParent;
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

            // 非ダンジョン area を列挙
            if (_areas != null)
                foreach (var kv in _areas.OrderBy(p => p.Key.Length).ThenBy(p => p.Key))
                    if (kv.Value is JsonObject a && J.Str(a, "size") != "dungeon")
                        _cbArea.Items.Add($"{kv.Key}: {J.Str(a, "name", kv.Key)}");
            _cbArea.SelectedIndexChanged += (_, _) => RefillFacilities();
            if (_cbArea.Items.Count > 0) _cbArea.SelectedIndex = 0;

            _rbRenameNew.CheckedChanged += (_, _) => UpdateCollisionUi();
            _rbRenameExisting.CheckedChanged += (_, _) => UpdateCollisionUi();
            _rbOverwrite.CheckedChanged += (_, _) => UpdateCollisionUi();

            // 一覧の選択ハンドラを先に張る（ワールド選択→一覧再構築で発火するため）。
            _lstPackages.SelectedIndexChanged += (_, _) => OnSelectPackage();

            // ワールド一覧を載せ、前回のワールド（無ければ先頭）を選ぶ。
            foreach (var w in _worlds) _cbWorld.Items.Add(w);
            _cbWorld.SelectedIndexChanged += (_, _) => ReloadList();
            int initial = 0;
            string last = Settings.Current?.LastImportNpcWorld;
            if (!string.IsNullOrEmpty(last))
            {
                int idx = _worlds.FindIndex(w => string.Equals(w, last, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) initial = idx;   // 見つからなければ先頭(0)
            }
            if (_cbWorld.Items.Count > 0) _cbWorld.SelectedIndex = initial; else ReloadList();
        }

        // 一覧行の表示文字列（名前）。ワールドは上のプルダウンで選ぶため名前のみ。
        private static string FormatListItem(NpcPackageInfo info) => info.Name;

        // 選択中ワールドの NPC 一覧を読み直して反映する（先頭を選択）。
        private void ReloadList()
        {
            string world = _cbWorld.SelectedItem as string;
            _infos = world != null ? NpcPortability.ListLibrary(world) : new List<NpcPackageInfo>();
            _lstPackages.Items.Clear();
            foreach (var info in _infos) _lstPackages.Items.Add(FormatListItem(info));
            if (_lstPackages.Items.Count > 0) _lstPackages.SelectedIndex = 0; else OnSelectPackage();
        }

        // ---- 一覧（ワールド選択＋NPC一覧） ----
        private Control BuildList()
        {
            var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));   // ワールドラベル
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));   // ワールドプルダウン
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));   // 一覧ヘッダ
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // NPC一覧
            p.Controls.Add(new Label { Text = I18n.T("npcimport.world"), AutoSize = true, Padding = new Padding(2, 2, 0, 0) }, 0, 0);
            p.Controls.Add(_cbWorld, 0, 1);
            p.Controls.Add(new Label { Text = I18n.T("npcimport.listHeader"), AutoSize = true, Padding = new Padding(2, 2, 0, 2) }, 0, 2);
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
                    Settings.Current.LastImportNpcWorld = w;
                    Settings.Save(Settings.Current);
                }
            }
            catch { /* 設定保存失敗は致命的にしない */ }
            base.OnFormClosed(e);
        }

        // ---- プレビュー（顔画像＋テキスト要約） ----
        private Control BuildPreview()
        {
            var p = new Panel { Dock = DockStyle.Fill };
            _pbFace = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom, Width = 100, Height = 130,
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
            p.Controls.Add(_pbFace);
            return p;
        }

        // 選択中パッケージの内容をプレビューへ反映する（未選択なら空表示）。
        private void RefreshPreview()
        {
            var old = _pbFace.Image;
            _pbFace.Image = null;
            old?.Dispose();
            if (_pkg != null && _pkg.Images.TryGetValue("face_image.png", out var face))
                try { _pbFace.Image = Image.FromStream(new MemoryStream(face)); } catch { }

            var sb = new StringBuilder();
            if (_pkg != null)
            {
                sb.AppendLine("■ " + J.Str(_pkg.Npc, "name"));
                if (_pkg.SourceWorld.Length > 0) sb.AppendLine(I18n.T("npcimport.sourcePrefix") + _pkg.SourceWorld);
                string job = J.Str(_pkg.Npc, "job");
                if (job.Length > 0) sb.AppendLine(I18n.T("npcimport.jobPrefix") + job);
                sb.AppendLine();
                sb.Append(J.Str(_pkg.Npc, "look_description"));
            }
            _tbPreview.Text = sb.ToString();
        }

        // 一覧の選択が変わったとき：パッケージ本体を読み込み、プレビューと名前重複UIを更新する。
        private void OnSelectPackage()
        {
            int i = _lstPackages.SelectedIndex;
            _pkg = null;
            if (i >= 0 && i < _infos.Count)
            {
                try { _pkg = NpcPortability.ReadPackage(_infos[i].Path); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, I18n.T("msg.loadFailed") + "\n\n" + ex.Message,
                        I18n.T("title.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            RefreshPreview();
            InitCollisionForPackage();
        }

        // 選択中パッケージの名前重複を判定し、解決UIの初期状態を整える。
        private void InitCollisionForPackage()
        {
            if (_pkg == null) { _grpCollision.Visible = false; UpdateCollisionUi(); return; }
            bool collide = NpcPortability.NameExists(_npcs, _pkg.OriginalName);
            _grpCollision.Visible = collide;
            _rbRenameNew.Checked = true;
            _tbName.Text = collide ? SuggestFree(_pkg.OriginalName) : _pkg.OriginalName;
            if (collide)
                _tbExistingNew.Text = SuggestFree(_pkg.OriginalName + I18n.T("npcimport.oldSuffix"));
            UpdateCollisionUi();
        }

        // ---- 配置設定 ----
        private Control BuildSettings()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            int r = 0;

            t.Controls.Add(Lbl(I18n.T("npcimport.area")), 0, r); t.Controls.Add(_cbArea, 1, r++);
            t.Controls.Add(Lbl(I18n.T("npcimport.facility")), 0, r); t.Controls.Add(_cbFac, 1, r++);

            var regPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            regPanel.Controls.Add(_chkAdventurer);
            t.Controls.Add(Lbl(I18n.T("npcimport.register")), 0, r); t.Controls.Add(regPanel, 1, r++);

            var inheritPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown };
            inheritPanel.Controls.Add(_chkRelationship); inheritPanel.Controls.Add(_chkLifeLog);
            t.Controls.Add(Lbl(I18n.T("npcimport.inherit")), 0, r); t.Controls.Add(inheritPanel, 1, r++);

            BuildCollisionGroup();
            t.Controls.Add(_grpCollision, 0, r); t.SetColumnSpan(_grpCollision, 2); r++;

            if (_worldDir == null)
            {
                t.Controls.Add(new Label
                {
                    Text = I18n.T("npcimport.noWorldDir"),
                    ForeColor = Color.Firebrick, AutoSize = true, Padding = new Padding(2, 6, 0, 0),
                }, 1, r++);
            }
            return t;
        }

        private void BuildCollisionGroup()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(8, 4, 8, 4) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _tbName.Dock = DockStyle.Fill; _tbExistingNew.Dock = DockStyle.Fill;
            int r = 0;
            t.Controls.Add(_rbRenameNew, 0, r); t.Controls.Add(_tbName, 1, r++);
            t.Controls.Add(_rbRenameExisting, 0, r); t.Controls.Add(_tbExistingNew, 1, r++);
            t.Controls.Add(_rbOverwrite, 0, r); t.Controls.Add(_lblOverwrite, 1, r++);
            t.Controls.Add(_lblWarn, 0, r); t.SetColumnSpan(_lblWarn, 2);
            _grpCollision.Controls.Add(t);
        }

        // ラジオ状態に応じて入力欄の有効/無効と警告表示を更新する。
        private void UpdateCollisionUi()
        {
            if (_pkg == null) { _lblOverwrite.Text = ""; _lblWarn.Text = ""; return; }
            bool renameExisting = _rbRenameExisting.Checked;
            bool overwrite = _rbOverwrite.Checked;
            _tbName.Enabled = !renameExisting && !overwrite;
            _tbExistingNew.Enabled = renameExisting;
            if (renameExisting || overwrite) _tbName.Text = _pkg.OriginalName;
            int dup = _npcs == null ? 0
                : _npcs.Count(p => p.Value is JsonObject o && J.Str(o, "name") == _pkg.OriginalName);
            _lblOverwrite.Text = overwrite
                ? (dup > 1 ? I18n.T("npcimport.dupOverwrite", dup) : "")
                : "";
            _lblWarn.Text = renameExisting
                ? I18n.T("npcimport.warnRename")
                : overwrite
                ? I18n.T("npcimport.warnOverwrite")
                : "";
        }

        private Control BuildButtons()
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
            var ok = new Button { Text = I18n.T("npcimport.import"), Width = 110 };
            var cancel = new Button { Text = I18n.T("btn.cancel"), Width = 110, DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => Create();
            bar.Controls.AddRange(new Control[] { ok, cancel });
            CancelButton = cancel;
            return bar;
        }

        // 選択エリアの施設一覧を作り直す。
        private void RefillFacilities()
        {
            string aid = AreaComboHelper.ExtractId(_cbArea.Text);
            AreaComboHelper.FillFacilityItems(_cbFac, _areas, aid);
            if (_cbFac.Items.Count > 0) _cbFac.SelectedIndex = 0; else _cbFac.Text = "";
        }

        // 重複しない名前候補を返す（base, base(2), base(3)...）。
        private string SuggestFree(string baseName)
        {
            if (!NpcPortability.NameExists(_npcs, baseName)) return baseName;
            for (int i = 2; i < 100; i++)
            {
                string cand = $"{baseName}({i})";
                if (!NpcPortability.NameExists(_npcs, cand)) return cand;
            }
            return baseName + "(" + Guid.NewGuid().ToString("N")[..4] + ")";
        }

        // 入力を検証し、NPC を world へ挿入する。
        private void Create()
        {
            if (_pkg == null)
            { MessageBox.Show(this, I18n.T("npcimport.errSelectPackage")); return; }
            if (_areas == null || _npcs == null || _index == null)
            { MessageBox.Show(this, I18n.T("npcimport.errNoContainers")); return; }
            string areaId = AreaComboHelper.ExtractId(_cbArea.Text);
            if (string.IsNullOrEmpty(areaId) || _areas[areaId] is not JsonObject area)
            { MessageBox.Show(this, I18n.T("npcimport.errSelectArea")); return; }
            string facId = AreaComboHelper.ExtractId(_cbFac.Text);

            bool overwrite = _grpCollision.Visible && _rbOverwrite.Checked;
            bool renameExisting = _grpCollision.Visible && _rbRenameExisting.Checked;
            string incomingName, existingNewName = null, overwriteId = null;
            if (overwrite)
            {
                incomingName = _pkg.OriginalName;
                overwriteId = _npcs.FirstOrDefault(p => p.Value is JsonObject o && J.Str(o, "name") == incomingName).Key;
                if (overwriteId == null)
                { MessageBox.Show(this, I18n.T("npcimport.errOverwriteNotFound", incomingName)); return; }
            }
            else if (renameExisting)
            {
                incomingName = _pkg.OriginalName;
                existingNewName = _tbExistingNew.Text.Trim();
                if (string.IsNullOrEmpty(existingNewName))
                { MessageBox.Show(this, I18n.T("npcimport.errExistingName")); return; }
                if (NpcPortability.NameExists(_npcs, existingNewName))
                { MessageBox.Show(this, I18n.T("npcimport.errNameTaken2", existingNewName)); return; }
            }
            else
            {
                incomingName = _tbName.Text.Trim();
                if (string.IsNullOrEmpty(incomingName))
                { MessageBox.Show(this, I18n.T("npcimport.errIncomingName")); return; }
                if (NpcPortability.NameExists(_npcs, incomingName))
                { MessageBox.Show(this, I18n.T("npcimport.errNameTaken", incomingName)); return; }
            }

            // 既存NPCのリネーム（同名すべて）。フォルダ移動は最初の1件で済み、以降は image_src のみ更新。
            if (renameExisting)
                foreach (var kv in _npcs.Where(p => p.Value is JsonObject o && J.Str(o, "name") == _pkg.OriginalName).ToList())
                    NpcPortability.RenameNpcOnDisk(kv.Value.AsObject(), _worldDir, existingNewName);

            // 取込NPCの組み立て。上書き時は既存 id を再利用する。
            var npc = _pkg.Npc;
            string newId = overwrite ? overwriteId : NpcPortability.NextNpcId(_index, _npcs);
            npc["id"] = newId;
            npc["name"] = incomingName;
            npc["current_area"] = areaId;
            npc["current_location"] = facId;
            npc["initial_location"] = new JsonObject { ["area"] = areaId, ["node"] = null, ["facility"] = facId };
            npc["location"] = new JsonObject { ["area"] = null, ["node"] = null, ["facility"] = null };

            // relationship / life_log は取込データの値を保持（引き継ぐ）。チェックを外したものは既定値に戻す。
            if (!_chkRelationship.Checked) npc["relationship"] = NpcPortability.DefaultRelationship();
            if (!_chkLifeLog.Checked) npc["life_log"] = new JsonArray();

            // 上書き時は旧配置の登録から外す（id を再利用するため）。
            if (overwrite) RemoveFromAreaLists(overwriteId);

            string charDir = _worldDir != null ? Path.Combine(_worldDir, "characters", incomingName) : null;
            try { NpcPortability.PlaceImages(npc, charDir, _pkg.Images); }
            catch (Exception ex) { MessageBox.Show(this, I18n.T("npcimport.errImageExtract") + "\n" + ex.Message); return; }

            _npcs[newId] = npc;

            // 冒険者として登録する場合のみ adventurer_npcs へ id を追加
            if (_chkAdventurer.Checked)
            {
                var arr = J.Arr(area, "adventurer_npcs");
                if (arr == null) { arr = new JsonArray(); area["adventurer_npcs"] = arr; }
                if (!arr.Any(x => x?.ToString() == newId)) arr.Add(newId);
            }

            string role = _chkAdventurer.Checked ? I18n.T("npcimport.roleAdventurer") : I18n.T("npcimport.roleNone");
            ResultSummary = (overwrite ? I18n.T("npcimport.summary.overwritePrefix", newId, incomingName)
                                       : I18n.T("npcimport.summary.newPrefix", newId, incomingName))
                          + I18n.T("npcimport.summary.area", areaId, J.Str(area, "name", areaId))
                          + (string.IsNullOrEmpty(facId) ? "" : I18n.T("npcimport.summary.facility", facId))
                          + I18n.T("npcimport.summary.placed", role)
                          + (renameExisting ? I18n.T("npcimport.summary.renamed", existingNewName) : "");
            DialogResult = DialogResult.OK;
            Close();
        }

        // 全 area の resident_npcs / adventurer_npcs から指定 id を取り除く（上書き時の旧配置解除）。
        private void RemoveFromAreaLists(string id)
        {
            if (_areas == null) return;
            foreach (var kv in _areas)
            {
                if (kv.Value is not JsonObject a) continue;
                foreach (var key in new[] { "resident_npcs", "adventurer_npcs" })
                {
                    var arr = J.Arr(a, key);
                    if (arr == null) continue;
                    for (int i = arr.Count - 1; i >= 0; i--)
                        if (arr[i]?.ToString() == id) arr.RemoveAt(i);
                }
            }
        }

        private static Label Lbl(string s) => new() { Text = s, AutoSize = true, Padding = new Padding(2, 6, 4, 0) };
    }
}
