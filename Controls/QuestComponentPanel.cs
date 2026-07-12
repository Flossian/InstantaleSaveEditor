// クエスト部品（enemies / boss / events）を ObjectForm 内で構造化編集するパネル。
// ワールドタブで quest / story_quest 選択時に使い、クエスト作成（QuestCreator）と同等の操作を提供する:
//   追加/選択 … 部品ライブラリ（templates/{enemies,bosses,events}）から選んで追加（ライブラリはボタン押下時に読む）
//   編集      … 構造化フォーム（EnemyEditDialog / EventEditDialog）
//   JSON編集  … 生 JSON での微調整（JsonEditDialog）
// 編集は対象の JsonArray / 親クエストの boss へ即時反映する（ObjectForm.Apply() の対象外）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    // 部品ライブラリの読込ヘルパ。QuestCreator と同じディレクトリをボタン押下のたびに読む
    // （テンプレ抽出直後でも再選択だけで最新が反映される）。
    internal static class QuestComponentLib
    {
        public static List<JsonObject> Enemies() => Read(QuestCreator.EnemiesDir);
        public static List<JsonObject> Bosses() => Read(QuestCreator.BossesDir);
        public static List<JsonObject> Events() => Read(QuestCreator.EventsDir);
        // 敵編集フォームのコンボ候補（race 等）の抽出元。QuestCreator と同じくライブラリの敵＋ボス全件。
        public static IEnumerable<JsonObject> EnemyCandidates() => Enemies().Concat(Bosses());
        private static List<JsonObject> Read(string dir)
        { var l = new List<JsonObject>(); QuestCreator.ReadLib(dir, l); return l; }

        public static string EnemyName(JsonObject o) => J.Str(J.Obj(o, "data"), "name", I18n.T("label.unnamed"));
        public static string EventName(JsonObject o) => J.Str(o, "event_name", I18n.T("label.unnamed"));
    }

    // enemies / events（クエスト部品の配列）の一覧編集パネル。配列を直接書き換える。
    internal sealed class QuestComponentListPanel : Panel
    {
        private readonly bool _isEvent;   // true=イベント（EventEditDialog）/ false=敵（EnemyEditDialog）
        private readonly ListBox _list = new() { Dock = DockStyle.Top, Height = 110, IntegralHeight = false };
        private JsonArray _model;

        public QuestComponentListPanel(bool isEvent)
        {
            _isEvent = isEvent;
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.add"), Add));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.edit"), Edit));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.delete"), Delete));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("quest.btnJsonEdit"), EditJson));
            _list.DoubleClick += (_, _) => Edit();
            // Dock=Top は後入れが上。下から: 一覧 → ボタン列。
            Controls.Add(_list);
            Controls.Add(bar);
        }

        public void Bind(JsonArray model) { _model = model; RefreshList(); }

        private string SectionTitle => I18n.T(_isEvent ? "quest.section.events" : "quest.section.enemies");
        private string ItemName(JsonObject o) => _isEvent ? QuestComponentLib.EventName(o) : QuestComponentLib.EnemyName(o);

        private void RefreshList()
        {
            _list.Items.Clear();
            foreach (var n in _model) _list.Items.Add(ItemName(n as JsonObject ?? new JsonObject()));
        }

        // ライブラリから複数選択して末尾へ追加する（選んだ要素はクローン）。
        private void Add()
        {
            var lib = _isEvent ? QuestComponentLib.Events() : QuestComponentLib.Enemies();
            if (lib.Count == 0) { MessageBox.Show(I18n.T("quest.libEmpty", SectionTitle)); return; }
            var labels = lib.Select(ItemName).ToList();
            var previews = lib.Select(e => e.ToJsonString(Codec.Pretty)).ToList();
            foreach (int i in ListPicker.Pick(FindForm(), I18n.T("quest.pickTitle", SectionTitle), labels, previews, multi: true))
                if (i >= 0 && i < lib.Count) _model.Add((JsonObject)lib[i].DeepClone());
            RefreshList();
        }

        // 選択要素を構造化フォームで編集（敵=EnemyEditDialog / イベント=EventEditDialog）。
        private void Edit()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _model.Count) return;
            JsonObject result = null;
            if (_isEvent)
            {
                using var d = new EventEditDialog(I18n.T("event.editTitle"), _model[i] as JsonObject);
                if (d.ShowDialog(FindForm()) == DialogResult.OK) result = d.ResultNode;
            }
            else
            {
                using var d = new EnemyEditDialog(I18n.T("enemy.editTitle"), _model[i] as JsonObject, QuestComponentLib.EnemyCandidates());
                if (d.ShowDialog(FindForm()) == DialogResult.OK) result = d.ResultNode;
            }
            if (result != null) { _model[i] = result; RefreshList(); }
        }

        private void Delete()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _model.Count) return;
            if (MessageBox.Show(I18n.T("msg.confirmDelete"), I18n.T("title.confirm"), MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _model.RemoveAt(i);
            RefreshList();
        }

        private void EditJson()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _model.Count) return;
            using var d = new JsonEditDialog(I18n.T("title.editField", SectionTitle), _model[i]);
            if (d.ShowDialog(FindForm()) == DialogResult.OK && d.ResultNode != null)
            { _model[i] = d.ResultNode; RefreshList(); }
        }
    }

    // boss（単一の敵オブジェクト）の編集パネル。親クエストの boss プロパティを直接書き換える。
    // ゲームデータではボスなしは空オブジェクト（{}）のため、data を持たない boss は「なし」として扱う。
    internal sealed class QuestBossPanel : Panel
    {
        private readonly Label _lbl = new()
        { Dock = DockStyle.Top, Height = 26, AutoEllipsis = true, BorderStyle = BorderStyle.FixedSingle, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
        private JsonObject _quest;

        public QuestBossPanel()
        {
            AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("quest.select"), SelectFromLib));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("btn.edit"), Edit));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("quest.none"), Clear));
            bar.Controls.Add(SkillEditDialog.MakeBtn(I18n.T("quest.btnJsonEdit"), EditJson));
            // Dock=Top は後入れが上。下から: 表示ラベル → ボタン列。
            Controls.Add(_lbl);
            Controls.Add(bar);
        }

        public void Bind(JsonObject quest) { _quest = quest; RefreshLabel(); }

        // 現在のボス（data を持つオブジェクトのみ有効。{} や null は「なし」）。
        private JsonObject Boss => J.Obj(_quest, "boss") is JsonObject b && J.Obj(b, "data") != null ? b : null;

        private void RefreshLabel()
            => _lbl.Text = Boss is JsonObject b ? QuestComponentLib.EnemyName(b) : I18n.T("label.none");

        // ライブラリから単一選択で差し替える。
        private void SelectFromLib()
        {
            var lib = QuestComponentLib.Bosses();
            string title = I18n.T("quest.section.boss");
            if (lib.Count == 0) { MessageBox.Show(I18n.T("quest.libEmpty", title)); return; }
            var labels = lib.Select(QuestComponentLib.EnemyName).ToList();
            var previews = lib.Select(e => e.ToJsonString(Codec.Pretty)).ToList();
            int i = ListPicker.PickOne(FindForm(), I18n.T("quest.pickTitle", title), labels, previews);
            if (i < 0) return;
            _quest["boss"] = (JsonObject)lib[i].DeepClone();
            RefreshLabel();
        }

        // ボス未設定のまま編集を開くと新規作成（type=boss の骨格から）になる。
        private void Edit()
        {
            using var d = new EnemyEditDialog(I18n.T("quest.editBoss"), Boss, QuestComponentLib.EnemyCandidates(), defaultType: "boss");
            if (d.ShowDialog(FindForm()) == DialogResult.OK) { _quest["boss"] = d.ResultNode; RefreshLabel(); }
        }

        // 「なし」= ゲームデータと同じ空オブジェクトへ戻す。
        private void Clear() { _quest["boss"] = new JsonObject(); RefreshLabel(); }

        private void EditJson()
        {
            using var d = new JsonEditDialog(I18n.T("quest.editBoss"), _quest["boss"] ?? new JsonObject());
            if (d.ShowDialog(FindForm()) == DialogResult.OK && d.ResultNode is JsonObject o)
            { _quest["boss"] = o; RefreshLabel(); }
        }
    }
}
