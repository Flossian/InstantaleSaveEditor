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

        private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
        private readonly PlayerTab _player = new();
        private readonly WorldTab _world = new();
        private readonly VariablesTab _vars = new();
        private readonly StatusStrip _status = new();
        private readonly ToolStripStatusLabel _statusLabel = new();

        public MainForm()
        {
            Text = "Instantale Save Editor";
            Width = 1040; Height = 760; StartPosition = FormStartPosition.CenterScreen;

            // Fill(本体=タブ) を先に、端の Top(メニュー)/Bottom(ステータス) を後に追加して重なりを防ぐ。
            _player.Dock = _world.Dock = _vars.Dock = DockStyle.Fill;
            var tpPlayer = new TabPage("プレイヤー"); tpPlayer.Controls.Add(_player);
            var tpWorld = new TabPage("ワールド"); tpWorld.Controls.Add(_world);
            var tpVars = new TabPage("ゲーム変数"); tpVars.Controls.Add(_vars);
            _tabs.TabPages.AddRange(new[] { tpPlayer, tpWorld, tpVars });
            Controls.Add(_tabs);

            BuildMenu();

            _statusLabel.Text = "ファイルを開いてください（ファイル → 開く）。";
            _status.Items.Add(_statusLabel);
            _status.Dock = DockStyle.Bottom;
            Controls.Add(_status);
        }

        // メニューバー（ファイル / ツール）を構築する。
        private void BuildMenu()
        {
            var menu = new MenuStrip { Dock = DockStyle.Top };
            var file = new ToolStripMenuItem("ファイル(&F)");
            file.DropDownItems.Add("開く...", null, (_, _) => OpenFile());
            file.DropDownItems.Add("上書き保存", null, (_, _) => SaveFile());
            file.DropDownItems.Add("名前を付けて保存...", null, (_, _) => SaveFileAs());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("JSONをエクスポート...", null, (_, _) => ExportPlain());
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add("終了", null, (_, _) => Close());

            var tools = new ToolStripMenuItem("ツール(&T)");
            tools.DropDownItems.Add("クエスト作成（テンプレから）...", null, (_, _) => CreateQuest());
            tools.DropDownItems.Add("クエストをテンプレ化...", null, (_, _) => ExtractTemplates());
            tools.DropDownItems.Add("全体をJSONで編集...", null, (_, _) => EditRaw());

            menu.Items.Add(file); menu.Items.Add(tools);
            MainMenuStrip = menu; Controls.Add(menu);
        }

        // ステータスバー表示（ファイル名を併記）。
        private void Status(string m)
            => _statusLabel.Text = m + (_path != null ? "  [" + Path.GetFileName(_path) + "]" : "");

        // ---------------- ファイル操作 ----------------
        // ファイルを開いて3タブにバインドする。失敗時はエラー表示。
        private void OpenFile()
        {
            // OpenFileDialog.InitialDirectory は環境変数を展開しないため、実パスに解決してから渡す。
            // 実際のセーブ位置は LocalAppData 側（%AppData% ではない）。存在する場合のみ指定する。
            string savesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Darmabeko", "Instantale", "saves");
            using var dlg = new OpenFileDialog
            {
                InitialDirectory = Directory.Exists(savesDir) ? savesDir : "",
                Filter = "セーブ/JSON|*.json|すべて|*.*"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            try { _root = Codec.Load(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "ファイルの読み込みに失敗しました。\n\n" + ex.Message,
                    "読み込み失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _path = dlg.FileName;
            _player.Bind(_root, _path);
            _world.Bind(_root, _path);
            _vars.Bind(_root);
            Status("読み込み完了");
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
            try { Codec.Save(_path, _root); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失敗", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
            MessageBox.Show(this, "保存しました。", "保存", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Status("保存しました");
        }

        // 別名保存（パスを決めてから上書き保存処理へ）。
        private void SaveFileAs()
        {
            if (_root == null) return;
            using var dlg = new SaveFileDialog { Filter = "セーブ/JSON|*.json", DefaultExt = "json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _path = dlg.FileName; SaveFile();
        }

        // 整形JSONを書き出す（確認用。ゲームには読み込めない）。
        private void ExportPlain()
        {
            if (_root == null) return;
            if (!ApplyAll()) return;
            using var dlg = new SaveFileDialog { Filter = "JSON|*.json", DefaultExt = "json", FileName = "savedata_plain.json" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dlg.FileName, _root.ToJsonString(Codec.Pretty), new UTF8Encoding(false));
            MessageBox.Show(this, "整形JSONを保存しました（確認用・ゲームには読み込めません）。",
                "エクスポート", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // クエスト作成ダイアログを開き、OK ならワールドタブを再構築して結果を通知する。
        private void CreateQuest()
        {
            if (_root == null) { MessageBox.Show(this, "先にファイルを開いてください。"); return; }
            if (J.Obj(_root, "areas") == null || J.Obj(_root, "quests") == null)
            { MessageBox.Show(this, "このファイルには areas / quests がありません。"); return; }
            using var dlg = new QuestCreator(_root);
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _world.Bind(_root, _path);   // ツリー更新
                Status("クエストを作成しました");
                MessageBox.Show(this, dlg.CreatedSummary, "作成完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                Filter = "world_data / savedata|*.json|すべて|*.*",
                Title = "テンプレ化するファイルを選択（world_data または savedata）"
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            JsonObject root;
            try { root = Codec.Load(dlg.FileName); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "読み込みに失敗しました。\n\n" + ex.Message, "失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (J.Obj(root, "quests") == null)
            { MessageBox.Show(this, "このファイルには quests がありません。"); return; }

            // 出典名: world_data.name → 無ければファイル所在のフォルダ名
            string source = J.Str(J.Obj(root, "world_data"), "name");
            if (string.IsNullOrWhiteSpace(source))
                source = Path.GetFileName(Path.GetDirectoryName(dlg.FileName));

            (int quests, int enemies, int bosses, int events, int skipped) res;
            try { res = QuestCreator.ExtractAll(root, source); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "書き出しに失敗しました。\n\n" + ex.Message, "失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            MessageBox.Show(this,
                $"「{source}」から抽出しました。\n\n" +
                $"クエスト: {res.quests} 件" + (res.skipped > 0 ? $"（ダンジョン area が無い {res.skipped} 件は除外）" : "") + "\n" +
                $"敵: {res.enemies} 種 / ボス: {res.bosses} 種 / イベント: {res.events} 件\n\n" +
                $"出力先:\n{QuestCreator.TemplatesRoot()}",
                "テンプレ化", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Status($"テンプレ抽出: クエスト{res.quests} 敵{res.enemies} ボス{res.bosses} イベント{res.events}");
        }

        // データ全体を生 JSON で編集する（上級者向け）。OK なら差し替えて全タブ再バインド。
        private void EditRaw()
        {
            if (_root == null) return;
            if (!ApplyAll()) return;
            using var d = new JsonEditDialog("全体をJSONで編集", _root);
            if (d.ShowDialog(this) == DialogResult.OK && d.ResultNode is JsonObject obj)
            {
                _root = obj;
                _player.Bind(_root, _path); _world.Bind(_root, _path); _vars.Bind(_root);
                Status("JSONを反映しました");
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
            Application.Run(new MainForm());
        }
    }
}
