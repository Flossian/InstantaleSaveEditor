// ワールド生成ウィンドウ（ノンモーダル）。名前・シード・集落数を指定して生成し、
// 結果をエディタ本体へ未保存データとして即時読み込ませる。
// 本体との連携はデリゲート経由: confirmReplace（未保存確認。開いているデータを閉じてよいか）
// → true なら生成 → load（本体の全タブへバインド）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal sealed class WorldGenWindow : Form
    {
        private readonly TextBox _name = new() { Width = 170 };
        private readonly NumericUpDown _seed = new() { Width = 110, Minimum = 0, Maximum = int.MaxValue };
        private readonly NumericUpDown _settlements = new() { Width = 60, Minimum = 1, Maximum = 50, Value = 9 };
        private readonly NumericUpDown _ownerEpithet = new() { Width = 60, Minimum = 0, Maximum = 100, Value = 20 };
        private readonly NumericUpDown _adventurerEpithet = new() { Width = 60, Minimum = 0, Maximum = 100, Value = 60 };
        private readonly Button _dice = new() { Text = "🎲", Width = 34 };
        private readonly Button _generate = new() { Width = 100 };
        private readonly Label _lblName = new() { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _lblSeed = new() { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _lblSettlements = new() { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly GroupBox _npcGroup = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        private readonly Label _lblOwnerEpithet = new() { AutoSize = true, Anchor = AnchorStyles.Left };
        private readonly Label _lblAdventurerEpithet = new() { AutoSize = true, Anchor = AnchorStyles.Left };

        private readonly Func<bool> _confirmReplace;      // 生成前の未保存確認（false で中断）
        private readonly Action<JsonObject> _load;        // 生成結果を本体へ読み込ませる

        public WorldGenWindow(Func<bool> confirmReplace, Action<JsonObject> load)
        {
            _confirmReplace = confirmReplace;
            _load = load;

            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            _seed.Value = new Random().Next(int.MaxValue);   // 起動時にランダムなシード
            _dice.Click += (_, _) => _seed.Value = new Random().Next(int.MaxValue);
            _generate.Click += (_, _) => Generate();

            // 2列グリッド（ラベル / 入力欄）＋最下段に生成ボタン。
            var grid = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
            };
            var seedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            seedRow.Controls.AddRange(new Control[] { _seed, _dice });
            grid.Controls.Add(_lblName, 0, 0); grid.Controls.Add(_name, 1, 0);
            grid.Controls.Add(_lblSeed, 0, 1); grid.Controls.Add(seedRow, 1, 1);
            grid.Controls.Add(_lblSettlements, 0, 2); grid.Controls.Add(_settlements, 1, 2);

            // NPC生成グループ（二つ名付与率）。ラベル / 数値欄＋% の2列グリッド。
            var npcGrid = new TableLayoutPanel
            {
                ColumnCount = 2,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(6, 3, 6, 3),
            };
            npcGrid.Controls.Add(_lblOwnerEpithet, 0, 0); npcGrid.Controls.Add(PercentRow(_ownerEpithet), 1, 0);
            npcGrid.Controls.Add(_lblAdventurerEpithet, 0, 1); npcGrid.Controls.Add(PercentRow(_adventurerEpithet), 1, 1);
            _npcGroup.Controls.Add(npcGrid);
            _npcGroup.Margin = new Padding(0, 8, 0, 0);
            grid.Controls.Add(_npcGroup, 0, 3);
            grid.SetColumnSpan(_npcGroup, 2);

            _generate.Anchor = AnchorStyles.Right;
            grid.Controls.Add(_generate, 1, 4);
            Controls.Add(grid);

            AcceptButton = _generate;

            static Control PercentRow(NumericUpDown num)
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
                row.Controls.Add(num);
                row.Controls.Add(new Label { Text = "%", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(2, 6, 0, 0) });
                return row;
            }

            Localize();
            // 言語切替に追従して開いているウィンドウへ即時反映する。Dispose で解除する。
            I18n.LanguageChanged += Localize;
        }

        // 言語切替・初期化時に、このフォームの可視文言を現在言語で再適用する。
        private void Localize()
        {
            Text = I18n.T("worldgen.title");
            _lblName.Text = I18n.T("worldgen.name");
            _lblSeed.Text = I18n.T("worldgen.seed");
            _lblSettlements.Text = I18n.T("worldgen.settlements");
            _npcGroup.Text = I18n.T("worldgen.npcGroup");
            _lblOwnerEpithet.Text = I18n.T("worldgen.ownerEpithet");
            _lblAdventurerEpithet.Text = I18n.T("worldgen.adventurerEpithet");
            _generate.Text = I18n.T("worldgen.generate");
        }

        // 登録解除（言語切替イベントのリーク防止）。
        protected override void Dispose(bool disposing)
        {
            if (disposing) I18n.LanguageChanged -= Localize;
            base.Dispose(disposing);
        }

        // 集落数の確認 → 未保存確認 → 生成 → 本体へ読み込み。
        private void Generate()
        {
            // 集落数は 9 固定が前提。9 以外はゲームがクラッシュし読み込めない恐れがある。
            if ((int)_settlements.Value != 9 &&
                MessageBox.Show(this, I18n.T("worldgen.warnSettlements"), I18n.T("title.confirm"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            if (!_confirmReplace()) return;

            JsonObject root;
            try
            {
                root = new WorldGenerator().Generate(new GenOptions
                {
                    Name = _name.Text,
                    Seed = (int)_seed.Value,
                    Settlements = (int)_settlements.Value,
                    OwnerEpithetChance = (double)_ownerEpithet.Value / 100.0,
                    AdventurerEpithetChance = (double)_adventurerEpithet.Value / 100.0,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, I18n.T("worldgen.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _load(root);
            // 名前未指定でランダムに決まった場合は、決まった名前を欄へ反映する。
            if (string.IsNullOrWhiteSpace(_name.Text))
                _name.Text = J.Str(J.Obj(root, "world_data"), "name");
        }
    }
}
