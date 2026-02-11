using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppCom.BBStudio.SRTeam.Manager;

namespace SRWYAccess
{
    /// <summary>
    /// Central localization for SRWYAccess mod.
    /// Detects game language via LocalizationManager and provides
    /// translated mod strings for screen reader announcements.
    /// </summary>
    public static class Loc
    {
        private static bool _initialized;
        private static string _currentLang = "en";

        // One dictionary per supported language
        private static readonly Dictionary<string, string> _zhCN = new();
        private static readonly Dictionary<string, string> _zhTW = new();
        private static readonly Dictionary<string, string> _en = new();
        private static readonly Dictionary<string, string> _ja = new();
        private static readonly Dictionary<string, string> _ko = new();

        /// <summary>
        /// Initialize localization. Call once when game is ready.
        /// </summary>
        public static void Initialize()
        {
            InitializeStrings();
            RefreshLanguage();
            _initialized = true;
        }

        /// <summary>
        /// Re-detect language. Prioritizes Windows system locale, falls back to
        /// game locale if system locale maps to English (may indicate unlocalized OS).
        /// </summary>
        public static void RefreshLanguage()
        {
            string systemLocale = GetSystemLocale();
            string gameLocale = GetGameLocale();

            DebugHelper.Write($"Loc: system locale={systemLocale}, game locale={gameLocale}");

            // Use system locale as primary source
            string lang = MapLocaleToLang(systemLocale);

            // If system locale mapped to English but game is in a CJK language,
            // prefer the game locale (user likely set game language deliberately)
            if (lang == "en" && !string.IsNullOrEmpty(gameLocale))
            {
                string gameLang = MapLocaleToLang(gameLocale);
                if (gameLang != "en")
                {
                    lang = gameLang;
                    DebugHelper.Write($"Loc: system=en but game={gameLang}, using game locale");
                }
            }

            _currentLang = lang;
            DebugHelper.Write($"Loc: selected language={_currentLang}");
        }

        private static string MapLocaleToLang(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return "en";

            if (locale.StartsWith("zh_CN", StringComparison.OrdinalIgnoreCase)
                || locale.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
                || locale.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
            {
                return "zh_CN";
            }
            if (locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return "zh_TW";
            }
            if (locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            {
                return "ja";
            }
            if (locale.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            {
                return "ko";
            }
            return "en";
        }

        private static string GetSystemLocale()
        {
            try
            {
                return CultureInfo.CurrentUICulture.Name;
            }
            catch { }
            return "en-US";
        }

        /// <summary>
        /// Get a localized string by key.
        /// </summary>
        public static string Get(string key)
        {
            if (!_initialized) Initialize();

            var dict = GetCurrentDictionary();

            if (dict.TryGetValue(key, out string value))
                return value;

            // Fallback: English
            if (_en.TryGetValue(key, out string enValue))
                return enValue;

            // Last fallback: key itself
            return key;
        }

        /// <summary>
        /// Get a localized string with placeholders ({0}, {1}, ...).
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                DebugHelper.Write($"Loc format error: key={key} args={args?.Length} template={template}");
                return template;
            }
        }

        private static string GetGameLocale()
        {
            try
            {
                var locMgr = LocalizationManager.instance;
                if ((object)locMgr != null)
                {
                    var localeId = locMgr.GetLocaleID();
                    return localeId.ToString();
                }
            }
            catch { }
            return "en_US";
        }

        private static Dictionary<string, string> GetCurrentDictionary()
        {
            switch (_currentLang)
            {
                case "zh_CN": return _zhCN;
                case "zh_TW": return _zhTW;
                case "ja": return _ja;
                case "ko": return _ko;
                default: return _en;
            }
        }

        private static void Add(string key, string en, string zhCN, string zhTW, string ja, string ko)
        {
            _en[key] = en;
            _zhCN[key] = zhCN;
            _zhTW[key] = zhTW;
            _ja[key] = ja;
            _ko[key] = ko;
        }

        private static void InitializeStrings()
        {
            // ===== GENERAL =====
            Add("mod_loaded",
                "SRWYAccess loaded",
                "SRWYAccess 已加载",
                "SRWYAccess 已載入",
                "SRWYAccess ロード完了",
                "SRWYAccess 로드됨");

            // ===== TITLE SCREEN =====
            Add("state_title",
                "Title screen",
                "标题画面",
                "標題畫面",
                "タイトル画面",
                "타이틀 화면");

            Add("state_title_press_any_key",
                "Title screen. Press any key",
                "标题画面。按任意键",
                "標題畫面。按任意鍵",
                "タイトル画面。何かキーを押してください",
                "타이틀 화면. 아무 키나 누르세요");

            // ===== GAME STATE TRANSITIONS =====

            Add("state_main_menu",
                "Main menu",
                "主菜单",
                "主選單",
                "メインメニュー",
                "메인 메뉴");

            Add("state_adventure",
                "Adventure mode",
                "冒险模式",
                "冒險模式",
                "アドベンチャーモード",
                "어드벤처 모드");

            Add("state_tactical",
                "Tactical battle",
                "战术战斗",
                "戰術戰鬥",
                "タクティカルバトル",
                "전술 전투");

            Add("state_battle",
                "Battle animation",
                "战斗动画",
                "戰鬥動畫",
                "バトルアニメーション",
                "전투 애니메이션");

            Add("state_strategy",
                "Strategy",
                "战略",
                "戰略",
                "ストラテジー",
                "전략");

            Add("state_game_clear",
                "Mission complete",
                "任务完成",
                "任務完成",
                "ミッションクリア",
                "미션 완료");

            // ===== MAIN MENU BUTTONS =====
            Add("menu_continue",
                "Continue",
                "继续",
                "繼續",
                "コンティニュー",
                "계속하기");

            Add("menu_start",
                "New Game",
                "新游戏",
                "新遊戲",
                "ニューゲーム",
                "새 게임");

            Add("menu_load",
                "Load",
                "读取",
                "讀取",
                "ロード",
                "불러오기");

            Add("menu_option",
                "Options",
                "选项",
                "選項",
                "オプション",
                "옵션");

            Add("menu_language",
                "Language",
                "语言",
                "語言",
                "言語",
                "언어");

            Add("menu_store",
                "Store",
                "商店",
                "商店",
                "ストア",
                "상점");

            Add("menu_quit",
                "Quit",
                "退出",
                "退出",
                "終了",
                "종료");

            // ===== DIALOG SYSTEM (H1) =====
            Add("dialog_message",
                "Dialog: {0}",
                "对话框：{0}",
                "對話框：{0}",
                "ダイアログ：{0}",
                "대화 상자: {0}");

            Add("dialog_yesno",
                "Dialog: {0} {1} or {2}",
                "对话框：{0} {1}或{2}",
                "對話框：{0} {1}或{2}",
                "ダイアログ：{0} {1}または{2}",
                "대화 상자: {0} {1} 또는 {2}");

            Add("dialog_select",
                "Selection: {0}",
                "选择：{0}",
                "選擇：{0}",
                "選択：{0}",
                "선택: {0}");

            Add("dialog_option",
                "Option {0} of {1}",
                "选项{0}，共{1}个",
                "選項{0}，共{1}個",
                "選択肢 {0}/{1}",
                "선택지 {0}/{1}");

            Add("dialog_option_named",
                "{0}, {1} of {2}",
                "{0}，第{1}项，共{2}项",
                "{0}，第{1}項，共{2}項",
                "{0}、{1}/{2}",
                "{0}, {1}/{2}");

            Add("dialog_yes",
                "Yes",
                "是",
                "是",
                "はい",
                "예");

            Add("dialog_no",
                "No",
                "否",
                "否",
                "いいえ",
                "아니오");

            // ===== TUTORIAL SYSTEM (G1) =====
            Add("tutorial_opened",
                "Tutorial:",
                "教程：",
                "教學：",
                "チュートリアル：",
                "튜토리얼:");

            Add("tutorial_page",
                "Page {0} of {1}",
                "第{0}页，共{1}页",
                "第{0}頁，共{1}頁",
                "ページ {0}/{1}",
                "페이지 {0}/{1}");

            Add("tutorial_prev_page",
                "Previous page",
                "上一页",
                "上一頁",
                "前のページ",
                "이전 페이지");

            Add("tutorial_next_page",
                "Next page",
                "下一页",
                "下一頁",
                "次のページ",
                "다음 페이지");

            Add("tutorial_skip",
                "Skip",
                "跳过",
                "跳過",
                "スキップ",
                "건너뛰기");

            // ===== ADVENTURE DIALOGUE (C1) =====
            Add("dialogue_line",
                "{0}: {1}",
                "{0}：{1}",
                "{0}：{1}",
                "{0}：{1}",
                "{0}: {1}");

            // ===== STRATEGY TOP MENU =====
            Add("state_strategy_menu",
                "Strategy menu",
                "战略菜单",
                "戰略選單",
                "ストラテジーメニュー",
                "전략 메뉴");

            Add("strategy_mission",
                "Mission",
                "任务",
                "任務",
                "ミッション",
                "미션");

            Add("strategy_unit",
                "Unit",
                "机体",
                "機體",
                "ユニット",
                "유닛");

            Add("strategy_update",
                "Upgrade",
                "升级",
                "升級",
                "アップデート",
                "업그레이드");

            Add("strategy_library",
                "Library",
                "图鉴",
                "圖鑑",
                "ライブラリ",
                "라이브러리");

            Add("strategy_option",
                "Options",
                "选项",
                "選項",
                "オプション",
                "옵션");

            // ===== OPTION MENU =====
            Add("state_option_menu",
                "Options menu",
                "选项菜单",
                "選項選單",
                "オプションメニュー",
                "옵션 메뉴");

            Add("option_save",
                "Save",
                "保存",
                "儲存",
                "セーブ",
                "저장");

            Add("option_load",
                "Load",
                "读取",
                "讀取",
                "ロード",
                "불러오기");

            Add("option_system",
                "System Options",
                "系统设置",
                "系統設定",
                "システムオプション",
                "시스템 옵션");

            Add("option_return_title",
                "Return to Title",
                "返回标题",
                "返回標題",
                "タイトルに戻る",
                "타이틀로 돌아가기");

            Add("option_exit",
                "Exit Game",
                "退出游戏",
                "退出遊戲",
                "ゲーム終了",
                "게임 종료");

            // ===== GAME OVER =====
            Add("state_game_over",
                "Game Over",
                "游戏结束",
                "遊戲結束",
                "ゲームオーバー",
                "게임 오버");

            Add("gameover_retry",
                "Retry",
                "重试",
                "重試",
                "リトライ",
                "재시도");

            Add("gameover_mainmenu",
                "Main Menu",
                "主菜单",
                "主選單",
                "メインメニュー",
                "메인 메뉴");

            // ===== SORTIE PREPARATION =====
            Add("state_sortie_prep",
                "Sortie preparation",
                "出击准备",
                "出擊準備",
                "出撃準備",
                "출격 준비");

            Add("sortie_finish",
                "Start Battle",
                "开始战斗",
                "開始戰鬥",
                "出撃開始",
                "출격 개시");

            Add("sortie_ship",
                "Ship Select",
                "选择母舰",
                "選擇母艦",
                "母艦選択",
                "모함 선택");

            Add("sortie_unit",
                "Unit Select",
                "选择机体",
                "選擇機體",
                "ユニット選択",
                "유닛 선택");

            Add("sortie_unit_manage",
                "Unit",
                "机体",
                "機體",
                "ユニット",
                "유닛");

            Add("sortie_save",
                "Save",
                "保存",
                "儲存",
                "セーブ",
                "저장");

            // ===== UNIT COMMAND =====
            Add("state_unit_command",
                "Unit command",
                "机体指令",
                "機體指令",
                "ユニットコマンド",
                "유닛 커맨드");

            Add("unit_robot_upgrade",
                "Robot Upgrade",
                "机体改造",
                "機體改造",
                "機体改造",
                "기체 개조");

            Add("unit_pilot_upgrade",
                "Pilot Upgrade",
                "驾驶员培养",
                "駕駛員培養",
                "パイロット養成",
                "파일럿 육성");

            Add("unit_assist",
                "Assist Link",
                "援助链接",
                "援助連結",
                "アシストリンク",
                "어시스트 링크");

            Add("unit_parts",
                "Parts",
                "部件",
                "零件",
                "パーツ",
                "파츠");

            Add("unit_change",
                "Change",
                "换乘",
                "換乘",
                "乗り換え",
                "탑승 변경");

            Add("unit_shop",
                "Shop",
                "商店",
                "商店",
                "ショップ",
                "상점");

            // ===== DATABASE TOP MENU =====
            Add("db_library",
                "Library", "图鉴", "圖鑑", "ライブラリ", "라이브러리");
            Add("db_sound",
                "Sound Select", "音乐鉴赏", "音樂鑑賞", "サウンドセレクト", "사운드 셀렉트");
            Add("db_movie",
                "Movie Collection", "影片收藏", "影片收藏", "ムービーコレクション", "무비 컬렉션");
            Add("db_record",
                "Play Record", "游玩记录", "遊玩記錄", "プレイレコード", "플레이 레코드");
            Add("db_search",
                "Search", "搜索", "搜尋", "サーチ", "검색");
            Add("db_tutorial",
                "Tutorial", "教程", "教學", "チュートリアル", "튜토리얼");

            // ===== BATTLE ANIMATION INFO ([ and ] keys) =====
            Add("battle_unit_info",
                "{0}: HP {1}, EN {2}",
                "{0}：HP {1}，EN {2}",
                "{0}：HP {1}，EN {2}",
                "{0}：HP {1}、EN {2}",
                "{0}: HP {1}, EN {2}");

            Add("battle_left",
                "Left",
                "左方",
                "左方",
                "左",
                "좌측");

            Add("battle_right",
                "Right",
                "右方",
                "右方",
                "右",
                "우측");

            // ===== BATTLE CHECK MENU =====
            Add("state_battle_check",
                "Battle check",
                "战斗确认",
                "戰鬥確認",
                "バトルチェック",
                "전투 확인");

            Add("battle_battle",
                "Battle",
                "战斗",
                "戰鬥",
                "戦闘",
                "전투");

            Add("battle_select",
                "Select",
                "选择",
                "選擇",
                "セレクト",
                "선택");

            Add("battle_spirit",
                "Spirit",
                "精神",
                "精神",
                "精神",
                "정신");

            Add("battle_assistlink",
                "Assist Link",
                "援助链接",
                "援助連結",
                "アシストリンク",
                "어시스트 링크");

            Add("battle_support",
                "Support",
                "支援",
                "支援",
                "支援",
                "지원");

            Add("battle_detail",
                "Detail",
                "详情",
                "詳情",
                "詳細",
                "상세");

            Add("battle_counter",
                "Counter",
                "反击",
                "反擊",
                "反撃",
                "반격");

            Add("battle_guard",
                "Guard",
                "防御",
                "防禦",
                "ガード",
                "가드");

            Add("battle_avoid",
                "Evade",
                "回避",
                "迴避",
                "回避",
                "회피");

            // ===== BATTLE CHECK COMBAT PREDICTIONS =====
            Add("battle_hit_rate",
                "Hit%",
                "命中率",
                "命中率",
                "命中率",
                "명중률");

            Add("battle_damage",
                "Damage",
                "伤害",
                "傷害",
                "ダメージ",
                "데미지");

            Add("battle_critical",
                "Crit%",
                "暴击率",
                "暴擊率",
                "クリティカル率",
                "크리티컬률");

            Add("battle_attack_power",
                "ATK",
                "攻击力",
                "攻擊力",
                "攻撃力",
                "공격력");

            Add("battle_terrain",
                "Terrain",
                "地形",
                "地形",
                "地形",
                "지형");

            // ===== TACTICAL COMMAND BUTTONS (btnType fallback) =====
            Add("cmd_persuade",
                "Persuade", "说服", "說服", "説得", "설득");
            Add("cmd_move",
                "Move", "移动", "移動", "移動", "이동");
            Add("cmd_attack",
                "Attack", "攻击", "攻擊", "攻撃", "공격");
            Add("cmd_split",
                "Spirit", "精神", "精神", "精神", "정신");
            Add("cmd_assistlink",
                "Assist Link", "援助链接", "援助連結", "アシストリンク", "어시스트 링크");
            Add("cmd_landformaction",
                "Terrain", "地形", "地形", "地形", "지형");
            Add("cmd_deformation",
                "Transform", "变形", "變形", "変形", "변형");
            Add("cmd_collect",
                "Collect", "回收", "回收", "回収", "회수");
            Add("cmd_departure",
                "Sortie", "出击", "出擊", "出撃", "출격");
            Add("cmd_special",
                "Special", "特殊", "特殊", "特殊", "특수");
            Add("cmd_special2",
                "Special 2", "特殊2", "特殊2", "特殊2", "특수2");
            Add("cmd_fix",
                "Repair", "修理", "修理", "修理", "수리");
            Add("cmd_supply",
                "Supply", "补给", "補給", "補給", "보급");
            Add("cmd_parts",
                "Parts", "部件", "零件", "パーツ", "파츠");
            Add("cmd_rest",
                "Wait", "待机", "待機", "待機", "대기");
            Add("cmd_phaseend",
                "End Turn", "结束回合", "結束回合", "ターン終了", "턴 종료");
            Add("cmd_mission",
                "Mission", "作战目的", "作戰目的", "作戦目的", "작전 목적");
            Add("cmd_tacticalsituation",
                "Situation", "战况", "戰況", "戦況", "전황");
            Add("cmd_unitlist",
                "Unit List", "部队表", "部隊表", "部隊表", "부대표");
            Add("cmd_auto",
                "Auto", "自动", "自動", "自動", "자동");
            Add("cmd_search",
                "Search", "搜索", "搜尋", "索敵", "수색");
            Add("cmd_system",
                "System", "系统", "系統", "システム", "시스템");
            Add("cmd_save",
                "Save", "存档", "存檔", "セーブ", "저장");

            // ===== MAP MENU =====
            Add("map_menu",
                "Map Menu", "地图菜单", "地圖選單", "マップメニュー", "맵 메뉴");

            // ===== MAP CURSOR =====
            Add("map_cursor",
                "{0},{1}", "{0},{1}", "{0},{1}", "{0},{1}", "{0},{1}");
            Add("map_cursor_unit",
                "{0},{1} {2}", "{0},{1} {2}", "{0},{1} {2}", "{0},{1} {2}", "{0},{1} {2}");

            // ===== SCREEN NAMES (announced when entering a new UI screen) =====

            // Tactical scene screens
            Add("screen_weaponlisthandler",
                "Weapon List", "武器列表", "武器列表", "武器リスト", "무기 목록");
            Add("screen_battlecheckmenuhandler",
                "Battle Check", "战斗确认", "戰鬥確認", "バトルチェック", "전투 확인");
            Add("screen_tacticalpartspirituihandler",
                "Spirit Commands", "精神指令", "精神指令", "精神コマンド", "정신 커맨드");
            Add("screen_missionuihandler",
                "Mission", "作战目的", "作戰目的", "作戦目的", "작전 목적");
            Add("screen_searchunittopuihandler",
                "Search", "搜索", "搜尋", "索敵", "수색");
            Add("screen_robotlistuihandler",
                "Unit List", "部队表", "部隊表", "部隊表", "부대표");
            Add("screen_saveloaduihandler",
                "Save/Load", "存档/读取", "存檔/讀取", "セーブ/ロード", "저장/불러오기");
            Add("screen_unitcommanduihandler",
                "Unit Command", "机体指令", "機體指令", "ユニットコマンド", "유닛 커맨드");
            Add("screen_tacticalotherscommanduihandler",
                "Other Commands", "其他指令", "其他指令", "その他コマンド", "기타 커맨드");
            Add("screen_actionresultuihandler",
                "Battle Result", "战斗结果", "戰鬥結果", "戦闘結果", "전투 결과");
            Add("screen_lvupuihandler",
                "Level Up", "升级", "升級", "レベルアップ", "레벨 업");
            Add("screen_acebonusuihandler",
                "Ace Bonus", "王牌奖励", "王牌獎勵", "エースボーナス", "에이스 보너스");

            // Strategy / intermission screens
            Add("screen_strategytopuihandler",
                "Strategy Menu", "战略菜单", "戰略選單", "ストラテジーメニュー", "전략 메뉴");
            Add("screen_statusuihandler",
                "Status", "状态", "狀態", "ステータス", "스테이터스");
            Add("screen_partsequipuihandler",
                "Parts Equip", "装备零件", "裝備零件", "パーツ装備", "파츠 장비");
            Add("screen_shopuihandler",
                "Shop", "商店", "商店", "ショップ", "상점");
            Add("screen_pilotlistuihandler",
                "Pilot List", "驾驶员列表", "駕駛員列表", "パイロットリスト", "파일럿 목록");
            Add("screen_pilottraininguihandler",
                "Pilot Training", "驾驶员培养", "駕駛員培養", "パイロット養成", "파일럿 육성");
            Add("screen_sortiepreparationtopuihandler",
                "Sortie Preparation", "出击准备", "出擊準備", "出撃準備", "출격 준비");
            Add("screen_optionmenuuihandler",
                "Options Menu", "选项菜单", "選項選單", "オプションメニュー", "옵션 메뉴");
            Add("screen_databasetopuihandler",
                "Database", "资料库", "資料庫", "データベース", "데이터베이스");
            Add("screen_librarytopuihandler",
                "Library", "图鉴", "圖鑑", "ライブラリ", "라이브러리");
            Add("screen_gameoveruihandler",
                "Game Over", "游戏结束", "遊戲結束", "ゲームオーバー", "게임 오버");
            Add("screen_difficultyuihandler",
                "Difficulty Select", "难度选择", "難度選擇", "難易度選択", "난이도 선택");

            // Other screens
            Add("screen_conversionuihandler",
                "Robot Upgrade", "机体改造", "機體改造", "機体改造", "기체 개조");
            Add("screen_encyclopediauihandler",
                "Encyclopedia", "百科", "百科", "百科事典", "백과사전");
            Add("screen_backloguihandler",
                "Message Log", "讯息记录", "訊息記錄", "メッセージログ", "메시지 로그");
            Add("screen_customrobotuihandler",
                "Robot Customization", "机体改造", "機體改造", "機体カスタマイズ", "기체 커스터마이즈");
            Add("screen_libraryrobotdetailuihandler",
                "Robot Detail", "机体详情", "機體詳情", "ロボット詳細", "로봇 상세");
            Add("screen_librarychardetailuihandler",
                "Character Detail", "角色详情", "角色詳情", "キャラクター詳細", "캐릭터 상세");
            Add("screen_updateuihandler",
                "Upgrade", "强化", "強化", "アップグレード", "업그레이드");
            Add("screen_rankupanimationhandler",
                "Rank Up", "升阶", "升階", "ランクアップ", "랭크 업");
            Add("screen_bonusuihandler",
                "Bonus", "奖励", "獎勵", "ボーナス", "보너스");
            Add("screen_creditwindowuihandler",
                "Credits", "制作人员", "製作人員", "クレジット", "크레딧");

            // ===== BATTLE RESULT =====
            Add("result_battle",
                "Battle result: {0}, Level {1}, EXP +{2}, Score +{3}, Credits +{4}",
                "战斗结果：{0}，等级 {1}，经验 +{2}，分数 +{3}，金币 +{4}",
                "戰鬥結果：{0}，等級 {1}，經驗 +{2}，分數 +{3}，金幣 +{4}",
                "戦闘結果：{0}、レベル {1}、EXP +{2}、スコア +{3}、クレジット +{4}",
                "전투 결과: {0}, 레벨 {1}, EXP +{2}, 점수 +{3}, 크레딧 +{4}");

            Add("result_level_up",
                "Level up! {0}: Level {1} to {2}",
                "升级！{0}：等级 {1} → {2}",
                "升級！{0}：等級 {1} → {2}",
                "レベルアップ！{0}：レベル {1} → {2}",
                "레벨 업! {0}: 레벨 {1} → {2}");

            Add("result_ace_bonus",
                "Ace bonus! {0}: {1}",
                "王牌奖励！{0}：{1}",
                "王牌獎勵！{0}：{1}",
                "エースボーナス！{0}：{1}",
                "에이스 보너스! {0}: {1}");

            // ===== BATTLE EXTRA INFO =====
            Add("battle_bullet",
                "Ammo",
                "弹药",
                "彈藥",
                "弾薬",
                "탄약");

            // ===== SCREEN REVIEW STRUCTURED ITEMS =====
            Add("review_level",
                "Level {0}",
                "等级 {0}",
                "等級 {0}",
                "レベル {0}",
                "레벨 {0}");

            Add("review_score",
                "Score +{0}",
                "分数 +{0}",
                "分數 +{0}",
                "スコア +{0}",
                "점수 +{0}");

            Add("review_credits",
                "Credits +{0}",
                "金币 +{0}",
                "金幣 +{0}",
                "クレジット +{0}",
                "크레딧 +{0}");

            Add("review_lvup_level",
                "Level {0} → {1}",
                "等级 {0} → {1}",
                "等級 {0} → {1}",
                "レベル {0} → {1}",
                "레벨 {0} → {1}");

            Add("review_total_price",
                "Total: {0}",
                "合计：{0}",
                "合計：{0}",
                "合計：{0}",
                "합계: {0}");

            Add("review_remaining_credits",
                "Remaining: {0}",
                "余额：{0}",
                "餘額：{0}",
                "残高：{0}",
                "잔액: {0}");

            // ===== STAT LABELS =====
            Add("stat_melee",
                "Melee",
                "格斗",
                "格鬥",
                "格闘",
                "격투");

            Add("stat_ranged",
                "Ranged",
                "射击",
                "射擊",
                "射撃",
                "사격");

            Add("stat_defend",
                "Defense",
                "防御",
                "防禦",
                "防御",
                "방어");

            Add("stat_hit",
                "Hit",
                "命中",
                "命中",
                "命中",
                "명중");

            Add("stat_evade",
                "Evade",
                "回避",
                "迴避",
                "回避",
                "회피");

            Add("stat_skill",
                "Skill",
                "技量",
                "技量",
                "技量",
                "기량");

            Add("stat_armor",
                "Armor",
                "装甲",
                "裝甲",
                "装甲",
                "장갑");

            Add("stat_mobility",
                "Mobility",
                "机动",
                "機動",
                "機動",
                "기동");

            Add("stat_move",
                "Move",
                "移动",
                "移動",
                "移動",
                "이동");

            Add("stat_sight",
                "Sight",
                "视野",
                "視野",
                "射程",
                "시야");

            // ===== MAP TERRAIN =====
            Add("map_terrain_block",
                "Impassable",
                "不可通行",
                "不可通行",
                "進入不可",
                "통행 불가");

            // ===== ADDITIONAL SCREEN NAMES =====
            Add("screen_characterselectionuihandler",
                "Character Select",
                "角色选择",
                "角色選擇",
                "キャラクター選択",
                "캐릭터 선택");

            Add("screen_weaponlistitemhandler",
                "Weapon List",
                "武器列表",
                "武器列表",
                "武器リスト",
                "무기 목록");

            // ===== WEAPON LIST INFO =====
            Add("weapon_morale",
                "Morale",
                "气力",
                "氣力",
                "気力",
                "기력");

            Add("weapon_required_skill",
                "Required:",
                "必要技能：",
                "必要技能：",
                "必要技能：",
                "필요 기능:");

            // ===== SPIRIT INFO =====
            Add("spirit_cost",
                "Cost:",
                "消耗：",
                "消耗：",
                "消費：",
                "소비:");

            Add("spirit_disabled",
                "(unavailable)",
                "(不可用)",
                "(不可用)",
                "(使用不可)",
                "(사용불가)");

            // ===== LEVEL UP NEW ABILITIES =====
            Add("lvup_new_spirit",
                "New spirit:",
                "习得精神：",
                "習得精神：",
                "精神習得：",
                "정신 습득:");

            Add("lvup_new_skill",
                "New skill:",
                "习得技能：",
                "習得技能：",
                "スキル習得：",
                "스킬 습득:");

            // ===== MISSION OBJECTIVES =====
            Add("mission_title_label",
                "Mission:",
                "作战目的：",
                "作戰目的：",
                "作戦目的：",
                "작전 목적:");

            Add("mission_win",
                "Win conditions:",
                "胜利条件：",
                "勝利條件：",
                "勝利条件：",
                "승리 조건:");

            Add("mission_lose",
                "Defeat conditions:",
                "败北条件：",
                "敗北條件：",
                "敗北条件：",
                "패배 조건:");

            // ===== TACTICAL SITUATION =====
            Add("situation_player_units",
                "Player units:",
                "我方机体：",
                "我方機體：",
                "味方ユニット：",
                "아군 유닛:");

            Add("situation_enemy_units",
                "Enemy units:",
                "敌方机体：",
                "敵方機體：",
                "敵ユニット：",
                "적 유닛:");

            Add("situation_player_kills",
                "Player kills:",
                "我方击坠：",
                "我方擊墜：",
                "味方撃墜数：",
                "아군 격추:");

            Add("situation_enemy_kills",
                "Enemy kills:",
                "敌方击坠：",
                "敵方擊墜：",
                "敵撃墜数：",
                "적 격추:");

            Add("situation_credits",
                "Credits gained:",
                "获得资金：",
                "獲得資金：",
                "取得資金：",
                "획득 자금:");

            Add("situation_parts",
                "Power parts:",
                "获得强化零件：",
                "獲得強化零件：",
                "取得強化パーツ：",
                "강화 파츠:");

            Add("situation_skills",
                "Skill programs:",
                "获得特殊技能：",
                "獲得特殊技能：",
                "取得スキルプログラム：",
                "스킬 프로그램:");

            // ===== SCREEN REVIEW (R / [ / ] keys) =====
            Add("review_no_info",
                "No information available",
                "没有可用信息",
                "沒有可用資訊",
                "情報がありません",
                "사용 가능한 정보 없음");

            Add("review_item",
                "{0}/{1}: {2}",
                "{0}/{1}：{2}",
                "{0}/{1}：{2}",
                "{0}/{1}：{2}",
                "{0}/{1}: {2}");

            Add("review_separator",
                ". ",
                "。",
                "。",
                "。",
                ". ");

            // ===== ASSIST LINK =====
            Add("screen_assistlinkmanager",
                "Assist Link",
                "援助链接",
                "援助連結",
                "アシストリンク",
                "어시스트 링크");

            Add("assistlink_level",
                "Lv.{0}",
                "Lv.{0}",
                "Lv.{0}",
                "Lv.{0}",
                "Lv.{0}");

            Add("assistlink_registered",
                "Registered",
                "已装备",
                "已裝備",
                "装備済",
                "장착됨");

            Add("assistlink_command_effect",
                "Command: {0}",
                "指令效果：{0}",
                "指令效果：{0}",
                "コマンド効果：{0}",
                "커맨드 효과: {0}");

            Add("assistlink_passive_effect",
                "Passive: {0}",
                "被动效果：{0}",
                "被動效果：{0}",
                "パッシブ効果：{0}",
                "패시브 효과: {0}");

            Add("assistlink_lv_command",
                "Lv Enhanced Command: {0}",
                "升级指令效果：{0}",
                "升級指令效果：{0}",
                "Lv強化コマンド効果：{0}",
                "Lv 강화 커맨드: {0}");

            Add("assistlink_lv_passive",
                "Lv Enhanced Passive: {0}",
                "升级被动效果：{0}",
                "升級被動效果：{0}",
                "Lv強化パッシブ効果：{0}",
                "Lv 강화 패시브: {0}");

            Add("assistlink_target",
                "Target: {0}",
                "对象：{0}",
                "對象：{0}",
                "対象：{0}",
                "대상: {0}");

            Add("assistlink_duration",
                "Duration: {0}",
                "持续：{0}",
                "持續：{0}",
                "持続：{0}",
                "지속: {0}");

            Add("assistlink_selection_count",
                "Equipped: {0}",
                "已装备：{0}",
                "已裝備：{0}",
                "装備数：{0}",
                "장착: {0}");

            // ===== MORALE =====
            Add("stat_morale",
                "Morale",
                "气力",
                "氣力",
                "気力",
                "기력");

            // ===== ROBOT SIZE =====
            Add("stat_size",
                "Size",
                "尺寸",
                "尺寸",
                "サイズ",
                "사이즈");

            Add("size_SS", "SS", "SS", "SS", "SS", "SS");
            Add("size_S", "S", "S", "S", "S", "S");
            Add("size_M", "M", "M", "M", "M", "M");
            Add("size_L", "L", "L", "L", "L", "L");
            Add("size_L2", "2L", "2L", "2L", "2L", "2L");
            Add("size_L3", "3L", "3L", "3L", "3L", "3L");
            Add("size_Infinity", "???", "???", "???", "???", "???");

            // ===== TERRAIN APTITUDE =====
            Add("stat_terrain",
                "Terrain",
                "地形适性",
                "地形適性",
                "地形適応",
                "지형적응");
            Add("terrain_sky",
                "Air",
                "空",
                "空",
                "空",
                "공");
            Add("terrain_ground",
                "Land",
                "陆",
                "陸",
                "陸",
                "육");
            Add("terrain_water",
                "Sea",
                "海",
                "海",
                "海",
                "해");
            Add("terrain_space",
                "Space",
                "宇",
                "宇",
                "宇",
                "우");

            // ===== SUPPORT COUNTS =====
            Add("stat_support_attack",
                "Support ATK",
                "援攻",
                "援攻",
                "援護攻撃",
                "원호공격");

            Add("stat_support_defense",
                "Support DEF",
                "援防",
                "援防",
                "援護防御",
                "원호방어");

            // ===== WEAPON EXTRA INFO =====
            Add("weapon_ammo",
                "Ammo",
                "弹药",
                "彈藥",
                "弾数",
                "탄약");

            Add("weapon_crit",
                "Crit",
                "暴击",
                "暴擊",
                "CRT",
                "크리티컬");

            Add("weapon_morale_req",
                "Req. Morale",
                "必要气力",
                "必要氣力",
                "必要気力",
                "필요 기력");

            // ===== BATTLE CHECK AUTO-READ =====
            Add("battle_vs",
                "vs",
                "对",
                "對",
                "vs",
                "vs");

            Add("battle_prediction",
                "{0}: {1} Hit {2}% Dmg {3} Crit {4}%",
                "{0}: {1} 命中{2}% 伤害{3} 暴击{4}%",
                "{0}: {1} 命中{2}% 傷害{3} 暴擊{4}%",
                "{0}: {1} 命中{2}% ダメージ{3} CRT{4}%",
                "{0}: {1} 명중{2}% 데미지{3} 크리{4}%");

            // ===== WEAPON DETAIL AUTO-READ =====
            Add("weapon_power",
                "Power",
                "攻击力",
                "攻擊力",
                "攻撃力",
                "공격력");

            Add("weapon_range",
                "Range",
                "射程",
                "射程",
                "射程",
                "사거리");

            Add("weapon_en_cost",
                "EN",
                "EN",
                "EN",
                "EN",
                "EN");

            Add("weapon_detail_auto",
                "{0}, {1}, {2}",
                "{0}, {1}, {2}",
                "{0}, {1}, {2}",
                "{0}, {1}, {2}",
                "{0}, {1}, {2}");

            // ===== ADDITIONAL STATE ANNOUNCEMENTS =====
            Add("state_opening_demo",
                "Opening demo",
                "开场动画",
                "開場動畫",
                "オープニングデモ",
                "오프닝 데모");

            Add("state_enemy_turn",
                "Enemy turn",
                "敌方回合",
                "敵方回合",
                "敵フェイズ",
                "적 페이즈");

            Add("state_auto_battle",
                "Auto battle",
                "自动战斗",
                "自動戰鬥",
                "オートバトル",
                "자동 전투");

            Add("state_command_menu",
                "Command menu",
                "指令菜单",
                "指令選單",
                "コマンドメニュー",
                "커맨드 메뉴");

            // ===== ADDITIONAL SCREEN NAMES =====
            Add("screen_handoveruihandler",
                "Handover",
                "引继",
                "引繼",
                "引き継ぎ",
                "인계");

            Add("screen_transferuihandler",
                "Transfer",
                "换乘",
                "換乘",
                "乗り換え",
                "탑승 변경");

            Add("screen_selectpartsuihandler",
                "Parts Select",
                "选择零件",
                "選擇零件",
                "パーツ選択",
                "파츠 선택");

            Add("screen_missionchartuihandler",
                "Mission Chart",
                "作战地图",
                "作戰地圖",
                "ミッションチャート",
                "미션 차트");

            Add("screen_survivalmissionresultuihandler",
                "Survival Result",
                "生存结果",
                "生存結果",
                "サバイバル結果",
                "서바이벌 결과");

            Add("screen_selectspecialcommanduihandler",
                "Special Command",
                "特殊指令",
                "特殊指令",
                "特殊コマンド",
                "특수 커맨드");

            // ===== PARTS EQUIP =====
            Add("parts_count",
                "Remaining: {0}/{1}",
                "剩余：{0}/{1}",
                "剩餘：{0}/{1}",
                "残り：{0}/{1}",
                "잔여: {0}/{1}");

            Add("parts_description",
                "Effect: {0}",
                "效果：{0}",
                "效果：{0}",
                "効果：{0}",
                "효과: {0}");

            // ===== DIFFICULTY SELECTION =====
            Add("difficulty_description",
                "{0}: {1}",
                "{0}：{1}",
                "{0}：{1}",
                "{0}：{1}",
                "{0}: {1}");
        }
    }
}
