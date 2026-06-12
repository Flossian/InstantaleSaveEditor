// ゲーム変数タブ: game_variables を汎用フォームで編集する。
// 実行時の一時状態が多く含まれるため注意書きを表示する。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class VariablesTab : UserControl
    {
        private readonly ObjectForm _form = new() { Dock = DockStyle.Fill };   // game_variables 編集フォーム
        private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Visible = false }; // 無い時の案内

        public VariablesTab()
        {
            // 上部に常時表示する注意書き（実行時状態を含むため）。
            var warn = new Label
            {
                Dock = DockStyle.Top,
                Height = 48,
                ForeColor = Color.Firebrick,
                Padding = new Padding(8, 6, 8, 6),
                AutoSize = false,
                Text = "注意: ここはバトル中フラグ・会話状態・UI退避情報など実行時の一時状態を多く含みます。\n不用意な変更はゲーム進行と不整合を起こす可能性があります。変更時のゲーム挙動はツール制作者も把握できてません。",
            };
            // Fill(本体/案内) を先に、Top(注意) を後に追加して重なりを防ぐ。
            Controls.Add(_form);
            Controls.Add(_empty);
            Controls.Add(warn);
        }

        // game_variables をバインド。無ければフォームを隠して案内を出す。
        public void Bind(JsonObject root)
        {
            var gv = J.Obj(root, "game_variables");
            if (gv == null)
            {
                _form.Clear(); _form.Visible = false; _empty.Visible = true;
                _empty.Text = "このファイルには game_variables がありません。";
                return;
            }
            _empty.Visible = false; _form.Visible = true;
            _form.Bind(gv);
        }

        // 保存前の最終反映（未確定入力の書き戻し）。
        public bool Apply() => _form.Apply();
    }
}
