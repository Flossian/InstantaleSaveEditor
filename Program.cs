// Instantale Save Editor (C# / WinForms)
// セーブデータ(savedata.json)の構造を土台にしたエディタ。
// タブ構成: プレイヤー / ワールド / ゲーム変数
// 入出力形式: 最小化UTF-8 (Codec 参照)
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // アプリ本体ウィンドウ。3つのタブ（プレイヤー/ワールド/ゲーム変数）とメニュー・ステータスを持つ。
    // _root に読み込んだ JSON を保持し、各タブはそれを共有して編集する。
    internal sealed class MainForm : Form
    {
        private JsonObject _root;                                           // 編集中のデータ全体
        private string _path;                                               // 現在のファイルパス
        private byte[] _baseline;                                           // 最後に読込/保存した時点の内容（未保存変更の検出用）
        private bool _srcPlain;                                             // 開いたファイルが平文 JSON だったか
        private readonly Settings _settings;                                // ツール設定（バックアップ・表示）

        // ワールド素データ（worlds\{スロット}\world_data.json）との同期用。savedata.json を開いたときだけ相方を持つ。
        // ゲームは同じ世界のレコードを両ファイルに持ち、店の売買や未生成エリアへの到着では world 側を引く。
        // このエディタで足したレコードが world 側に無いとそこで止まるため、保存時に足した分／消した分を写す（WorldSync）。
        private string _worldPath;             // 相方の world_data.json（無ければ null）
        private bool _worldPlain;              // 相方が平文 JSON で置かれていたか（開いた形式のまま書き戻す）
        private IdSet _saveBase;               // 読み込み／保存時点の save 側 id 集合（保存時の追加・削除差分の基準）
        private bool _worldMissingWarned;      // 相方が無い旨の警告を出したか（開いたファイルごとに1回）

        private const int MaxRecentFiles = 10;   // 「最近開いたファイル」の保持件数

        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly PlayerTab _player = new();
        private readonly WorldTab _world = new();
        private readonly VariablesTab _vars = new();
        private readonly StatusStrip _status = new();
        private readonly ToolStripStatusLabel _statusLabel = new();

        // Localize() で文言を再適用するため、可視文言を持つ要素を保持する。
        private TabPage _tpPlayer, _tpWorld, _tpVars;
        private ToolStripMenuItem _miFile, _miFileOpen, _miFileRecent, _miFileSave, _miFileSaveAs, _miFileExport, _miFileExit;
        private ToolStripMenuItem _miTools, _miToolGenerateWorld, _miToolCreateQuest, _miToolExtract, _miToolImportNpc, _miToolImportFacility, _miToolPlayerToNpc, _miToolPlayerToPreset, _miToolCleanup, _miToolSyncWorld, _miToolCompactQuestLog, _miToolEditRaw;
        private ToolStripMenuItem _miSettings, _miSettingsBackup, _miSettingsLang, _miSettingsMisc;
        private ToolStripMenuItem _miAutoBackup;   // メニュー右端の自動バックアップ ON/OFF トグル表示
        private string _statusKey = "status.initial";   // 現在のステータス文言キー（言語切替時に再解決する）

        public MainForm(Settings settings)
        {
            _settings = settings ?? new Settings();
            ApplyWindowSettings();   // 設定に基づいてサイズ/位置を決める（既定はセンター表示）

            // Fill(本体=タブ) を先に、端の Top(メニュー)/Bottom(ステータス) を後に追加して重なりを防ぐ。
            _player.Dock = _world.Dock = _vars.Dock = DockStyle.Fill;
            _tpPlayer = new TabPage(); _tpPlayer.Controls.Add(_player);
            _tpWorld = new TabPage(); _tpWorld.Controls.Add(_world);
            _tpVars = new TabPage(); _tpVars.Controls.Add(_vars);
            _tabs.TabPages.AddRange(new[] { _tpPlayer, _tpWorld, _tpVars });
            Controls.Add(_tabs);

            BuildMenu();

            _status.Items.Add(_statusLabel);
            _status.Dock = DockStyle.Bottom;
            Controls.Add(_status);

            // JSON ファイルをウィンドウのどこへドロップしても開けるようにする。
            EnableFileDrop(this);

            Localize();
            // 言語切替に追従して開いているウィンドウへ即時反映する。Dispose で解除する。
            I18n.LanguageChanged += Localize;
        }

        // 言語切替・初期化時に、このフォームの可視文言を現在言語で再適用する。
        private void Localize()
        {
            UpdateTitle();
            _tpPlayer.Text = I18n.T("tab.player");
            _tpWorld.Text = I18n.T("tab.world");
            _tpVars.Text = I18n.T("tab.variables");

            _miFile.Text = I18n.T("menu.file");
            _miFileOpen.Text = I18n.T("menu.file.open");
            _miFileRecent.Text = I18n.T("menu.file.recent");
            _miFileSave.Text = I18n.T("menu.file.save");
            _miFileSaveAs.Text = I18n.T("menu.file.saveAs");
            _miFileExport.Text = I18n.T("menu.file.exportJson");
            _miFileExit.Text = I18n.T("menu.file.exit");

            _miTools.Text = I18n.T("menu.tools");
            _miToolGenerateWorld.Text = I18n.T("menu.tools.generateWorld");
            _miToolCreateQuest.Text = I18n.T("menu.tools.createQuest");
            _miToolExtract.Text = I18n.T("menu.tools.extractTemplates");
            _miToolImportNpc.Text = I18n.T("menu.tools.importNpc");
            _miToolImportFacility.Text = I18n.T("menu.tools.importFacility");
            _miToolPlayerToNpc.Text = I18n.T("menu.tools.playerToNpc");
            _miToolPlayerToPreset.Text = I18n.T("menu.tools.playerToPreset");
            _miToolCleanup.Text = I18n.T("menu.tools.cleanup");
            _miToolSyncWorld.Text = I18n.T("menu.tools.syncWorld");
            _miToolCompactQuestLog.Text = I18n.T("menu.tools.compactQuestLog");
            _miToolEditRaw.Text = I18n.T("menu.tools.editRaw");

            _miSettings.Text = I18n.T("menu.settings");
            _miSettingsBackup.Text = I18n.T("menu.settings.backup");
            _miSettingsLang.Text = I18n.T("menu.settings.language");
            _miSettingsMisc.Text = I18n.T("menu.settings.misc");
            UpdateAutoBackupIndicator();

            _statusLabel.Text = ResolveStatus();
        }

        // 登録解除（言語切替イベントのリーク防止）。
        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= Localize;
            base.Dispose(disposing);
        }

        // メニューバー（ファイル / ツール）を構築する。文言は Localize() で設定する。
        private void BuildMenu()
        {
            var menu = new MenuStrip { Dock = DockStyle.Top };
            _miFile = new ToolStripMenuItem();
            // 主要操作には標準的なショートカットを割り当てる（メニュー右側に表示される）。
            _miFileOpen = new ToolStripMenuItem(null, null, (_, _) => OpenFile()) { ShortcutKeys = Keys.Control | Keys.O };
            _miFileRecent = new ToolStripMenuItem();
            _miFileSave = new ToolStripMenuItem(null, null, (_, _) => SaveFile()) { ShortcutKeys = Keys.Control | Keys.S };
            _miFileSaveAs = new ToolStripMenuItem(null, null, (_, _) => SaveFileAs()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S };
            _miFileExport = new ToolStripMenuItem(null, null, (_, _) => ExportPlain());
            _miFileExit = new ToolStripMenuItem(null, null, (_, _) => Close());
            _miFile.DropDownItems.Add(_miFileOpen);
            _miFile.DropDownItems.Add(_miFileRecent);
            _miFile.DropDownItems.Add(new ToolStripSeparator());
            _miFile.DropDownItems.Add(_miFileSave);
            _miFile.DropDownItems.Add(_miFileSaveAs);
            _miFile.DropDownItems.Add(new ToolStripSeparator());
            _miFile.DropDownItems.Add(_miFileExport);
            _miFile.DropDownItems.Add(new ToolStripSeparator());
            _miFile.DropDownItems.Add(_miFileExit);
            RebuildRecentMenu();

            _miTools = new ToolStripMenuItem();
            _miToolGenerateWorld = new ToolStripMenuItem(null, null, (_, _) => OpenWorldGenerator());
            _miToolCreateQuest = new ToolStripMenuItem(null, null, (_, _) => CreateQuest());
            _miToolExtract = new ToolStripMenuItem(null, null, (_, _) => ExtractTemplates());
            _miToolImportNpc = new ToolStripMenuItem(null, null, (_, _) => ImportNpc());
            _miToolImportFacility = new ToolStripMenuItem(null, null, (_, _) => ImportFacility());
            _miToolPlayerToNpc = new ToolStripMenuItem(null, null, (_, _) => PlayerToNpc());
            _miToolPlayerToPreset = new ToolStripMenuItem(null, null, (_, _) => PlayerToPreset());
            _miToolCleanup = new ToolStripMenuItem(null, null, (_, _) => CleanupWorld());
            _miToolSyncWorld = new ToolStripMenuItem(null, null, (_, _) => SyncWorld());
            _miToolCompactQuestLog = new ToolStripMenuItem(null, null, (_, _) => CompactQuestEventLog());
            _miToolEditRaw = new ToolStripMenuItem(null, null, (_, _) => EditRaw());
            _miTools.DropDownItems.Add(_miToolGenerateWorld);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
            _miTools.DropDownItems.Add(_miToolCreateQuest);
            _miTools.DropDownItems.Add(_miToolExtract);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
            _miTools.DropDownItems.Add(_miToolImportNpc);
            _miTools.DropDownItems.Add(_miToolImportFacility);
            _miTools.DropDownItems.Add(_miToolPlayerToNpc);
            _miTools.DropDownItems.Add(_miToolPlayerToPreset);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
            _miTools.DropDownItems.Add(_miToolCleanup);
            _miTools.DropDownItems.Add(_miToolSyncWorld);
            _miTools.DropDownItems.Add(_miToolCompactQuestLog);
            _miTools.DropDownItems.Add(_miToolEditRaw);

            // 設定（ファイル/ツールと同じドロップダウン形式に統一）。バックアップ / 言語 / その他に分ける。
            _miSettings = new ToolStripMenuItem();
            _miSettingsBackup = new ToolStripMenuItem(null, null, (_, _) => OpenSettings(SettingsSection.Backup));
            _miSettingsLang = new ToolStripMenuItem(null, null, (_, _) => OpenSettings(SettingsSection.Language));
            _miSettingsMisc = new ToolStripMenuItem(null, null, (_, _) => OpenSettings(SettingsSection.Misc));
            _miSettings.DropDownItems.Add(_miSettingsBackup);
            _miSettings.DropDownItems.Add(_miSettingsLang);
            _miSettings.DropDownItems.Add(_miSettingsMisc);

            // メニュー右端に自動バックアップ ON/OFF を表示。クリックで即トグルして settings.json へ保存する。
            _miAutoBackup = new ToolStripMenuItem { Alignment = ToolStripItemAlignment.Right };
            _miAutoBackup.Click += (_, _) =>
            {
                _settings.AutoBackupEnabled = !_settings.AutoBackupEnabled;
                Settings.Save(_settings);
                UpdateAutoBackupIndicator();
            };

            menu.Items.Add(_miFile); menu.Items.Add(_miTools); menu.Items.Add(_miSettings);
            menu.Items.Add(_miAutoBackup);
            MainMenuStrip = menu; Controls.Add(menu);
            UpdateMenuState();
        }

        // ファイル未読み込みの間は、開いているデータを前提とするメニューを無効にする。
        private void UpdateMenuState()
        {
            bool loaded = _root != null;
            _miFileSave.Enabled = _miFileSaveAs.Enabled = _miFileExport.Enabled = loaded;
            _miToolCreateQuest.Enabled = _miToolImportNpc.Enabled = _miToolImportFacility.Enabled = _miToolPlayerToNpc.Enabled
                = _miToolPlayerToPreset.Enabled = _miToolCleanup.Enabled = _miToolSyncWorld.Enabled = _miToolCompactQuestLog.Enabled = _miToolEditRaw.Enabled = loaded;
        }

        // 「最近開いたファイル」のドロップダウンを設定の履歴から作り直す。履歴が空なら無効化する。
        private void RebuildRecentMenu()
        {
            // Clear() は切り離すだけ。旧項目は Click ハンドラごと残るため破棄して作り直す。
            var old = _miFileRecent.DropDownItems.Cast<ToolStripItem>().ToArray();
            _miFileRecent.DropDownItems.Clear();
            foreach (var it in old) it.Dispose();
            var list = _settings.RecentFiles;
            if (list != null)
                foreach (var p in list)
                {
                    // 設定ファイルが壊れている（null 要素）と起動時に落ちるため読み飛ばす。
                    if (string.IsNullOrEmpty(p)) continue;
                    string path = p;
                    // & はニーモニック扱いされて表示が崩れるためエスケープする。
                    var mi = new ToolStripMenuItem(path.Replace("&", "&&"));
                    mi.Click += (_, _) => { if (ConfirmUnsavedChanges()) LoadPath(path); };
                    _miFileRecent.DropDownItems.Add(mi);
                }
            _miFileRecent.Enabled = _miFileRecent.DropDownItems.Count > 0;
        }

        // 履歴の先頭へ追加する（重複除去・上限超過は切り捨て）。設定保存とメニュー再構築まで行う。
        private void AddRecent(string path)
        {
            var list = _settings.RecentFiles ??= new List<string>();
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > MaxRecentFiles) list.RemoveRange(MaxRecentFiles, list.Count - MaxRecentFiles);
            Settings.Save(_settings);
            RebuildRecentMenu();
        }

        // 開けなかったファイルを履歴から取り除く（存在しなければ何もしない）。
        private void RemoveRecent(string path)
        {
            var list = _settings.RecentFiles;
            if (list == null || list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == 0) return;
            Settings.Save(_settings);
            RebuildRecentMenu();
        }

        // 設定ダイアログを指定セクションで開く。OK ならウィンドウサイズ等と右端表示を即時反映する。
        private void OpenSettings(SettingsSection section)
        {
            using var dlg = new SettingsForm(_settings, section);
            if (dlg.ShowDialog(this) == DialogResult.OK) ApplyWindowSettings();
            UpdateAutoBackupIndicator();   // バックアップ設定で ON/OFF が変わった可能性に追従
        }

        // メニュー右端の自動バックアップ ON/OFF 表示を現在の設定で更新する。
        private void UpdateAutoBackupIndicator()
        {
            if (_miAutoBackup == null) return;
            _miAutoBackup.Text = I18n.T(_settings.AutoBackupEnabled ? "menu.autoBackup.on" : "menu.autoBackup.off");
            _miAutoBackup.ForeColor = _settings.AutoBackupEnabled ? Color.ForestGreen : SystemColors.GrayText;
        }

        // 設定に基づいてウィンドウのサイズ/位置を決める。
        // 画面の作業領域にクランプして画面外・過大表示を防ぐ。
        private void ApplyWindowSettings()
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1040, 760);

            int w, h;
            if (_settings.WindowSizeMode == WindowSizeMode.Fixed)
            { w = _settings.FixedWindowWidth; h = _settings.FixedWindowHeight; }
            else
            {
                w = _settings.SavedWindowWidth > 0 ? _settings.SavedWindowWidth : 1040;
                h = _settings.SavedWindowHeight > 0 ? _settings.SavedWindowHeight : 760;
            }
            // 作業領域が下限より狭い環境（極小の仮想ディスプレイ等）では Math.Clamp が
            // min>max で例外になるため、上限側を優先して下限を潰す。
            Width = Math.Clamp(w, Math.Min(400, wa.Width), wa.Width);
            Height = Math.Clamp(h, Math.Min(300, wa.Height), wa.Height);

            // 位置: RememberLast かつ位置記憶 ON で保存値があるときのみ復元。それ以外はセンター。
            if (_settings.WindowSizeMode == WindowSizeMode.RememberLast && _settings.RememberWindowPosition
                && (_settings.SavedWindowWidth > 0 || _settings.SavedWindowHeight > 0))
            {
                StartPosition = FormStartPosition.Manual;
                int x = Math.Clamp(_settings.SavedWindowX, wa.Left, Math.Max(wa.Left, wa.Right - Width));
                int y = Math.Clamp(_settings.SavedWindowY, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
                Location = new Point(x, y);
            }
            else StartPosition = FormStartPosition.CenterScreen;
        }

        // 終了時、未保存の変更があれば確認する。続行時は前回サイズ記憶モードなら現在のサイズ
        // （位置記憶 ON なら位置も）を保存する。
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // ここで例外が抜けると e.Cancel が評価されないままウィンドウ処理が巻き戻り、
            // 「閉じられない」状態になる（タスクマネージャ以外で終了できなくなる）。
            // 未保存確認が失敗した場合は確認を諦めてそのまま閉じられるようにする。
            bool ok;
            try { ok = e.Cancel || ConfirmUnsavedChanges(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.closeCheckFailed") + "\n\n" + ex.Message,
                    I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                ok = true;
            }
            if (!ok)
            {
                e.Cancel = true;
                base.OnFormClosing(e);
                return;
            }
            if (_settings != null && _settings.WindowSizeMode == WindowSizeMode.RememberLast)
            {
                // 最大化/最小化中は復元時のサイズ(RestoreBounds)を採用する。
                var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                _settings.SavedWindowWidth = b.Width;
                _settings.SavedWindowHeight = b.Height;
                if (_settings.RememberWindowPosition) { _settings.SavedWindowX = b.X; _settings.SavedWindowY = b.Y; }
                Settings.Save(_settings);
            }
            base.OnFormClosing(e);
        }

        private object[] _statusArgs;   // ステータス文言の書式引数（言語切替時の再解決用）

        // ステータスバー表示（文言キーを保持し、言語切替時に再解決する）。ファイル名を併記する。
        private void Status(string key, params object[] args)
        {
            _statusKey = key; _statusArgs = args;
            _statusLabel.Text = ResolveStatus();
        }

        // 現在のステータスキー＋引数を現在言語で解決し、ファイル名を併記して返す。
        private string ResolveStatus()
            => I18n.T(_statusKey, _statusArgs ?? Array.Empty<object>())
               + (_path != null ? "  [" + FileLabel() + "]" : "");

        // ---------------- ファイル操作 ----------------
        // タイトルバーにアプリ名と編集中のファイル名を表示する。
        // パスなしのデータ（生成直後のワールド）はワールド名＋未保存の旨を出す。
        private void UpdateTitle()
            => Text = I18n.T("app.title")
               + (_path != null ? " - " + FileLabel()
                : _root != null ? " - " + I18n.T("title.generatedUnsaved", ResolveWorldName() ?? "") : "");

        // ファイル名＋（ワールド名）の表示文字列を作る。
        // ワールド名は world_data.name → 無ければファイル所在のワールドフォルダ名。
        private string FileLabel()
        {
            string name = Path.GetFileName(_path);
            string world = ResolveWorldName();
            return string.IsNullOrEmpty(world) ? name : $"{name}（{world}）";
        }

        // 現在開いているファイルのワールド名を解決する。
        private string ResolveWorldName()
        {
            string name = _root != null ? J.Str(J.Obj(_root, "world_data"), "name") : "";
            if (!string.IsNullOrWhiteSpace(name)) return name;
            string dir = WorldTab.ResolveWorldDir(_path);
            return string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir);
        }

        // ドロップ配線済みコントロール（重複配線の防止）。再バインドで親から外れたまま破棄されない
        // コントロールを溜め込まないよう、弱参照テーブルで保持する。
        private readonly ConditionalWeakTable<Control, object> _dropWired = new();

        // JSON ファイルのドラッグ＆ドロップ受け付けを再帰的に配線する。
        // OLE ドロップは親コントロールへ伝播しないため、子にも個別に配線する必要がある。
        // 後から生成されるコントロールには ControlAdded で追従する。
        private void EnableFileDrop(Control c)
        {
            if (!_dropWired.TryAdd(c, null)) return;   // 同一コントロールの再追加（再バインド時など）は配線済み
            // 固有のドラッグ／ドロップ動作を持ち得る入力・一覧系コントロールは対象外にする。
            if (c is not (TextBoxBase or ComboBox or ListBox or ListView or TreeView or DataGridView or UpDownBase))
            {
                c.AllowDrop = true;
                c.DragEnter += OnFileDragEnter;
                c.DragDrop += OnFileDragDrop;
            }
            c.ControlAdded += (_, e) => EnableFileDrop(e.Control);
            foreach (Control child in c.Controls) EnableFileDrop(child);
        }

        // JSON ファイルのドラッグ＆ドロップで開く（未保存の変更があれば確認してから読み込む）。
        private void OnFileDragEnter(object sender, DragEventArgs e)
        { if (GetDroppedJson(e) != null) e.Effect = DragDropEffects.Copy; }

        private void OnFileDragDrop(object sender, DragEventArgs e)
        {
            string p = GetDroppedJson(e);
            if (p != null && ConfirmUnsavedChanges()) LoadPath(p);
        }

        // ドロップされたデータから開ける JSON ファイルのパスを返す（対象外なら null）。
        private static string GetDroppedJson(DragEventArgs e)
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return null;
            string p = files[0];
            return File.Exists(p) && p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? p : null;
        }

        // 未保存の変更があれば保存するか確認する。続行してよいときのみ true。
        // はい=保存して続行 / いいえ=破棄して続行 / キャンセル=中断。
        private bool ConfirmUnsavedChanges()
        {
            if (_root == null || _baseline == null) return true;
            // 未確定入力を反映してから比較する。反映に失敗した場合も「変更あり」として確認する。
            bool applied = ApplyAll();
            if (applied && Codec.Encode(_root).AsSpan().SequenceEqual(_baseline)) return true;
            var r = MessageBox.Show(this, I18n.T("msg.unsavedPrompt"), I18n.T("title.unsaved"),
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return false;
            if (r != DialogResult.Yes) return true;
            // 反映に失敗している間は保存できない（型エラーは表示済み）。中断して修正してもらう。
            return applied && SaveFile();
        }

        // ファイルを開いて3タブにバインドする。失敗時はエラー表示。
        private void OpenFile()
        {
            if (!ConfirmUnsavedChanges()) return;
            // OpenFileDialog.InitialDirectory は環境変数を展開しないため、実パスに解決してから渡す。
            // 実際のセーブ位置は LocalAppData 側（%AppData% ではない）。存在する場合のみ指定する。
            string savesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Darmabeko", "Instantale", "saves");
            // 前回開いたフォルダがあればそれを初期位置に使う（無ければセーブ既定フォルダ）。
            string initDir = Directory.Exists(_settings.LastOpenedFolder) ? _settings.LastOpenedFolder
                           : Directory.Exists(savesDir) ? savesDir : "";
            using var dlg = new OpenFileDialog
            {
                InitialDirectory = initDir,
                Filter = I18n.T("filter.saveJson")
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            LoadPath(dlg.FileName);
        }

        // 指定パスを読み込んで3タブにバインドする（開く・履歴・ドラッグ＆ドロップの共通処理）。
        private void LoadPath(string path)
        {
            JsonObject root;
            try { root = Codec.Load(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.openFailed") + "\n\n" + ex.Message,
                    I18n.T("title.openFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                RemoveRecent(path);   // 開けないファイルは履歴に残さない
                return;
            }
            _root = root;
            _path = path;
            // 平文 JSON（エクスポートした確認用ファイル等）を開いた場合は、保存時に黙って
            // 難読化で上書きしないよう覚えておく。
            _srcPlain = Codec.IsPlainJsonFile(path);
            // 数値の表記を UI の書き戻しと同じ形へ揃えておく（未保存変更の誤検知防止）。
            CanonicalizeNumbers(_root);
            // 次回ダイアログの初期位置に使うため、開いたフォルダを記憶する（AddRecent が設定保存も行う）。
            _settings.LastOpenedFolder = Path.GetDirectoryName(_path) ?? "";
            AddRecent(_path);
            _player.Bind(_root, _path);
            _world.Bind(_root, _path);
            _vars.Bind(_root);
            // タブの書き戻しはグリッド再構築等で表記を揃えるため、未保存変更の基準は
            // 一度反映させた後の内容で取る（開いただけで「変更あり」になる誤検知の防止）。
            ApplyAll();
            _baseline = Codec.Encode(_root);
            // 相方の world_data.json（savedata を開いたときだけ）。保存時の同期と突き合わせツールで使う。
            // 以後の追加・削除を拾うため、読み込み時点の id 集合を控える。
            _worldPath = J.Obj(_root, "player_data") != null ? WorldSync.PartnerPath(_path) : null;
            _worldPlain = _worldPath != null && Codec.IsPlainJsonFile(_worldPath);
            _worldMissingWarned = false;
            _saveBase = IdSet.Capture(_root);
            UpdateMenuState();
            UpdateTitle();
            Status("status.loaded");
        }

        // 保存/エクスポート前に、全タブの未確定入力をモデルへ反映する。型エラーがあれば false。
        private bool ApplyAll()
            => _player.Apply() && _world.ApplyCurrent() && _vars.Apply();

        // 読み込んだ JSON の数値表記を、UI の書き戻し（各タブの long/double 生成）と同じ形へ揃える。
        // "10.0" のような表記は書き戻すと "10" になり、値が同じでもバイト比較で未保存変更と
        // 誤検知されるため、読込直後に正規化しておく。値が変わってしまう場合は元の表記を残す。
        private static void CanonicalizeNumbers(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    { if (Canonical(o[key]) is { } c) o[key] = c; else CanonicalizeNumbers(o[key]); }
                    break;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++)
                    { if (Canonical(a[i]) is { } c) a[i] = c; else CanonicalizeNumbers(a[i]); }
                    break;
            }
        }

        // 数値ノードの正規形を返す。数値でない・既に正規形・decimal で同値と確認できない場合は null。
        private static JsonNode Canonical(JsonNode n)
        {
            if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return null;
            string raw = v.ToJsonString();
            JsonNode canon =
                long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                    ? JsonValue.Create(l)
                : double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    ? JsonValue.Create(d)
                : null;
            if (canon == null || canon.ToJsonString() == raw) return null;
            return decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d1)
                && decimal.TryParse(canon.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d2)
                && d1 == d2 ? canon : null;
        }

        // 全タブを反映してから、ゲームが読める形式で上書き保存する。成功したら true。
        private bool SaveFile()
        {
            if (_root == null) return false;
            if (_path == null) return SaveFileAs();
            if (!ApplyAll()) return false;
            // 平文 JSON を開いていた場合、難読化で上書きすると形式が黙って変わる。どちらで書くか確認する。
            bool plain = _srcPlain;
            if (plain)
            {
                var ans = MessageBox.Show(this, I18n.T("msg.plainSaveConfirm"), I18n.T("title.confirm"),
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (ans == DialogResult.Cancel) return false;
                if (ans == DialogResult.Yes) { plain = false; _srcPlain = false; }   // 以後は難読化で保存する
            }
            // ワールド素データとの同期。読み込み後に足した／消したレコードを相方の world_data.json へ写す。
            // 相方はここでディスクから読み直す（ゲームが書いた最新を土台にし、古い内容で潰さない）。
            // 相方が読めない・書けないときは基準（_saveBase）を進めず、次の保存でもう一度写す。
            JsonObject world = null;
            WorldSync.Result sync = default;
            bool worldOk = true;
            if (_worldPath != null && WorldSync.PartnerPath(_path) == _worldPath)
            {
                try { world = Codec.Load(_worldPath); }
                catch (Exception ex)
                {
                    worldOk = false;
                    MessageBox.Show(this, I18n.T("msg.worldSync.loadFailed", ex.Message), I18n.T("title.worldSync"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (world != null)
                    sync = WorldSync.Apply(_root, world,
                        WorldSync.Added(_root, _saveBase, world), WorldSync.Removed(_root, _saveBase, world));
            }
            else if (_worldPath == null && WorldSync.IsSlotSavePath(_path) && !_worldMissingWarned)
            {
                // 黙って抜けると、直したつもりのまま壊れたセーブが増える。スロット内の savedata のときだけ知らせる。
                _worldMissingWarned = true;
                MessageBox.Show(this, I18n.T("msg.worldSync.missing"), I18n.T("title.worldSync"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            byte[] bytes;
            try
            {
                // 書き込む内容を先に確定し、上書き直前にディスク上のファイルをバックアップする。
                bytes = plain
                    ? new UTF8Encoding(false).GetBytes(_root.ToJsonString(Codec.Pretty))
                    : Codec.Encode(_root);
                BackupManager.BackupBeforeOverwrite(_path, bytes, _settings);
                Codec.WriteAtomic(_path, bytes);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, I18n.T("title.saveFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
            // savedata の書き込みが成った後で相方を書く（savedata が失敗したら world 側も書かない）。
            if (world != null && sync.WorldChanged && !WriteWorld(world)) worldOk = false;
            // 未保存判定は保存形式によらず難読化後のバイトで統一する。
            _baseline = Codec.Encode(_root);
            if (worldOk) _saveBase = IdSet.Capture(_root);
            if (sync.WorldChanged) Status("status.savedSynced", sync.Copied + sync.Programs, sync.Removed);
            else Status("status.saved");
            return true;
        }

        // world_data.json を savedata と同じ手順（バックアップ → 一時ファイル経由の置換）で書き戻す。
        private bool WriteWorld(JsonObject world)
        {
            try
            {
                byte[] bytes = _worldPlain
                    ? new UTF8Encoding(false).GetBytes(world.ToJsonString(Codec.Pretty))
                    : Codec.Encode(world);
                BackupManager.BackupBeforeOverwrite(_worldPath, bytes, _settings);
                Codec.WriteAtomic(_worldPath, bytes);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.worldSync.writeFailed", _worldPath, ex.Message), I18n.T("title.saveFailed"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // 別名保存（パスを決めてから上書き保存処理へ）。成功したら true。
        private bool SaveFileAs()
        {
            if (_root == null) return false;
            using var dlg = new SaveFileDialog { Filter = I18n.T("filter.saveJsonSave"), DefaultExt = "json" };
            // パスなしのデータ（生成直後のワールド）はゲームが読むファイル名を既定にする。
            if (_path == null)
                dlg.FileName = J.Obj(_root, "player_data") == null ? "world_data.json" : "savedata.json";
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            string prev = _path;
            _path = dlg.FileName;
            // 失敗時は元のパスへ戻す（表示や次回 Ctrl+S の保存先が変わったままにならないように）。
            if (!SaveFile()) { _path = prev; return false; }
            AddRecent(_path);
            UpdateTitle();
            return true;
        }

        // 整形JSONを書き出す（確認用。ゲームには読み込めない）。
        private void ExportPlain()
        {
            if (_root == null) return;
            if (!ApplyAll()) return;
            using var dlg = new SaveFileDialog { Filter = I18n.T("filter.json"), DefaultExt = "json", FileName = "savedata_plain.json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dlg.FileName, _root.ToJsonString(Codec.Pretty), new UTF8Encoding(false)); }
            catch (Exception ex)
            {
                // 書き込み先が読み取り専用・ロック中・取り外し済みでも保存失敗として扱う。
                MessageBox.Show(this, ex.Message, I18n.T("title.saveFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(this, I18n.T("msg.exportedPlain"),
                I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ---------------- ワールド生成 ----------------
        private WorldGenWindow _worldGenWindow;   // 開いている生成ウィンドウ（ノンモーダル・1枚のみ）

        // ワールド生成ウィンドウを開く。既に開いていれば前面化するだけ。
        private void OpenWorldGenerator()
        {
            if (_worldGenWindow is { IsDisposed: false }) { _worldGenWindow.Activate(); return; }
            _worldGenWindow = new WorldGenWindow(ConfirmUnsavedChanges, LoadGeneratedWorld);
            _worldGenWindow.Show(this);
        }

        // 生成されたワールドを未保存データとして全タブへ読み込む（ファイルパスなし。
        // 保存は Ctrl+S →「名前を付けて保存」）。未保存確認は生成ウィンドウ側が事前に行う。
        private void LoadGeneratedWorld(JsonObject root)
        {
            _root = root;
            _path = null;
            _worldPath = null; _saveBase = null; _worldMissingWarned = false;   // 生成データに相方は無い
            // 数値の表記を UI の書き戻しと同じ形へ揃えておく（未保存変更の誤検知防止）。
            CanonicalizeNumbers(_root);
            _player.Bind(_root, null);
            _world.Bind(_root, null);
            _vars.Bind(_root);
            // 生成直後を未保存変更の基準にする。手を加えず再生成した場合は確認なしで置き換わる。
            ApplyAll();
            _baseline = Codec.Encode(_root);
            UpdateMenuState();
            UpdateTitle();
            Status("status.worldGenerated", ResolveWorldName() ?? "");
            _tabs.SelectedTab = _tpWorld;   // 生成結果をすぐ確認できるようワールドタブを開く
        }

        // クエスト作成ダイアログを開き、OK ならワールドタブを再構築して結果を通知する。
        private void CreateQuest()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "areas") == null || J.Obj(_root, "quests") == null)
            { MessageBox.Show(this, I18n.T("msg.noAreasQuests")); return; }
            using var dlg = new QuestCreator(_root);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _world.Bind(_root, _path);   // ツリー更新
                Status("status.questCreated");
                MessageBox.Show(this, dlg.CreatedSummary, I18n.T("title.created"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // 指定した world_data / savedata からクエストを抽出し、テンプレ JSON として書き出す。
        private void ExtractTemplates()
        {
            string instantaleDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Darmabeko", "Instantale");
            using var dlg = new OpenFileDialog
            {
                InitialDirectory = Directory.Exists(instantaleDir) ? instantaleDir : "",
                Filter = I18n.T("filter.worldOrSave"),
                Title = I18n.T("title.selectTemplateSource")
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            JsonObject root;
            try { root = Codec.Load(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.loadFailed") + "\n\n" + ex.Message, I18n.T("title.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (J.Obj(root, "quests") == null)
            { MessageBox.Show(this, I18n.T("msg.noQuests")); return; }

            // 出典名: world_data.name → 無ければファイル所在のフォルダ名
            string source = J.Str(J.Obj(root, "world_data"), "name");
            if (string.IsNullOrWhiteSpace(source))
                source = Path.GetFileName(Path.GetDirectoryName(dlg.FileName));

            (int quests, int enemies, int bosses, int events, int skipped) res;
            try { res = QuestCreator.ExtractAll(root, source); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.writeFailed") + "\n\n" + ex.Message, I18n.T("title.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(this,
                I18n.T("msg.extracted.header", source) + "\n\n" +
                I18n.T("msg.extracted.quests", res.quests) + (res.skipped > 0 ? I18n.T("msg.extracted.skipped", res.skipped) : "") + "\n" +
                I18n.T("msg.extracted.parts", res.enemies, res.bosses, res.events) + "\n\n" +
                I18n.T("msg.extracted.output", QuestCreator.TemplatesRoot()),
                I18n.T("title.templatize"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            Status("status.templateExtracted", res.quests, res.enemies, res.bosses, res.events);
        }

        // npc\{ワールド}\ のライブラリから NPC を選び、配置先を指定してワールドへ挿入する。
        private void ImportNpc()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "npcs") == null || J.Obj(_root, "areas") == null || J.Obj(_root, "index") == null)
            { MessageBox.Show(this, I18n.T("msg.noNpcsAreasIndex")); return; }

            var worlds = NpcPortability.ListWorlds();
            if (worlds.Count == 0)
            { MessageBox.Show(this, I18n.T("npcimport.empty", NpcPortability.BaseDir())); return; }

            string worldDir = WorldTab.ResolveWorldDir(_path);
            using var dlg = new NpcImportDialog(_root, worldDir, worlds);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _world.Bind(_root, _path);   // ツリー更新
                Status("status.npcImported");
                MessageBox.Show(this, dlg.ResultSummary, I18n.T("title.importDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // facility\{ワールド}\ のライブラリから施設を選び、配置先と接続先を指定してワールドへ挿入する。
        private void ImportFacility()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "areas") == null)
            { MessageBox.Show(this, I18n.T("facimport.errNoContainers")); return; }

            var worlds = FacilityPortability.ListWorlds();
            if (worlds.Count == 0)
            { MessageBox.Show(this, I18n.T("facimport.empty", FacilityPortability.BaseDir())); return; }

            string worldDir = WorldTab.ResolveWorldDir(_path);
            using var dlg = new FacilityImportDialog(_root, worldDir, worlds);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _world.Bind(_root, _path);   // ツリー更新
                Status("status.facilityImported");
                MessageBox.Show(this, dlg.ResultSummary, I18n.T("title.importDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // 現在の player_data を NPC に変換し、npcs へ追加 / 単体 JSON でエクスポートする。
        private void PlayerToNpc()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "player_data") == null)
            { MessageBox.Show(this, I18n.T("msg.noPlayerData")); return; }
            if (!_player.Apply()) return;   // プレイヤータブの未確定入力を反映してから変換する

            using var dlg = new PlayerToNpcDialog(_root, WorldTab.ResolveWorldDir(_path));
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Added)
            {
                _world.Bind(_root, _path);   // ツリー更新
                Status("status.playerAddedAsNpc");
                MessageBox.Show(this, dlg.ResultSummary, I18n.T("title.addDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // 現在の player_data をキャラクタープリセット（キャラ作成画面のテンプレート）として
        // {Instantale}\characters\{名前}\ へ書き出す。
        private void PlayerToPreset()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "player_data") == null)
            { MessageBox.Show(this, I18n.T("msg.noPlayerData")); return; }
            if (!_player.Apply()) return;   // プレイヤータブの未確定入力を反映してから変換する

            using var dlg = new PlayerToPresetDialog(J.Obj(_root, "player_data"), _path, WorldTab.ResolveWorldDir(_path));
            if (dlg.ShowDialog(this) == DialogResult.OK)
                Status("status.presetExported");
        }

        // ワールドデータをクリーンアップする。確認後、ストーリークエストの status を incomplete に戻し、
        // 全 NPC の life_log / current_log を空に、relationship を初期値に戻す。実行後は world タブを再構築する。
        private void CleanupWorld()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "npcs") == null && J.Obj(_root, "story_quests") == null)
            { MessageBox.Show(this, I18n.T("msg.cleanup.nothing")); return; }

            if (MessageBox.Show(this, I18n.T("msg.cleanup.confirm"), I18n.T("title.confirm"),
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            var res = WorldCleanup.Run(_root);
            _world.Bind(_root, _path);   // life_log / relationship 等の表示を更新
            Status("status.cleaned", res.Quests, res.Npcs);
            MessageBox.Show(this, I18n.T("msg.cleanup.done", res.Quests, res.Npcs),
                I18n.T("title.cleanupDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // セーブデータにだけあるエリア・ノード・施設・NPC を一覧から選んで world_data.json へ写す。
        // 保存時の同期（SaveFile）は読み込み後の追加分しか扱わないため、既に片方にしか無い状態に
        // なっているセーブはこちらで手当てする。書き込みは即時（savedata 側は index が揃った場合のみ変わる）。
        private void SyncWorld()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (_worldPath == null || WorldSync.PartnerPath(_path) != _worldPath)
            {
                MessageBox.Show(this, I18n.T("msg.worldSync.noPartner"), I18n.T("title.worldSync"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ApplyAll()) return;
            JsonObject world;
            try { world = Codec.Load(_worldPath); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.worldSync.loadFailed", ex.Message), I18n.T("title.worldSync"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            var cand = WorldSync.SaveOnly(_root, world);
            var sel = new IdSet();   // 候補が無くても index の食い違いだけは揃える
            if (!cand.IsEmpty)
            {
                using var dlg = new WorldSyncDialog(_root, cand, _worldPath);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                sel = dlg.Selected;
            }
            var res = WorldSync.Apply(_root, world, sel, new IdSet());
            if (res.WorldChanged && !WriteWorld(world)) return;
            if (res.SaveIndexChanged) _world.Bind(_root, _path);   // index の表示を更新
            string msg = res.Copied + res.Programs == 0 && cand.IsEmpty
                ? I18n.T("msg.worldSync.nothing")
                : I18n.T("msg.worldSync.done", res.Areas, res.Nodes, res.Facilities, res.Npcs, res.Programs);
            if (res.Skipped > 0) msg += I18n.T("msg.worldSync.skipped", res.Skipped);
            if (res.SaveIndexChanged) msg += I18n.T("msg.worldSync.indexNote");
            Status("status.worldSynced", res.Copied + res.Programs);
            MessageBox.Show(this, msg, I18n.T("title.worldSync"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // quest_event_log を圧縮する（ゲーム側の既知バグ対策）。確認後、最新3件以外を切り捨てる。
        private void CompactQuestEventLog()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "game_variables") == null)
            { MessageBox.Show(this, I18n.T("msg.noGameVariables")); return; }

            if (MessageBox.Show(this, I18n.T("msg.compactQuestLog.confirm", QuestEventLogCompactor.KeepCount), I18n.T("title.confirm"),
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

            int removed = QuestEventLogCompactor.Run(_root);
            _vars.Bind(_root);   // quest_event_log の表示を更新
            Status("status.questLogCompacted", removed);
            MessageBox.Show(this, I18n.T("msg.compactQuestLog.done", removed),
                I18n.T("title.compactDone"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // データ全体を生 JSON で編集する（上級者向け）。OK なら差し替えて全タブ再バインド。
        private void EditRaw()
        {
            if (_root == null) return;
            if (!ApplyAll()) return;
            using var d = new JsonEditDialog(I18n.T("title.editWholeJson"), _root);
            if (d.ShowDialog(this) == DialogResult.OK && d.ResultNode is JsonObject obj)
            {
                _root = obj;
                _player.Bind(_root, _path); _world.Bind(_root, _path); _vars.Bind(_root);
                Status("status.jsonApplied");
            }
        }
    }

    // アプリのエントリポイント。WinForms 標準の初期化を行って MainForm を起動する。
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // UI スレッドの想定外の例外で即終了せず、内容を表示して作業を続行（保存）できるようにする。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => ShowUnhandled(e.Exception);
            var settings = Settings.Load();   // 起動時に設定をロードしてからフォームを生成する
            FieldOptions.EnsureMigrated();    // field_options.json も同様に exe 直下 → setting フォルダへ移行しておく
            I18n.Init(settings.Language);      // フォーム生成前に言語を確定する（初回書き出し・辞書構築）
            Application.AddMessageFilter(new ComboWheelGuard());   // 閉じたプルダウンへのホイールで値が変わるのを防ぐ
            Application.Run(new MainForm(settings));
        }

        private static bool _showingUnhandled;   // ダイアログ表示中フラグ（再入抑止）

        // 想定外の例外の内容を表示する。長大なスタックトレースは先頭のみ残す。
        // MessageBox はメッセージポンプを回すため、描画・レイアウト経路の例外は表示中に再発する。
        // そのままだとダイアログが際限なく積み重なって操作不能になるため、表示中の再発は捨てる。
        private static void ShowUnhandled(Exception ex)
        {
            if (_showingUnhandled) return;
            _showingUnhandled = true;
            try
            {
                string detail = ex?.ToString() ?? "";
                if (detail.Length > 1500) detail = detail[..1500] + "\n...";
                // 所有者を指定して本体の裏に隠れないようにする。
                MessageBox.Show(Form.ActiveForm, I18n.T("msg.unhandledError") + "\n" + detail,
                    I18n.T("title.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* 表示にも失敗する状況では何もできない */ }
            finally { _showingUnhandled = false; }
        }
    }
}
