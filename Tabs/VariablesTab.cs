// ゲーム変数タブ: game_variables をカテゴリ別の折りたたみグループで編集する。
// 実行時の一時状態が多く含まれるため注意書きを表示し、編集価値の高い「パーティ・進行」だけ既定で開く。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class VariablesTab : UserControl
    {
        // カテゴリ定義: 見出し / 含めるキー（この順で表示）/ 既定で開くか。
        // ここに無いキーは末尾の「その他」グループへ自動的に集約する（取りこぼし防止）。
        // title は表示名の i18n キー。
        private static readonly (string title, string[] keys, bool open)[] Groups =
        {
            ("vars.group.party", new[]
            { "party", "original_party", "exp_to_gain", "highest_cleared_quest_difficulty", "current_period" }, true),

            ("vars.group.battle", new[]
            { "in_battle", "in_colosseum_battle", "in_boss_battle", "generating_enemy_count",
              "escaped_member_in_battle", "surrendered_characters", "combat_log", "current_enemy_data", "loot_container" }, false),

            ("vars.group.conversation", new[]
            { "in_conversation", "in_action_in_conversation", "in_free_input", "in_shopping",
              "text_input_disabled", "is_party_member_talk_enabled", "current_conversation_history",
              "current_situation", "player_attempt_text" }, false),

            ("vars.group.uiBackup", new[]
            { "buttons", "buttons_backup", "buttons_backup_for_shopping", "function_dict_correspond_to_input",
              "input_backup", "input_backup_for_shopping", "hud_top_info_label", "hud_top_info_texts", "location_image" }, false),

            ("vars.group.logText", new[]
            { "to_save_texts", "quest_log", "quest_event_log", "quest_party_accompany_backgrounds",
              "current_narration_log", "vacation_hobby_log", "master_process_log", "current_quest_data" }, false),
        };

        private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true };  // グループを縦に積むスクロール領域
        private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false }; // 無い時の案内
        private readonly List<ObjectForm> _forms = new();   // 各グループのフォーム（保存時に全て Apply する）
        private JsonObject _root;   // NPC ID→名前の解決・追加候補の列挙に使う（party のメンバー編集用）

        private readonly Label _warn = new()   // 上部に常時表示する注意書き（実行時状態を含むため）。
        {
            Dock = DockStyle.Top,
            Height = 48,
            ForeColor = Color.Firebrick,
            Padding = new Padding(8, 6, 8, 6),
            AutoSize = false,
        };

        public VariablesTab()
        {
            // Fill(本体/案内) を先に、Top(注意) を後に追加して重なりを防ぐ。
            Controls.Add(_scroll);
            Controls.Add(_empty);
            Controls.Add(_warn);

            Localize();
            // 言語切替で注意書きとグループ見出しを更新する。Dispose で解除。
            I18n.LanguageChanged += OnLanguageChanged;
        }

        // 言語に依存する文言を適用する。
        private void Localize() => _warn.Text = I18n.T("vars.warning");

        // 言語切替時に注意書きとグループを作り直す。
        private void OnLanguageChanged() { Localize(); if (_root != null) Bind(_root); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= OnLanguageChanged;
            base.Dispose(disposing);
        }

        // game_variables をバインド。無ければグループを隠して案内を出す。
        public void Bind(JsonObject root)
        {
            _root = root;
            // 破棄はフォーカス中の入力欄に Leave を発火させ、コミット処理が走る。旧レコードの
            // 入力値が書き込まれるのを防ぐため、先に各フォームのバインドを外す（Clear() が行う）。
            foreach (var f in _forms) f.Clear();
            _forms.Clear();
            // Controls.Clear() は切り離すだけで破棄しない。再バインド（ファイル読込・言語切替・
            // ツール実行後）のたびに旧グループのハンドルが溜まり、枯渇でクラッシュするため破棄する。
            Ui.DisposeChildren(_scroll);
            var gv = J.Obj(root, "game_variables");
            if (gv == null)
            {
                _scroll.Visible = false; _empty.Visible = true;
                _empty.Text = I18n.T("msg.noGameVariables");
                return;
            }
            _empty.Visible = false; _scroll.Visible = true;

            // どのカテゴリにも属さないキーは「その他」へ。
            var known = new HashSet<string>(Groups.SelectMany(g => g.keys));
            var others = gv.Select(p => p.Key).Where(k => !known.Contains(k)).ToList();

            // Dock=Top は後から追加した方が上に来るため、表示順と逆（下のグループから）追加する。
            if (others.Count > 0) AddGroup(gv, "vars.group.other", others, false);
            for (int i = Groups.Length - 1; i >= 0; i--)
                AddGroup(gv, Groups[i].title, Groups[i].keys, Groups[i].open);
        }

        // 1カテゴリ分の折りたたみグループを作り、内部フォームに該当キーだけを表示する。titleKey は見出しの i18n キー。
        private void AddGroup(JsonObject gv, string titleKey, IEnumerable<string> keys, bool open)
        {
            var keyList = keys.Where(gv.ContainsKey).ToList();
            if (keyList.Count == 0) return;   // 対象キーが1つも無ければグループ自体を出さない

            var group = new CollapsibleGroup(I18n.T(titleKey), open);
            var form = new ObjectForm(autoSize: true) { Dock = DockStyle.Top };
            // party のメンバー編集フック（party を含むグループでのみ使われる）。
            form.PartyNpcNamer = ResolveNpcName;
            form.PartyNpcIsDead = ResolveNpcIsDead;
            form.PartyNpcCandidates = NpcCandidates;
            form.Bind(gv, keyOrder: keyList);
            group.Content.Controls.Add(form);
            _scroll.Controls.Add(group);
            _forms.Add(form);
        }

        // NPC ID を名前へ解決する。npcs に該当 ID があれば name を返し、無ければ null。
        private string ResolveNpcName(string id)
        {
            var npcs = J.Obj(_root, "npcs");
            if (npcs == null || string.IsNullOrEmpty(id)) return null;
            return npcs[id] is JsonObject o ? J.Str(o, "name") : null;
        }

        // NPC ID が死亡扱い（config.is_dead）か。
        private bool ResolveNpcIsDead(string id)
            => J.Obj(_root, "npcs")?[id] is JsonObject o && J.NpcIsDead(o);

        // party 追加候補に出す全 NPC の (ID, 名前, 死亡フラグ) 一覧。npcs が無ければ空。
        private IEnumerable<(string id, string name, bool dead)> NpcCandidates()
        {
            var npcs = J.Obj(_root, "npcs");
            if (npcs == null) yield break;
            foreach (var kv in npcs)
            {
                var o = kv.Value as JsonObject;
                yield return (kv.Key, o != null ? J.Str(o, "name", kv.Key) : kv.Key, o != null && J.NpcIsDead(o));
            }
        }

        // 保存前の最終反映（未確定入力の書き戻し）。全グループのフォームを順に確定する。
        public bool Apply()
        {
            foreach (var f in _forms)
                if (!f.Apply()) return false;
            return true;
        }
    }
}
