// ツール設定（settings.json）と自動バックアップ（BackupManager）。
// 設定は exe 隣の settings.json にポータブル保存する。既存の保存ロジック・各タブ挙動は変更しない。
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace InstantaleSaveEditor
{
    // ウィンドウサイズの扱い: 前回サイズを記憶する / 固定サイズで起動する。
    internal enum WindowSizeMode { RememberLast, Fixed }

    // アプリの設定。System.Text.Json で settings.json へ直列化する（既定値はプロパティ初期化子で定義）。
    internal sealed class Settings
    {
        // --- バックアップ ---
        public bool AutoBackupEnabled { get; set; } = true;          // 自動バックアップ ON/OFF
        public string BackupBaseFolderOverride { get; set; } = "";   // 空ならセーブと同じ場所の \backups\ を使う
        public bool AutoDeleteOldBackups { get; set; } = false;      // 既定オフ。蓄積し続ける
        public int BackupRetentionCount { get; set; } = 10;          // 自動削除が ON のときのみ有効

        // --- 表示 ---
        public string Language { get; set; } = "ja";                 // UI 言語コード（I18n が参照する）
        public WindowSizeMode WindowSizeMode { get; set; } = WindowSizeMode.RememberLast;
        public int FixedWindowWidth { get; set; } = 1040;            // 固定サイズ時の幅
        public int FixedWindowHeight { get; set; } = 760;            // 固定サイズ時の高さ
        public bool RememberWindowPosition { get; set; } = false;    // 起動位置も記憶するか

        // RememberLast 用に保存する直近のサイズ/位置（0/未設定なら既定値を使う）。
        public int SavedWindowWidth { get; set; } = 0;
        public int SavedWindowHeight { get; set; } = 0;
        public int SavedWindowX { get; set; } = 0;
        public int SavedWindowY { get; set; } = 0;

        // --- インベントリ（グリッド編集） ---
        public string GameAssetRoot { get; set; } = "";             // ゲーム導入先。image_src の相対パス解決基準
        public int InventoryGridColumns { get; set; } = 4;          // グリッド列数（固定値。セーブからは読まない）
        public int InventoryGridRows { get; set; } = 6;             // グリッド行数（実容量に合わせユーザーが変更）

        // --- NPC エクスポート ---
        public bool NpcExportPerWorld { get; set; } = true;         // true: npc\{ワールド名}\ へ / false: npc\ALL\ へ

        // --- その他 ---
        public string LastOpenedFolder { get; set; } = "";          // ファイルダイアログの初期位置
        public string LastImportNpcWorld { get; set; } = "";        // NPCインポート画面で最後に開いていたワールド
        public List<string> RecentFiles { get; set; } = new();     // 「最近開いたファイル」の履歴（新しい順）

        // 起動時に読み込んだ現在の設定インスタンス（コントロール等から参照する）。
        // SettingsForm は同一インスタンスを書き換えるため、設定変更も即ここへ反映される。
        [JsonIgnore]
        public static Settings Current { get; private set; } = new Settings();

        [JsonIgnore]
        private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        // settings.json のフルパス（exe と同じディレクトリ）。
        // 単一ファイル発行では Assembly.Location が空になるため Environment.ProcessPath を使う。
        public static string FilePath()
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
            return Path.Combine(dir, "settings.json");
        }

        // 設定を読み込む。ファイルが無い/壊れている場合は既定値で返す（例外は出さない）。
        public static Settings Load()
        {
            try
            {
                string p = FilePath();
                Current = !File.Exists(p)
                    ? new Settings()
                    : JsonSerializer.Deserialize<Settings>(File.ReadAllText(p, Encoding.UTF8), JsonOpts) ?? new Settings();
                return Current;
            }
            catch { return Current = new Settings(); }
        }

        // 設定を保存する。書込不可の場所では致命的にせず MessageBox 表示のみで継続する。
        public static void Save(Settings s)
        {
            try { File.WriteAllText(FilePath(), JsonSerializer.Serialize(s, JsonOpts), new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.settingsSaveFailed") + "\n\n" + ex.Message,
                    I18n.T("title.settingsSaveFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // 自動バックアップ。保存の「上書き直前」に呼び、上書き対象のディスク上ファイルを ZIP として退避する。
    internal static class BackupManager
    {
        // 上書き直前のバックアップ。例外時もクラッシュさせず通知のみ。
        //   savePath: これから上書きするファイルのパス
        //   newBytes: これから書き込む内容（差分なしならスキップ判定に使う）
        public static void BackupBeforeOverwrite(string savePath, byte[] newBytes, Settings s)
        {
            try
            {
                if (s == null || !s.AutoBackupEnabled) return;
                if (string.IsNullOrEmpty(savePath) || !File.Exists(savePath)) return;   // 新規保存はスキップ

                byte[] existing = File.ReadAllBytes(savePath);
                if (newBytes != null && existing.AsSpan().SequenceEqual(newBytes)) return;   // 内容が同一ならスキップ

                // プレイヤー名（＝キャラ名）はディスク上の（上書き前の）内容から取得する。
                string player = "unknown";
                try
                {
                    var root = Codec.Load(savePath);
                    string p = J.Str(J.Obj(root, "player_data"), "name");
                    if (!string.IsNullOrWhiteSpace(p)) player = Sanitize(p);
                }
                catch { /* 解析できなくても unknown で続行 */ }

                // 出力先 = backups\ 直下。ベースは設定の上書き先か、無ければセーブと同じ場所の backups\。
                // 親フォルダ（スロット名）に既にワールド名があるため、ワールド名のサブフォルダは作らない。
                string destDir = string.IsNullOrWhiteSpace(s.BackupBaseFolderOverride)
                    ? Path.Combine(Path.GetDirectoryName(savePath) ?? ".", "backups")
                    : s.BackupBaseFolderOverride;
                Directory.CreateDirectory(destDir);

                // 出力ファイル名 = <プレイヤー名>_savedata_yyyyMMdd_HHmmss.zip
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string zipPath = Path.Combine(destDir, $"{player}_savedata_{stamp}.zip");

                // 内部エントリ名は元ファイル名のまま。バイトは無変換で格納する（内容は同一）。
                string entryName = Path.GetFileName(savePath);
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var es = entry.Open();
                    es.Write(existing, 0, existing.Length);
                }

                // 世代の自動削除（ON のときのみ）。同系列の新しい順に保持数だけ残す。
                if (s.AutoDeleteOldBackups) PruneOld(destDir, player, s.BackupRetentionCount);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18n.T("msg.backupFailed") + "\n\n" + ex.Message,
                    I18n.T("title.backupFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // 同一系列（<player>_savedata_*.zip）を更新日時の新しい順に keep 件残し、超過分を削除する。
        private static void PruneOld(string destDir, string player, int keep)
        {
            if (keep < 1) keep = 1;
            var files = new DirectoryInfo(destDir)
                .GetFiles($"{player}_savedata_*.zip")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            foreach (var f in files.Skip(keep))
                try { f.Delete(); } catch { /* 削除失敗は無視 */ }
        }

        // Windows で不正な文字を _ に置換し、末尾の空白・ピリオドを除去する。空になれば unknown。
        private static string Sanitize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append("\\/:*?\"<>|".IndexOf(c) >= 0 ? '_' : c);
            string r = sb.ToString().TrimEnd(' ', '.');
            return r.Length == 0 ? "unknown" : r;
        }
    }

    // 設定ダイアログで開くセクション。メニューから対象を指定して開く。
    internal enum SettingsSection { Backup, Language, Misc }

    // 設定ダイアログ（モーダル）。指定セクション（バックアップ / 言語 / その他）のみ表示する。OK で settings.json を保存する。
    // 言語コンボの項目。表示名を見せ、値はコードを保持する。
    internal sealed class LangItem
    {
        public string Code { get; init; }
        public string Name { get; init; }
        public override string ToString() => Name;   // ComboBox はこの文字列を表示する
    }

    internal sealed class SettingsForm : Form
    {
        private readonly Settings _s;
        private readonly string _originalLang;   // キャンセル時に言語プレビューを元へ戻すため保持

        private readonly CheckBox _autoBackup = new() { AutoSize = true };
        private readonly TextBox _baseFolder = new() { Width = 320 };
        private readonly CheckBox _autoDelete = new() { AutoSize = true };
        private readonly NumericUpDown _retention = new() { Minimum = 1, Maximum = 9999, Width = 80 };

        // インベントリ（グリッド編集）。
        private readonly TextBox _assetRoot = new() { Width = 320 };
        private readonly NumericUpDown _invCols = new() { Minimum = 1, Maximum = 64, Width = 80 };
        private readonly NumericUpDown _invRows = new() { Minimum = 1, Maximum = 64, Width = 80 };

        // NPC エクスポート。
        private readonly CheckBox _npcPerWorld = new() { AutoSize = true };

        private readonly ComboBox _lang = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly ComboBox _sizeMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        private readonly NumericUpDown _fixedW = new() { Minimum = 400, Maximum = 10000, Width = 80 };
        private readonly NumericUpDown _fixedH = new() { Minimum = 300, Maximum = 10000, Width = 80 };
        private readonly CheckBox _rememberPos = new() { AutoSize = true };

        // Localize() で文言を再適用するため、可視文言を持つ要素を保持する。
        private GroupBox _grpBackup, _grpLanguage, _grpMisc, _grpAssetRoot, _grpInventory, _grpNpc;
        private Label _lblBaseFolder, _lblBaseHint, _lblRetention, _lblLang, _lblSizeMode, _lblFixedSize, _lblTimes;
        private Label _lblAssetRoot, _lblAssetNote, _lblAssetHint, _lblInvCols, _lblInvRows, _lblNpcHint;
        private Button _btnBrowse, _btnBrowseAsset, _btnOk, _btnCancel;

        private readonly SettingsSection _section;   // 表示するセクション

        // OK で確定された設定（呼び出し側は ShowDialog 後にこれを使う）。
        public Settings Result => _s;

        public SettingsForm(Settings current, SettingsSection section)
        {
            _s = current;
            _section = section;
            _originalLang = I18n.CurrentCode;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;

            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(10) };

            // 全グループを構築（文言再適用のため）するが、root に積むのは対象セクションのみ。
            // LoadValues/SaveValues は全項目を往復させるため、非表示セクションの値も保持される。
            BuildBackupGroup();
            BuildLanguageGroup();
            BuildMiscGroup();
            BuildAssetRootGroup();
            BuildInventoryGroup();
            BuildNpcGroup();
            switch (_section)
            {
                case SettingsSection.Backup: root.Controls.Add(_grpBackup); Width = 460; Height = 286; break;
                case SettingsSection.Language: root.Controls.Add(_grpLanguage); Width = 360; Height = 166; break;
                default: root.Controls.Add(_grpMisc); root.Controls.Add(_grpAssetRoot); root.Controls.Add(_grpInventory); root.Controls.Add(_grpNpc); Width = 460; Height = 600; break;
            }

            Controls.Add(root);
            Controls.Add(BuildButtonBar());

            LoadValues();
            WireEnableState();
            WireLanguagePreview();

            Localize();
            I18n.LanguageChanged += Localize;   // コンボでの言語プレビューに自フォームも追従する
        }

        // 自フォームの可視文言を現在言語で再適用する。
        private void Localize()
        {
            // タイトルは表示中のセクションに合わせる。
            Text = _section switch
            {
                SettingsSection.Backup => I18n.T("settings.group.backup"),
                SettingsSection.Language => I18n.T("settings.group.language"),
                _ => I18n.T("settings.group.misc"),
            };
            _grpBackup.Text = I18n.T("settings.group.backup");
            _autoBackup.Text = I18n.T("settings.autoBackup");
            _lblBaseFolder.Text = I18n.T("settings.baseFolder");
            _btnBrowse.Text = I18n.T("btn.browse");
            _lblBaseHint.Text = I18n.T("settings.baseHint");
            _autoDelete.Text = I18n.T("settings.autoDelete");
            _lblRetention.Text = I18n.T("settings.retention");

            _grpLanguage.Text = I18n.T("settings.group.language");
            _grpMisc.Text = I18n.T("settings.group.misc");
            _lblLang.Text = I18n.T("settings.language");
            _lblSizeMode.Text = I18n.T("settings.windowSize");
            _lblFixedSize.Text = I18n.T("settings.fixedSize");
            _lblTimes.Text = "×";
            _rememberPos.Text = I18n.T("settings.rememberPos");

            _grpAssetRoot.Text = I18n.T("settings.group.assetRoot");
            _lblAssetRoot.Text = I18n.T("settings.assetRoot");
            _btnBrowseAsset.Text = I18n.T("btn.browse");
            _lblAssetNote.Text = I18n.T("settings.assetNote");
            _lblAssetHint.Text = I18n.T("settings.assetHint");

            _grpInventory.Text = I18n.T("settings.group.inventory");
            _lblInvCols.Text = I18n.T("settings.invCols");
            _lblInvRows.Text = I18n.T("settings.invRows");

            _grpNpc.Text = I18n.T("settings.group.npc");
            _npcPerWorld.Text = I18n.T("settings.npcPerWorld");
            _lblNpcHint.Text = I18n.T("settings.npcPerWorldHint");

            // ウィンドウサイズモードのコンボ（選択は維持）。
            int sizeIdx = _sizeMode.SelectedIndex;
            _sizeMode.Items.Clear();
            _sizeMode.Items.AddRange(new object[] { I18n.T("settings.sizeMode.remember"), I18n.T("settings.sizeMode.fixed") });
            _sizeMode.SelectedIndex = sizeIdx >= 0 ? sizeIdx : (_s.WindowSizeMode == WindowSizeMode.Fixed ? 1 : 0);

            _btnOk.Text = I18n.T("btn.ok");
            _btnCancel.Text = I18n.T("btn.cancel");
        }

        // 言語イベントの登録解除（リーク防止）。
        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= Localize;
            base.Dispose(disposing);
        }

        // 「バックアップ」グループ。
        private GroupBox BuildBackupGroup()
        {
            _grpBackup = new GroupBox { Width = 410, Height = 180, Margin = new Padding(3) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 20, 8, 8) };

            int r = 0;
            t.Controls.Add(_autoBackup, 0, r); t.SetColumnSpan(_autoBackup, 2); r++;

            _lblBaseFolder = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblBaseFolder, 0, r);
            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            _btnBrowse = new Button { AutoSize = true };
            _btnBrowse.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = I18n.T("settings.browseDesc") };
                if (Directory.Exists(_baseFolder.Text)) d.SelectedPath = _baseFolder.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _baseFolder.Text = d.SelectedPath;
            };
            folderRow.Controls.Add(_baseFolder); folderRow.Controls.Add(_btnBrowse);
            t.Controls.Add(folderRow, 1, r); r++;

            _lblBaseHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
            t.Controls.Add(_lblBaseHint, 1, r); r++;

            t.Controls.Add(_autoDelete, 0, r); t.SetColumnSpan(_autoDelete, 2); r++;

            _lblRetention = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblRetention, 0, r);
            t.Controls.Add(_retention, 1, r); r++;

            _grpBackup.Controls.Add(t);
            return _grpBackup;
        }

        // 「言語/Language」グループ。
        private GroupBox BuildLanguageGroup()
        {
            _grpLanguage = new GroupBox { Width = 320, Height = 70, Margin = new Padding(3) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 20, 8, 8) };

            // 言語一覧は I18n から動的生成する（lang\ に JSON を置くだけで増える）。
            PopulateLanguages();
            _lblLang = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblLang, 0, 0);
            t.Controls.Add(_lang, 1, 0);

            _grpLanguage.Controls.Add(t);
            return _grpLanguage;
        }

        // 「その他」グループ（ウィンドウサイズなど。今後の雑多な設定もここに追加する）。
        private GroupBox BuildMiscGroup()
        {
            _grpMisc = new GroupBox { Width = 410, Height = 140, Margin = new Padding(3) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 20, 8, 8) };

            int r = 0;
            // サイズモードの項目は Localize() で設定する。
            _sizeMode.Items.AddRange(new object[] { "", "" });
            _lblSizeMode = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblSizeMode, 0, r);
            t.Controls.Add(_sizeMode, 1, r); r++;

            _lblFixedSize = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblFixedSize, 0, r);
            var sizeRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            sizeRow.Controls.Add(_fixedW);
            _lblTimes = new Label { Text = "×", AutoSize = true, Padding = new Padding(4, 6, 4, 0) };
            sizeRow.Controls.Add(_lblTimes);
            sizeRow.Controls.Add(_fixedH);
            t.Controls.Add(sizeRow, 1, r); r++;

            t.Controls.Add(_rememberPos, 0, r); t.SetColumnSpan(_rememberPos, 2); r++;

            _grpMisc.Controls.Add(t);
            return _grpMisc;
        }

        // 「インストールフォルダ指定」グループ（ゲーム導入先）。インベントリ以外の機能でも参照する共通設定。
        private GroupBox BuildAssetRootGroup()
        {
            _grpAssetRoot = new GroupBox { Width = 410, Height = 130, Margin = new Padding(3) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 20, 8, 8) };

            int r = 0;
            _lblAssetRoot = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblAssetRoot, 0, r);
            var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            _btnBrowseAsset = new Button { AutoSize = true };
            _btnBrowseAsset.Click += (_, _) =>
            {
                using var d = new FolderBrowserDialog { Description = I18n.T("settings.browseAssetDesc") };
                if (Directory.Exists(_assetRoot.Text)) d.SelectedPath = _assetRoot.Text;
                if (d.ShowDialog(this) == DialogResult.OK) _assetRoot.Text = d.SelectedPath;
            };
            folderRow.Controls.Add(_assetRoot); folderRow.Controls.Add(_btnBrowseAsset);
            t.Controls.Add(folderRow, 1, r); r++;

            _lblAssetNote = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
            t.Controls.Add(_lblAssetNote, 1, r); r++;

            _lblAssetHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
            t.Controls.Add(_lblAssetHint, 1, r); r++;

            _grpAssetRoot.Controls.Add(t);
            return _grpAssetRoot;
        }

        // 「インベントリ」グループ（グリッド列数/行数）。
        private GroupBox BuildInventoryGroup()
        {
            _grpInventory = new GroupBox { Width = 410, Height = 100, Margin = new Padding(3) };
            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 20, 8, 8) };

            int r = 0;
            _lblInvCols = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblInvCols, 0, r);
            t.Controls.Add(_invCols, 1, r); r++;

            _lblInvRows = new Label { AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
            t.Controls.Add(_lblInvRows, 0, r);
            t.Controls.Add(_invRows, 1, r); r++;

            _grpInventory.Controls.Add(t);
            return _grpInventory;
        }

        // 「NPCエクスポート」グループ（ワールド毎に分けるか）。
        private GroupBox BuildNpcGroup()
        {
            _grpNpc = new GroupBox { Width = 410, Height = 90, Margin = new Padding(3) };
            var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 20, 8, 8) };

            t.Controls.Add(_npcPerWorld, 0, 0);
            _lblNpcHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };
            t.Controls.Add(_lblNpcHint, 0, 1);

            _grpNpc.Controls.Add(t);
            return _grpNpc;
        }

        // 言語コンボを I18n.GetAvailableLanguages() で（再）構築する。現在の選択コードを維持する。
        private void PopulateLanguages()
        {
            string keep = (_lang.SelectedItem as LangItem)?.Code ?? _s.Language;
            _lang.Items.Clear();
            LangItem sel = null;
            foreach (var (code, name) in I18n.GetAvailableLanguages())
            {
                var item = new LangItem { Code = code, Name = name };
                _lang.Items.Add(item);
                if (code == keep) sel = item;
            }
            if (sel != null) _lang.SelectedItem = sel;
            else if (_lang.Items.Count > 0) _lang.SelectedIndex = 0;
        }

        // コンボの言語変更で即時プレビューする（OK で確定保存、キャンセルで元へ戻す）。
        private void WireLanguagePreview()
        {
            _lang.SelectedIndexChanged += (_, _) =>
            {
                if (_lang.SelectedItem is LangItem li) I18n.SetLanguage(li.Code);
            };
        }

        // 下部の OK / キャンセル。
        private Panel BuildButtonBar()
        {
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(0, 8, 8, 10) };
            _btnOk = new Button { Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.OK };
            _btnCancel = new Button { Dock = DockStyle.Right, Width = 90, DialogResult = DialogResult.Cancel };
            _btnOk.Click += (_, _) => { SaveValues(); Settings.Save(_s); };
            bar.Controls.Add(_btnOk); bar.Controls.Add(_btnCancel);
            AcceptButton = _btnOk; CancelButton = _btnCancel;
            return bar;
        }

        // キャンセルで閉じたときは言語プレビューを元へ戻す（OK 時は選択言語のまま）。
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (DialogResult != DialogResult.OK && I18n.CurrentCode != _originalLang)
                I18n.SetLanguage(_originalLang);
            base.OnFormClosed(e);
        }

        // 設定値をウィジェットへ反映する。
        private void LoadValues()
        {
            _autoBackup.Checked = _s.AutoBackupEnabled;
            _baseFolder.Text = _s.BackupBaseFolderOverride;
            _autoDelete.Checked = _s.AutoDeleteOldBackups;
            _retention.Value = Math.Clamp(_s.BackupRetentionCount, (int)_retention.Minimum, (int)_retention.Maximum);

            // 言語は PopulateLanguages() で現在コードに合わせて選択済み。
            _sizeMode.SelectedIndex = _s.WindowSizeMode == WindowSizeMode.Fixed ? 1 : 0;
            _fixedW.Value = Math.Clamp(_s.FixedWindowWidth, (int)_fixedW.Minimum, (int)_fixedW.Maximum);
            _fixedH.Value = Math.Clamp(_s.FixedWindowHeight, (int)_fixedH.Minimum, (int)_fixedH.Maximum);
            _rememberPos.Checked = _s.RememberWindowPosition;

            _assetRoot.Text = _s.GameAssetRoot;
            _invCols.Value = Math.Clamp(_s.InventoryGridColumns, (int)_invCols.Minimum, (int)_invCols.Maximum);
            _invRows.Value = Math.Clamp(_s.InventoryGridRows, (int)_invRows.Minimum, (int)_invRows.Maximum);

            _npcPerWorld.Checked = _s.NpcExportPerWorld;
        }

        // ウィジェットの値を設定へ書き戻す。
        private void SaveValues()
        {
            _s.AutoBackupEnabled = _autoBackup.Checked;
            _s.BackupBaseFolderOverride = _baseFolder.Text.Trim();
            _s.AutoDeleteOldBackups = _autoDelete.Checked;
            _s.BackupRetentionCount = (int)_retention.Value;

            _s.Language = (_lang.SelectedItem as LangItem)?.Code ?? "ja";
            _s.WindowSizeMode = _sizeMode.SelectedIndex == 1 ? WindowSizeMode.Fixed : WindowSizeMode.RememberLast;
            _s.FixedWindowWidth = (int)_fixedW.Value;
            _s.FixedWindowHeight = (int)_fixedH.Value;
            _s.RememberWindowPosition = _rememberPos.Checked;

            _s.GameAssetRoot = _assetRoot.Text.Trim();
            _s.InventoryGridColumns = (int)_invCols.Value;
            _s.InventoryGridRows = (int)_invRows.Value;

            _s.NpcExportPerWorld = _npcPerWorld.Checked;
        }

        // チェック状態に応じて関連ウィジェットの有効/無効を切り替える。
        private void WireEnableState()
        {
            void Update()
            {
                _retention.Enabled = _autoDelete.Checked;
                bool fixedMode = _sizeMode.SelectedIndex == 1;
                _fixedW.Enabled = _fixedH.Enabled = fixedMode;
            }
            _autoDelete.CheckedChanged += (_, _) => Update();
            _sizeMode.SelectedIndexChanged += (_, _) => Update();
            Update();
        }
    }
}
