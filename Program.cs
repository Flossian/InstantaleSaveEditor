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
        private readonly Settings _settings;                                // ツール設定（バックアップ・表示）

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
        private ToolStripMenuItem _miTools, _miToolCreateQuest, _miToolExtract, _miToolImportNpc, _miToolPlayerToNpc, _miToolCleanup, _miToolEditRaw;
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
            _miToolCreateQuest.Text = I18n.T("menu.tools.createQuest");
            _miToolExtract.Text = I18n.T("menu.tools.extractTemplates");
            _miToolImportNpc.Text = I18n.T("menu.tools.importNpc");
            _miToolPlayerToNpc.Text = I18n.T("menu.tools.playerToNpc");
            _miToolCleanup.Text = I18n.T("menu.tools.cleanup");
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
            _miToolCreateQuest = new ToolStripMenuItem(null, null, (_, _) => CreateQuest());
            _miToolExtract = new ToolStripMenuItem(null, null, (_, _) => ExtractTemplates());
            _miToolImportNpc = new ToolStripMenuItem(null, null, (_, _) => ImportNpc());
            _miToolPlayerToNpc = new ToolStripMenuItem(null, null, (_, _) => PlayerToNpc());
            _miToolCleanup = new ToolStripMenuItem(null, null, (_, _) => CleanupWorld());
            _miToolEditRaw = new ToolStripMenuItem(null, null, (_, _) => EditRaw());
            _miTools.DropDownItems.Add(_miToolCreateQuest);
            _miTools.DropDownItems.Add(_miToolExtract);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
            _miTools.DropDownItems.Add(_miToolImportNpc);
            _miTools.DropDownItems.Add(_miToolPlayerToNpc);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
            _miTools.DropDownItems.Add(_miToolCleanup);
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
            _miToolCreateQuest.Enabled = _miToolImportNpc.Enabled = _miToolPlayerToNpc.Enabled
                = _miToolCleanup.Enabled = _miToolEditRaw.Enabled = loaded;
        }

        // 「最近開いたファイル」のドロップダウンを設定の履歴から作り直す。履歴が空なら無効化する。
        private void RebuildRecentMenu()
        {
            _miFileRecent.DropDownItems.Clear();
            var list = _settings.RecentFiles;
            if (list != null)
                foreach (var p in list)
                {
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
            Width = Math.Clamp(w, 400, wa.Width);
            Height = Math.Clamp(h, 300, wa.Height);

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
            if (!e.Cancel && !ConfirmUnsavedChanges())
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
               + (_path != null ? "  [" + Path.GetFileName(_path) + "]" : "");

        // ---------------- ファイル操作 ----------------
        // タイトルバーにアプリ名と編集中のファイル名を表示する。
        private void UpdateTitle()
            => Text = I18n.T("app.title") + (_path != null ? " - " + Path.GetFileName(_path) : "");

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
            byte[] bytes;
            try
            {
                // 書き込む内容を先に確定し、上書き直前にディスク上のファイルをバックアップする。
                bytes = Codec.Encode(_root);
                BackupManager.BackupBeforeOverwrite(_path, bytes, _settings);
                File.WriteAllBytes(_path, bytes);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, I18n.T("title.saveFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
            _baseline = bytes;
            Status("status.saved");
            return true;
        }

        // 別名保存（パスを決めてから上書き保存処理へ）。成功したら true。
        private bool SaveFileAs()
        {
            if (_root == null) return false;
            using var dlg = new SaveFileDialog { Filter = I18n.T("filter.saveJsonSave"), DefaultExt = "json" };
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
            File.WriteAllText(dlg.FileName, _root.ToJsonString(Codec.Pretty), new UTF8Encoding(false));
            MessageBox.Show(this, I18n.T("msg.exportedPlain"),
                I18n.T("title.export"), MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            var settings = Settings.Load();   // 起動時に設定をロードしてからフォームを生成する
            I18n.Init(settings.Language);      // フォーム生成前に言語を確定する（初回書き出し・辞書構築）
            Application.Run(new MainForm(settings));
        }
    }
}
