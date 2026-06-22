// Instantale Save Editor (C# / WinForms)
// セーブデータ(savedata.json)の構造を土台にしたエディタ。
// タブ構成: プレイヤー / ワールド / ゲーム変数
// 入出力形式: 最小化UTF-8 (Codec 参照)
using System.Text;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // アプリ本体ウィンドウ。3つのタブ（プレイヤー/ワールド/ゲーム変数）とメニュー・ステータスを持つ。
    // _root に読み込んだ JSON を保持し、各タブはそれを共有して編集する。
    internal sealed class MainForm : Form
    {
        private JsonObject _root;                                           // 編集中のデータ全体
        private string _path;                                               // 現在のファイルパス
        private readonly Settings _settings;                                // ツール設定（バックアップ・表示）

        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly PlayerTab _player = new();
        private readonly WorldTab _world = new();
        private readonly VariablesTab _vars = new();
        private readonly StatusStrip _status = new();
        private readonly ToolStripStatusLabel _statusLabel = new();

        // Localize() で文言を再適用するため、可視文言を持つ要素を保持する。
        private TabPage _tpPlayer, _tpWorld, _tpVars;
        private ToolStripMenuItem _miFile, _miFileOpen, _miFileSave, _miFileSaveAs, _miFileExport, _miFileExit;
        private ToolStripMenuItem _miTools, _miToolCreateQuest, _miToolExtract, _miToolImportNpc, _miToolPlayerToNpc, _miToolEditRaw;
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

            Localize();
            // 言語切替に追従して開いているウィンドウへ即時反映する。Dispose で解除する。
            I18n.LanguageChanged += Localize;
        }

        // 言語切替・初期化時に、このフォームの可視文言を現在言語で再適用する。
        private void Localize()
        {
            Text = I18n.T("app.title");
            _tpPlayer.Text = I18n.T("tab.player");
            _tpWorld.Text = I18n.T("tab.world");
            _tpVars.Text = I18n.T("tab.variables");

            _miFile.Text = I18n.T("menu.file");
            _miFileOpen.Text = I18n.T("menu.file.open");
            _miFileSave.Text = I18n.T("menu.file.save");
            _miFileSaveAs.Text = I18n.T("menu.file.saveAs");
            _miFileExport.Text = I18n.T("menu.file.exportJson");
            _miFileExit.Text = I18n.T("menu.file.exit");

            _miTools.Text = I18n.T("menu.tools");
            _miToolCreateQuest.Text = I18n.T("menu.tools.createQuest");
            _miToolExtract.Text = I18n.T("menu.tools.extractTemplates");
            _miToolImportNpc.Text = I18n.T("menu.tools.importNpc");
            _miToolPlayerToNpc.Text = I18n.T("menu.tools.playerToNpc");
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
            _miFileOpen = new ToolStripMenuItem(null, null, (_, _) => OpenFile());
            _miFileSave = new ToolStripMenuItem(null, null, (_, _) => SaveFile());
            _miFileSaveAs = new ToolStripMenuItem(null, null, (_, _) => SaveFileAs());
            _miFileExport = new ToolStripMenuItem(null, null, (_, _) => ExportPlain());
            _miFileExit = new ToolStripMenuItem(null, null, (_, _) => Close());
            _miFile.DropDownItems.Add(_miFileOpen);
            _miFile.DropDownItems.Add(_miFileSave);
            _miFile.DropDownItems.Add(_miFileSaveAs);
            _miFile.DropDownItems.Add(new ToolStripSeparator());
            _miFile.DropDownItems.Add(_miFileExport);
            _miFile.DropDownItems.Add(new ToolStripSeparator());
            _miFile.DropDownItems.Add(_miFileExit);

            _miTools = new ToolStripMenuItem();
            _miToolCreateQuest = new ToolStripMenuItem(null, null, (_, _) => CreateQuest());
            _miToolExtract = new ToolStripMenuItem(null, null, (_, _) => ExtractTemplates());
            _miToolImportNpc = new ToolStripMenuItem(null, null, (_, _) => ImportNpc());
            _miToolPlayerToNpc = new ToolStripMenuItem(null, null, (_, _) => PlayerToNpc());
            _miToolEditRaw = new ToolStripMenuItem(null, null, (_, _) => EditRaw());
            _miTools.DropDownItems.Add(_miToolCreateQuest);
            _miTools.DropDownItems.Add(_miToolExtract);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
            _miTools.DropDownItems.Add(_miToolImportNpc);
            _miTools.DropDownItems.Add(_miToolPlayerToNpc);
            _miTools.DropDownItems.Add(new ToolStripSeparator());
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

        // 終了時、前回サイズ記憶モードなら現在のサイズ（位置記憶 ON なら位置も）を保存する。
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
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
        // ファイルを開いて3タブにバインドする。失敗時はエラー表示。
        private void OpenFile()
        {
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
            try { _root = Codec.Load(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.openFailed") + "\n\n" + ex.Message,
                    I18n.T("title.openFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _path = dlg.FileName;
            // 次回ダイアログの初期位置に使うため、開いたフォルダを記憶する。
            _settings.LastOpenedFolder = Path.GetDirectoryName(_path) ?? "";
            Settings.Save(_settings);
            _player.Bind(_root, _path);
            _world.Bind(_root, _path);
            _vars.Bind(_root);
            Status("status.loaded");
        }

        // 保存/エクスポート前に、全タブの未確定入力をモデルへ反映する。型エラーがあれば false。
        private bool ApplyAll()
            => _player.Apply() && _world.ApplyCurrent() && _vars.Apply();

        // 全タブを反映してから、ゲームが読める形式で上書き保存する。
        private void SaveFile()
        {
            if (_root == null) return;
            if (_path == null) { SaveFileAs(); return; }
            if (!ApplyAll()) return;
            try
            {
                // 書き込む内容を先に確定し、上書き直前にディスク上のファイルをバックアップする。
                byte[] bytes = Codec.Encode(_root);
                BackupManager.BackupBeforeOverwrite(_path, bytes, _settings);
                File.WriteAllBytes(_path, bytes);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, I18n.T("title.saveFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            MessageBox.Show(this, I18n.T("msg.saved"), I18n.T("title.save"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            Status("status.saved");
        }

        // 別名保存（パスを決めてから上書き保存処理へ）。
        private void SaveFileAs()
        {
            if (_root == null) return;
            using var dlg = new SaveFileDialog { Filter = I18n.T("filter.saveJsonSave"), DefaultExt = "json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _path = dlg.FileName; SaveFile();
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

        // NPCパッケージ(zip)を読み込み、配置先を指定してワールドへ挿入する。
        private void ImportNpc()
        {
            if (_root == null) { MessageBox.Show(this, I18n.T("msg.openFileFirst")); return; }
            if (J.Obj(_root, "npcs") == null || J.Obj(_root, "areas") == null || J.Obj(_root, "index") == null)
            { MessageBox.Show(this, I18n.T("msg.noNpcsAreasIndex")); return; }

            using var ofd = new OpenFileDialog
            {
                Filter = I18n.T("filter.npcPackage"),
                Title = I18n.T("title.selectNpcPackage"),
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            NpcPackage pkg;
            try { pkg = NpcPortability.ReadPackage(ofd.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, I18n.T("msg.loadFailed") + "\n\n" + ex.Message, I18n.T("title.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string worldDir = WorldTab.ResolveWorldDir(_path);
            using var dlg = new NpcImportDialog(_root, worldDir, pkg);
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
