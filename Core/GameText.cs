// ゲームデータに書く固定の文言を、ワールドの言語（ルート直下の "language"）に合わせて返す。
//
// ゲームは新しい NPC の初期値（好感度の文 "警戒心がある"・関係 "初対面"・記憶の要約 "ゲーム開始"）を
// 自分の翻訳表（scripts.languages.translate_dict）で訳してから保存する。
// 英語のワールドには "Wary" / "Game Start"、繁体字のワールドには "抱持戒心" / "陌生人" が入っている（実データで確認）。
// 日本語のまま書くと、そのワールドの NPC だけ別の言語の文を持つことになるため、ここで訳す。
// 訳はゲーム本体（main_026）の translate_dict から写したもの。ru は main_026 で追加された。
//
// 一方、雇用した仲間の印 "同行中" は翻訳表に無く、ゲームは言語によらず日本語のまま書く（PartyMembers.Accompany）。
using System.Text.Json.Nodes;

namespace InstantaleSaveEditor
{
    internal static class GameText
    {
        // ゲームが対応する言語コード（ワールドの "language"・キャラクタープリセットの "language" と同じ語彙）。
        public static readonly string[] Languages = { "ja", "en", "zh-Hant", "ru" };

        // 日本語 → { ja, en, zh-Hant, ru }。ゲームの translate_dict と同じ訳。
        private static readonly Dictionary<string, string[]> Table = new()
        {
            ["警戒心がある"] = new[] { "警戒心がある", "Wary", "抱持戒心", "Настороженность" },
            ["初対面"] = new[] { "初対面", "Stranger", "陌生人", "Незнакомец" },
            ["ゲーム開始"] = new[] { "ゲーム開始", "Game Start", "遊戲開始", "Начать игру" },
            ["家族"] = new[] { "家族", "Family", "家人", "Семья" },
        };

        // エディタが既定で入れる NPC の説明文（ゲームの文言ではない）。空だと読込が終わらないため必ず非空にする。
        private static readonly Dictionary<string, string[]> EditorTable = new()
        {
            ["profile"] = new[]
            {
                "この地で暮らす人物。詳しい経歴はまだ語られていない。",
                "A person who lives in this land. Their past has yet to be told.",
                "生活在這片土地上的人物。詳細的經歷尚未被述說。",
                "Житель этих мест. Подробности его прошлого пока неизвестны.",
            },
            ["personality"] = new[]
            {
                "落ち着いた物腰で、接する相手に応じて態度を変える。",
                "Calm in manner, and changes their attitude depending on who they are dealing with.",
                "舉止沉穩，會依接觸的對象改變態度。",
                "Держится спокойно и меняет манеру общения в зависимости от собеседника.",
            },
        };

        // ルートのワールド言語。無い・空なら ja（ゲームの既定）。
        public static string LanguageOf(JsonObject root)
        {
            string s = J.Str(root, "language");
            return string.IsNullOrWhiteSpace(s) ? "ja" : s.Trim();
        }

        // 日本語の文をワールド言語へ。表に無い語・未知の言語は日本語のまま返す（ゲームの tr と同じく元の文で残す）。
        public static string Tr(string ja, string lang)
        {
            int i = Array.IndexOf(Languages, lang ?? "ja");
            return i >= 0 && Table.TryGetValue(ja, out var v) ? v[i] : ja;
        }

        // 同じ語のどれかの言語の訳か（言語の分からない値を照合するとき用）。
        public static bool IsAnyOf(string ja, string value)
            => value != null && Table.TryGetValue(ja, out var v) && v.Contains(value);

        public static string Wary(string lang) => Tr("警戒心がある", lang);
        public static string Stranger(string lang) => Tr("初対面", lang);
        public static string GameStart(string lang) => Tr("ゲーム開始", lang);

        public static string DefaultProfile(string lang) => Editor("profile", lang);
        public static string DefaultPersonality(string lang) => Editor("personality", lang);

        private static string Editor(string key, string lang)
        {
            int i = Array.IndexOf(Languages, lang ?? "ja");
            return EditorTable[key][i >= 0 ? i : 0];
        }

        // NPC の初期 relationship（player のみ・初対面）。affinityAsArray: affinity_text を文の配列で持つか
        //（ゲームは文字列と配列の両方を書く。新規作成時は文字列、作り直し時は配列のものがある）。
        public static JsonObject InitialRelationship(string lang, bool affinityAsArray) => new()
        {
            ["player"] = new JsonObject
            {
                ["affinity"] = 0,
                ["affinity_text"] = affinityAsArray ? new JsonArray { Wary(lang) } : Wary(lang),
                ["relationship"] = new JsonArray { Stranger(lang) },
                ["conversation_count"] = 0,
            },
        };
    }
}
