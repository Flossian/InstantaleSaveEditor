// quest_event_log の圧縮（ツールメニューから呼ぶ）。
// ゲーム側の既知バグでこのログが際限なく肥大化するため、修正待ちの間の緩和策として
// セーブエディタ側で最新分以外を切り捨てる。
// quest_event_log は配列ではなく、"〈プレイヤーの入力〉..." で始まる区切りが連なった1本の文字列。
// 区切り単位（＝プレイヤーの入力から次の入力の直前まで）を1ターンとし、末尾側の新しいターンを残す。
using System.Linq;
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class QuestEventLogCompactor
    {
        public const string TurnMarker = "〈プレイヤーの入力〉";
        public const int KeepCount = 3;

        // game_variables.quest_event_log を末尾 KeepCount ターンのみへ切り詰める。
        // 対象が無い、区切りが見つからない、または既に KeepCount ターン以内なら何もせず 0 を返す。戻り値は削除したターン数。
        public static int Run(JsonObject root)
        {
            var gv = J.Obj(root, "game_variables");
            if (gv == null) return 0;
            if (!gv.TryGetPropertyValue("quest_event_log", out var node) || node is not JsonValue v || !v.TryGetValue<string>(out var text))
                return 0;
            if (string.IsNullOrEmpty(text)) return 0;

            var turns = SplitTurns(text);
            if (turns == null || turns.Count <= KeepCount) return 0;

            int removed = turns.Count - KeepCount;
            gv["quest_event_log"] = string.Concat(turns.Skip(removed));
            return removed;
        }

        // TurnMarker の出現位置ごとに区切ってターン単位のリストにする。区切りが1つも無ければ null（切り詰め不可）。
        // 最初の区切りより前に文章がある場合（通常は無いはずの前置き）は、1ターン目に含めて情報を失わないようにする。
        private static List<string> SplitTurns(string text)
        {
            var indices = new List<int>();
            for (int pos = 0; (pos = text.IndexOf(TurnMarker, pos, StringComparison.Ordinal)) >= 0; pos += TurnMarker.Length)
                indices.Add(pos);
            if (indices.Count == 0) return null;

            var turns = new List<string>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                int segStart = indices[i];
                int segEnd = i + 1 < indices.Count ? indices[i + 1] : text.Length;
                turns.Add(text.Substring(segStart, segEnd - segStart));
            }
            if (indices[0] > 0) turns[0] = text.Substring(0, indices[0]) + turns[0];
            return turns;
        }
    }
}
