using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppCom.BBStudio.SRTeam.Common;
using Il2CppCom.BBStudio.SRTeam.Manager;
using Il2CppCosmos;

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
        private static int _recheckCounter; // throttle: only re-check every N frames

        /// <summary>Current language code for diagnostics.</summary>
        public static string CurrentLang => _currentLang;

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
        /// Call from main loop. Periodically re-checks language from game managers.
        /// The game may change locale after mod init (e.g. loading save data),
        /// so we keep polling every ~1 second.
        /// </summary>
        public static void TryConfirmLanguage()
        {
            _recheckCounter++;
            if (_recheckCounter < 60) return; // ~1 second at 60fps
            _recheckCounter = 0;
            RefreshLanguage();
        }

        /// <summary>
        /// Re-detect language. LocalizationManager is the primary source (tracks
        /// the user's chosen display language). KpiManager is the fallback (tracks
        /// game SKU/region, which may differ from display language).
        /// </summary>
        public static void RefreshLanguage()
        {
            // Source 1: LocalizationManager.GetLocaleID() — tracks actual display language
            string lang = GetLocalizationManagerLanguage();

            // Source 2: KpiManager.gameLanguage — fallback (SKU/region language)
            if (lang == null)
                lang = GetKpiManagerLanguage();

            if (lang != null)
            {
                if (lang != _currentLang)
                {
                    DebugHelper.Write($"Loc: language changed {_currentLang} → {lang}");
                    _currentLang = lang;
                }
            }
            else
            {
                // Game managers not available yet — use system locale as temporary default
                string systemLocale = GetSystemLocale();
                string sysLang = MapLocaleToLang(systemLocale);
                if (sysLang != _currentLang)
                {
                    _currentLang = sysLang;
                    DebugHelper.Write($"Loc: game data not ready, temporary system locale → {_currentLang}");
                }
            }
        }

        private static string MapLocaleToLang(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return "en";

            // GameLanguage enum names from SaveLoadManager
            if (locale.Equals("SimpleChinese", StringComparison.OrdinalIgnoreCase))
                return "zh_CN";
            if (locale.Equals("TraditionalChinese", StringComparison.OrdinalIgnoreCase))
                return "zh_TW";
            if (locale.Equals("Japanese", StringComparison.OrdinalIgnoreCase))
                return "ja";
            if (locale.Equals("Korean", StringComparison.OrdinalIgnoreCase))
                return "ko";
            if (locale.Equals("English", StringComparison.OrdinalIgnoreCase))
                return "en";

            // Standard locale codes (fallback for system locale / Unity SelectedLocale)
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

        /// <summary>
        /// Try KpiManager.gameLanguage — returns lang code or null.
        /// </summary>
        private static string GetKpiManagerLanguage()
        {
            try
            {
                var kpi = KpiManager.instance;
                if ((object)kpi == null) return null;

                var lang = kpi.gameLanguage;
                string result;
                switch (lang)
                {
                    case GameLanguage.English: result = "en"; break;
                    case GameLanguage.SimpleChinese: result = "zh_CN"; break;
                    case GameLanguage.TraditionalChinese: result = "zh_TW"; break;
                    case GameLanguage.Japanese: result = "ja"; break;
                    case GameLanguage.Korean: result = "ko"; break;
                    default: result = "en"; break;
                }
                DebugHelper.Write($"Loc: KpiManager.gameLanguage={lang} → {result}");
                return result;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Loc: KpiManager failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Try LocalizationManager.GetLocaleID() — returns lang code or null.
        /// </summary>
        private static string GetLocalizationManagerLanguage()
        {
            try
            {
                var lm = LocalizationManager.instance;
                if ((object)lm == null) return null;

                var localeId = lm.GetLocaleID();
                string result;
                switch (localeId)
                {
                    case LocalizationManager.LocaleID.en_US: result = "en"; break;
                    case LocalizationManager.LocaleID.zh_CN: result = "zh_CN"; break;
                    case LocalizationManager.LocaleID.zh_TW: result = "zh_TW"; break;
                    case LocalizationManager.LocaleID.zh_HK: result = "zh_TW"; break;
                    case LocalizationManager.LocaleID.ja_JP: result = "ja"; break;
                    case LocalizationManager.LocaleID.ko_KR: result = "ko"; break;
                    default: result = "en"; break;
                }
                DebugHelper.Write($"Loc: LocalizationManager.GetLocaleID()={localeId} → {result}");
                return result;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"Loc: LocalizationManager failed: {ex.Message}");
                return null;
            }
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

            Add("mod_enabled",
                "Mod enabled",
                "模组已启用",
                "模組已啟用",
                "モッド有効",
                "모드 활성화");

            Add("mod_disabled",
                "Mod disabled",
                "模组已禁用",
                "模組已停用",
                "モッド無効",
                "모드 비활성화");

            Add("mod_reset",
                "Mod state reset",
                "模组状态已重置",
                "模組狀態已重置",
                "モッド状態リセット",
                "모드 상태 초기화");

            Add("mod_critical_error",
                "Mod error occurred",
                "模组发生错误",
                "模組發生錯誤",
                "モッドエラーが発生しました",
                "모드 오류 발생");

            Add("handler_disabled",
                "Feature disabled due to errors",
                "功能因错误已禁用",
                "功能因錯誤已停用",
                "エラーにより機能が無効化されました",
                "오류로 인해 기능 비활성화됨");

            Add("audio_cues_on",
                "Audio cues on",
                "音效提示已开启",
                "音效提示已開啟",
                "オーディオキューON",
                "오디오 큐 켜짐");

            Add("audio_cues_off",
                "Audio cues off",
                "音效提示已关闭",
                "音效提示已關閉",
                "オーディオキューOFF",
                "오디오 큐 꺼짐");

            Add("battle_anim_label",
                "Battle animation: ",
                "战斗动画：",
                "戰鬥動畫：",
                "戦闘アニメ：",
                "전투 애니메이션: ");

            Add("battle_anim_on",
                "Battle animation: On",
                "战斗动画：开启",
                "戰鬥動畫：開啟",
                "戦闘アニメ：ON",
                "전투 애니메이션: 켜짐");

            Add("battle_anim_face",
                "Battle animation: Face only",
                "战斗动画：仅肖像",
                "戰鬥動畫：僅肖像",
                "戦闘アニメ：顔のみ",
                "전투 애니메이션: 얼굴만");

            Add("battle_anim_off",
                "Battle animation: Off",
                "战斗动画：关闭",
                "戰鬥動畫：關閉",
                "戦闘アニメ：OFF",
                "전투 애니메이션: 꺼짐");

            // Short direction names for path prediction (東3北4 style)
            Add("dir_north", "N", "北", "北", "北", "북");
            Add("dir_south", "S", "南", "南", "南", "남");
            Add("dir_east", "E", "东", "東", "東", "동");
            Add("dir_west", "W", "西", "西", "西", "서");
            Add("path_same_position",
                "Same position",
                "相同位置",
                "相同位置",
                "同じ位置",
                "같은 위치");
            Add("path_no_unit",
                "No unit selected",
                "未选择机体",
                "未選擇機體",
                "ユニット未選択",
                "유닛 미선택");

            Add("sort_type",
                "Sort: {0}",
                "排序：{0}",
                "排序：{0}",
                "ソート：{0}",
                "정렬: {0}");

            Add("filter_type",
                "Filter: {0}",
                "筛选：{0}",
                "篩選：{0}",
                "フィルター：{0}",
                "필터: {0}");

            Add("safe_mode_enabled",
                "Mod entered safe mode. Only basic features available.",
                "模组进入安全模式，仅提供基本功能",
                "模組進入安全模式，僅提供基本功能",
                "モッドがセーフモードに入りました。基本機能のみ利用可能です",
                "모드가 안전 모드로 전환됨. 기본 기능만 사용 가능");

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

            // ===== MISSION SELECTION DETAIL =====
            Add("mission_location",
                "Location",
                "地点",
                "地點",
                "場所",
                "위치");

            Add("mission_recommend_rank",
                "Recommended rank",
                "推荐等级",
                "推薦等級",
                "推奨ランク",
                "권장 랭크");

            // ===== SORTIE PREPARATION =====
            Add("sortie_unit_count",
                "Units: {0}",
                "出击机体：{0}",
                "出擊機體：{0}",
                "出撃ユニット：{0}",
                "출격 유닛: {0}");

            Add("sortie_ship_count",
                "Ships: {0}",
                "出击舰船：{0}",
                "出擊艦船：{0}",
                "出撃艦船：{0}",
                "출격 함선: {0}");

            Add("sortie_difficulty",
                "Difficulty: {0}",
                "难度：{0}",
                "難度：{0}",
                "難易度：{0}",
                "난이도: {0}");

            Add("sortie_info_not_available",
                "Sortie info not available",
                "出击信息不可用",
                "出擊資訊不可用",
                "出撃情報が利用できません",
                "출격 정보 사용 불가");

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

            // ===== SYSTEM OPTION PAGES =====
            Add("option_page_game",
                "Game Settings",
                "游戏设置",
                "遊戲設定",
                "ゲーム設定",
                "게임 설정");

            Add("option_page_sound",
                "Sound Settings",
                "声音设置",
                "聲音設定",
                "サウンド設定",
                "사운드 설정");

            Add("option_page_screen",
                "Screen Settings",
                "画面设置",
                "畫面設定",
                "画面設定",
                "화면 설정");

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

            Add("shop_price",
                "Price:",
                "价格：",
                "價格：",
                "価格：",
                "가격:");
            Add("shop_owned",
                "Owned:",
                "持有：",
                "持有：",
                "所持：",
                "보유:");
            Add("shop_buy_count",
                "Buying:",
                "购买：",
                "購買：",
                "購入：",
                "구매:");

            // ===== PILOT TRAINING =====
            Add("training_tab_skill",
                "Skills Tab",
                "技能页",
                "技能頁",
                "スキルタブ",
                "스킬 탭");
            Add("training_tab_param",
                "Parameters Tab",
                "能力值页",
                "能力值頁",
                "能力値タブ",
                "능력치 탭");
            Add("training_learning",
                "Learning:",
                "学习：",
                "學習：",
                "習得：",
                "학습:");
            Add("training_upgrading",
                "Upgrading:",
                "强化：",
                "強化：",
                "強化：",
                "강화:");
            Add("training_cost",
                "Cost:",
                "费用：",
                "費用：",
                "費用：",
                "비용:");

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

            Add("stat_accuracy",
                "Accuracy",
                "照准",
                "照準",
                "照準",
                "조준");

            Add("stat_score",
                "Kills",
                "击坠数",
                "擊墜數",
                "撃墜数",
                "격추수");

            Add("stat_move_max",
                "Max movement",
                "最大移动范围",
                "最大移動範圍",
                "最大移動",
                "최대 이동");

            Add("stat_move_remaining",
                "Remaining steps",
                "剩余步数",
                "剩餘步數",
                "残り移動",
                "남은 이동");

            Add("stat_attack_range",
                "Attack range",
                "攻击范围",
                "攻擊範圍",
                "攻撃射程",
                "공격 사거리");

            Add("range_no_weapons",
                "No weapons available",
                "没有可用武器",
                "沒有可用武器",
                "使用可能な武器なし",
                "사용 가능한 무기 없음");

            Add("range_after_move",
                "After move",
                "移动后",
                "移動後",
                "移動後",
                "이동 후");

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

            // ===== WEAPON ATTRIBUTES =====
            Add("weapon_melee",
                "Melee",
                "格斗",
                "格鬥",
                "格闘",
                "격투");

            Add("weapon_shooting",
                "Ranged",
                "射击",
                "射擊",
                "射撃",
                "사격");

            Add("weapon_beam",
                "Beam",
                "光束",
                "光束",
                "ビーム",
                "빔");

            Add("weapon_entity",
                "Physical",
                "实弹",
                "實彈",
                "実弾",
                "실탄");

            Add("weapon_barrier_pen",
                "Barrier Pen.",
                "屏障贯通",
                "屏障貫通",
                "バリア貫通",
                "배리어관통");

            Add("weapon_ignore_size",
                "Size Ignore",
                "尺寸无视",
                "尺寸無視",
                "サイズ無視",
                "사이즈무시");

            Add("weapon_post_move",
                "P",
                "移",
                "移",
                "移",
                "이");

            Add("weapon_counter",
                "Counter",
                "反击可",
                "反擊可",
                "反撃可",
                "반격가능");

            Add("weapon_map",
                "MAP",
                "MAP",
                "MAP",
                "MAP",
                "MAP");

            Add("weapon_map_straight",
                "MAP Straight",
                "MAP直线型",
                "MAP直線型",
                "MAP直線型",
                "MAP직선형");

            Add("weapon_map_landing",
                "MAP Landing",
                "MAP着弹型",
                "MAP著彈型",
                "MAP着弾型",
                "MAP착탄형");

            Add("weapon_map_center",
                "MAP Center",
                "MAP自机中心",
                "MAP自機中心",
                "MAP自機中心型",
                "MAP자기중심형");

            Add("weapon_map_range",
                "MAP Range:{0}",
                "MAP射程:{0}",
                "MAP射程:{0}",
                "MAP射程:{0}",
                "MAP사정:{0}");

            Add("weapon_map_tiles",
                "{0} tiles",
                "{0}格",
                "{0}格",
                "{0}マス",
                "{0}칸");

            Add("weapon_map_friendly_fire",
                "Friendly Fire",
                "友军误伤",
                "友軍誤傷",
                "友軍誤射",
                "아군오사");

            // MAP weapon targeting announcements
            Add("map_weapon_targets",
                "{0} enemies, {1} allies in range",
                "范围内: {0}个敌人, {1}个友军",
                "範圍內: {0}個敵人, {1}個友軍",
                "範囲内: 敵{0}体, 味方{1}体",
                "범위 내: 적{0}기, 아군{1}기");

            Add("map_weapon_targets_enemy_only",
                "{0} enemies in range",
                "范围内: {0}个敌人",
                "範圍內: {0}個敵人",
                "範囲内: 敵{0}体",
                "범위 내: 적{0}기");

            Add("map_weapon_no_targets",
                "No targets in range",
                "范围内无目标",
                "範圍內無目標",
                "範囲内に対象なし",
                "범위 내 대상 없음");

            Add("weapon_debuff_en_down",
                "EN Down",
                "EN减少",
                "EN減少",
                "EN低下",
                "EN감소");

            Add("weapon_debuff_armor_down",
                "Armor Down",
                "装甲减少",
                "裝甲減少",
                "装甲低下",
                "장갑감소");

            Add("weapon_debuff_mobility_down",
                "Mobility Down",
                "运动性减少",
                "運動性減少",
                "運動性低下",
                "운동성감소");

            Add("weapon_debuff_sight_down",
                "Sight Down",
                "照准减少",
                "照準減少",
                "照準低下",
                "조준감소");

            Add("weapon_debuff_morale_down",
                "Morale Down",
                "气力减少",
                "氣力減少",
                "気力低下",
                "기력감소");

            Add("weapon_debuff_sp_down",
                "SP Down",
                "SP减少",
                "SP減少",
                "SP低下",
                "SP감소");

            Add("weapon_debuff_param_half",
                "Param Halve",
                "能力减半",
                "能力減半",
                "能力半減",
                "능력반감");

            Add("weapon_debuff_shutdown",
                "Shutdown",
                "行动不能",
                "行動不能",
                "行動不能",
                "행동불능");

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

            // ===== STATUS SCREEN TABS =====
            Add("status_tab_pilot",
                "Pilot", "驾驶员", "駕駛員", "パイロット", "파일럿");
            Add("status_tab_robot",
                "Robot", "机体", "機體", "機体", "기체");
            Add("status_tab_weapon",
                "Weapon", "武器", "武器", "武器", "무기");

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
            Add("screen_selectdogmauihandler",
                "Dogma Command",
                "信條指令",
                "信條指令",
                "ドグマコマンド",
                "도그마 커맨드");
            Add("screen_selecttacticalcommanduihandler",
                "Tactical Command",
                "戰術指令",
                "戰術指令",
                "タクティカルコマンド",
                "전술 커맨드");

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

            Add("parts_slot",
                "Slot {0}",
                "插槽 {0}",
                "插槽 {0}",
                "スロット {0}",
                "슬롯 {0}");

            Add("parts_slot_empty",
                "Equipable",
                "可装备",
                "可裝備",
                "装備可能",
                "장비 가능");

            // ===== DIFFICULTY SELECTION =====
            Add("difficulty_description",
                "{0}: {1}",
                "{0}：{1}",
                "{0}：{1}",
                "{0}：{1}",
                "{0}: {1}");

            // ===== NEWLY COVERED SCREEN NAMES =====
            Add("screen_saveconfirmdialoguihandler",
                "Save Confirmation",
                "保存确认",
                "儲存確認",
                "セーブ確認",
                "저장 확인");

            Add("screen_prologuesystem",
                "Prologue",
                "序章",
                "序章",
                "プロローグ",
                "프롤로그");

            Add("screen_fullcustombonusuihandler",
                "Custom Bonus",
                "自定义奖励",
                "自訂獎勵",
                "カスタムボーナス",
                "커스텀 보너스");

            Add("screen_custombonuseffectuihandler",
                "Bonus Effect",
                "奖励效果",
                "獎勵效果",
                "ボーナス効果",
                "보너스 효과");

            Add("screen_optionuihandler",
                "System Options",
                "系统设置",
                "系統設定",
                "システムオプション",
                "시스템 옵션");

            Add("screen_optionuihandlerv",
                "System Options",
                "系统设置",
                "系統設定",
                "システムオプション",
                "시스템 옵션");

            Add("screen_libraryplayerrecorduihandler",
                "Player Record",
                "游玩记录",
                "遊玩記錄",
                "プレイレコード",
                "플레이 레코드");

            Add("screen_licencewindowuihandler",
                "License",
                "许可协议",
                "授權協議",
                "ライセンス",
                "라이선스");

            Add("screen_designworkuihandler",
                "Design Works",
                "设计作品",
                "設計作品",
                "デザインワークス",
                "디자인 워크");

            Add("screen_resultdisplay",
                "Battle Result",
                "战斗结果",
                "戰鬥結果",
                "戦闘結果",
                "전투 결과");

            Add("screen_resultdisplay2",
                "Battle Rewards",
                "战斗奖励",
                "戰鬥獎勵",
                "戦闘報酬",
                "전투 보상");

            Add("screen_battledetailsuihandler",
                "Battle Details",
                "战斗详情",
                "戰鬥詳情",
                "バトル詳細",
                "전투 상세");

            Add("screen_difficultyuihandler_dlc",
                "Difficulty Select",
                "难度选择",
                "難度選擇",
                "難易度選択",
                "난이도 선택");

            Add("screen_singlesimplebattlehandler",
                "Simple Battle",
                "简易战斗",
                "簡易戰鬥",
                "シンプルバトル",
                "심플 배틀");

            Add("screen_intermissionuihandler",
                "Intermission",
                "幕间",
                "幕間",
                "インターミッション",
                "인터미션");
            Add("screen_sortiepreparationrobotlistuihandler",
                "Sortie Robot List",
                "出击机体列表",
                "出擊機體列表",
                "出撃ロボットリスト",
                "출격 로봇 리스트");
            Add("screen_sortiepreparationpilotlistuihandler",
                "Sortie Pilot List",
                "出击驾驶员列表",
                "出擊駕駛員列表",
                "出撃パイロットリスト",
                "출격 파일럿 리스트");
            Add("screen_tacticalpartstatusuihandler",
                "Unit Status",
                "机体状态",
                "機體狀態",
                "ユニットステータス",
                "유닛 스테이터스");
            Add("screen_sortieshipselect",
                "Ship Select",
                "战舰选择",
                "戰艦選擇",
                "艦船選択",
                "함선 선택");
            Add("screen_sortieunitselect",
                "Unit Select",
                "机体选择",
                "機體選擇",
                "ユニット選択",
                "유닛 선택");

            // Support selection
            Add("support_attack_screen",
                "Attack Support",
                "攻击支援",
                "攻擊支援",
                "攻撃サポート",
                "공격 지원");
            Add("support_defence_screen",
                "Defence Support",
                "防御支援",
                "防禦支援",
                "防御サポート",
                "방어 지원");
            Add("support_none",
                "No Support",
                "无支援",
                "無支援",
                "サポートなし",
                "지원 없음");
            Add("support_double_attack",
                "Double Attack",
                "合体攻击",
                "合體攻擊",
                "ダブルアタック",
                "더블 어택");

            // Save/Load details
            Add("save_new_slot",
                "Empty Slot",
                "空存档",
                "空存檔",
                "空きスロット",
                "빈 슬롯");
            Add("save_auto",
                "Auto Save",
                "自动存档",
                "自動存檔",
                "オートセーブ",
                "자동 저장");
            Add("save_chapter",
                "Chapter {0}",
                "第{0}章",
                "第{0}章",
                "第{0}章",
                "{0}장");
            Add("save_turn",
                "Turn {0}",
                "回合 {0}",
                "回合 {0}",
                "ターン {0}",
                "턴 {0}");
            Add("save_playtime",
                "Playtime {0}",
                "游戏时间 {0}",
                "遊戲時間 {0}",
                "プレイ時間 {0}",
                "플레이 시간 {0}");
            Add("save_lap",
                "Lap {0}",
                "周目 {0}",
                "周目 {0}",
                "周目 {0}",
                "회차 {0}");

            // ===== PHASE INFO (TACTICAL) =====
            Add("phase_wave",
                "Wave {0}/{1}",
                "波次 {0}/{1}",
                "波次 {0}/{1}",
                "ウェーブ {0}/{1}",
                "웨이브 {0}/{1}");

            Add("phase_enemies",
                "Remaining enemies: {0}",
                "剩余敌人：{0}",
                "剩餘敵人：{0}",
                "残り敵数：{0}",
                "잔여 적: {0}");

            // ===== UNIT DISTANCE QUERY (;/' for enemies, .// for allies, \ repeat) =====
            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance, {5}=hpNow, {6}=hpMax
            Add("dist_enemy",
                "Enemy {0}/{1}: {2}, {3} distance {4}, HP {5}/{6}",
                "敌方 {0}/{1}：{2}，{3} 距离 {4}，HP {5}/{6}",
                "敵方 {0}/{1}：{2}，{3} 距離 {4}，HP {5}/{6}",
                "敵 {0}/{1}：{2}、{3} 距離 {4}、HP {5}/{6}",
                "적 {0}/{1}: {2}, {3} 거리 {4}, HP {5}/{6}");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance
            Add("dist_enemy_simple",
                "Enemy {0}/{1}: {2}, {3} distance {4}",
                "敌方 {0}/{1}：{2}，{3} 距离 {4}",
                "敵方 {0}/{1}：{2}，{3} 距離 {4}",
                "敵 {0}/{1}：{2}、{3} 距離 {4}",
                "적 {0}/{1}: {2}, {3} 거리 {4}");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance, {5}=hpNow, {6}=hpMax
            Add("dist_ally",
                "Ally {0}/{1}: {2}, {3} distance {4}, HP {5}/{6}",
                "友方 {0}/{1}：{2}，{3} 距离 {4}，HP {5}/{6}",
                "友方 {0}/{1}：{2}，{3} 距離 {4}，HP {5}/{6}",
                "味方 {0}/{1}：{2}、{3} 距離 {4}、HP {5}/{6}",
                "아군 {0}/{1}: {2}, {3} 거리 {4}, HP {5}/{6}");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance
            Add("dist_ally_simple",
                "Ally {0}/{1}: {2}, {3} distance {4}",
                "友方 {0}/{1}：{2}，{3} 距离 {4}",
                "友方 {0}/{1}：{2}，{3} 距離 {4}",
                "味方 {0}/{1}：{2}、{3} 距離 {4}",
                "아군 {0}/{1}: {2}, {3} 거리 {4}");

            Add("dist_no_enemies",
                "No enemy units",
                "没有敌方单位",
                "沒有敵方單位",
                "敵ユニットなし",
                "적 유닛 없음");

            Add("dist_no_allies",
                "No ally units",
                "没有友方单位",
                "沒有友方單位",
                "味方ユニットなし",
                "아군 유닛 없음");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance, {5}=hpNow, {6}=hpMax
            Add("dist_unacted",
                "Unacted {0}/{1}: {2}, {3} distance {4}, HP {5}/{6}",
                "未行动 {0}/{1}：{2}，{3} 距离 {4}，HP {5}/{6}",
                "未行動 {0}/{1}：{2}，{3} 距離 {4}，HP {5}/{6}",
                "未行動 {0}/{1}：{2}、{3} 距離 {4}、HP {5}/{6}",
                "미행동 {0}/{1}: {2}, {3} 거리 {4}, HP {5}/{6}");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance
            Add("dist_unacted_simple",
                "Unacted {0}/{1}: {2}, {3} distance {4}",
                "未行动 {0}/{1}：{2}，{3} 距离 {4}",
                "未行動 {0}/{1}：{2}，{3} 距離 {4}",
                "未行動 {0}/{1}：{2}、{3} 距離 {4}",
                "미행동 {0}/{1}: {2}, {3} 거리 {4}");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance, {5}=hpNow, {6}=hpMax
            Add("dist_acted",
                "Acted {0}/{1}: {2}, {3} distance {4}, HP {5}/{6}",
                "已行动 {0}/{1}：{2}，{3} 距离 {4}，HP {5}/{6}",
                "已行動 {0}/{1}：{2}，{3} 距離 {4}，HP {5}/{6}",
                "行動済 {0}/{1}：{2}、{3} 距離 {4}、HP {5}/{6}",
                "행동완료 {0}/{1}: {2}, {3} 거리 {4}, HP {5}/{6}");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance
            Add("dist_acted_simple",
                "Acted {0}/{1}: {2}, {3} distance {4}",
                "已行动 {0}/{1}：{2}，{3} 距离 {4}",
                "已行動 {0}/{1}：{2}，{3} 距離 {4}",
                "行動済 {0}/{1}：{2}、{3} 距離 {4}",
                "행동완료 {0}/{1}: {2}, {3} 거리 {4}");

            Add("dist_no_named_enemies",
                "No named enemies",
                "没有特殊敌方单位",
                "沒有特殊敵方單位",
                "ネームド敵ユニットなし",
                "네임드 적 유닛 없음");

            Add("dist_no_unacted",
                "No unacted units",
                "没有未行动单位",
                "沒有未行動單位",
                "未行動ユニットなし",
                "미행동 유닛 없음");

            Add("dist_no_acted",
                "No acted units",
                "没有已行动单位",
                "沒有已行動單位",
                "行動済ユニットなし",
                "행동완료 유닛 없음");

            // ===== DIRECTION (compass bearings for unit distance) =====
            Add("dir_n", "north", "北方", "北方", "北", "북쪽");
            Add("dir_s", "south", "南方", "南方", "南", "남쪽");
            Add("dir_e", "east", "东方", "東方", "東", "동쪽");
            Add("dir_w", "west", "西方", "西方", "西", "서쪽");
            Add("dir_ne", "northeast", "东北方", "東北方", "北東", "북동쪽");
            Add("dir_nw", "northwest", "西北方", "西北方", "北西", "북서쪽");
            Add("dir_se", "southeast", "东南方", "東南方", "南東", "남동쪽");
            Add("dir_sw", "southwest", "西南方", "西南方", "南西", "남서쪽");
            Add("dir_same", "same position", "同一位置", "同一位置", "同位置", "같은 위치");

            // ===== EXPANDED STATS =====
            Add("stat_exp",
                "EXP",
                "经验",
                "經驗",
                "経験値",
                "경험치");

            Add("stat_next_level",
                "Next",
                "下一级",
                "下一級",
                "次レベル",
                "다음 레벨");

            Add("stat_ace_rank",
                "Rank",
                "阶级",
                "階級",
                "ランク",
                "랭크");

            Add("rank_none",
                "Normal",
                "普通",
                "普通",
                "ノーマル",
                "일반");

            Add("rank_ace",
                "Ace",
                "王牌",
                "王牌",
                "エース",
                "에이스");

            Add("rank_superace",
                "Super Ace",
                "超级王牌",
                "超級王牌",
                "スーパーエース",
                "슈퍼 에이스");

            Add("rank_ultraace",
                "Ultra Ace",
                "究极王牌",
                "究極王牌",
                "ウルトラエース",
                "울트라 에이스");

            Add("stat_ace_bonus",
                "Ace Bonus",
                "王牌奖励",
                "王牌獎勵",
                "エースボーナス",
                "에이스 보너스");

            Add("stat_pilot_skills",
                "Pilot Skills",
                "驾驶员技能",
                "駕駛員技能",
                "パイロットスキル",
                "파일럿 스킬");

            Add("stat_robot_skills",
                "Robot Abilities",
                "机体特殊能力",
                "機體特殊能力",
                "機体特殊能力",
                "기체 특수 능력");

            Add("stat_spirit_commands",
                "Spirit Commands",
                "精神指令",
                "精神指令",
                "精神コマンド",
                "정신 커맨드");

            Add("stat_power_parts",
                "Power Parts",
                "强化零件",
                "強化零件",
                "強化パーツ",
                "강화 파츠");

            Add("stat_custom_bonus",
                "Custom Bonus",
                "自定义奖励",
                "自訂獎勵",
                "カスタムボーナス",
                "커스텀 보너스");

            Add("stat_upgrade_levels",
                "Upgrades",
                "改造",
                "改造",
                "改造",
                "개조");

            Add("stat_weapon_boost",
                "Weapon",
                "武器",
                "武器",
                "武器",
                "무기");

            // ===== UNIT STATS (I key) =====
            Add("unit_no_unit",
                "No unit at cursor",
                "光标处没有单位",
                "游標處沒有單位",
                "カーソル位置にユニットなし",
                "커서 위치에 유닛 없음");

            // ===== TURN SUMMARY =====
            // {0}=actionable count, {1}=total player units
            Add("turn_summary",
                "Player turn, {0}/{1} units can act",
                "我方回合，{0}/{1}台可行动",
                "我方回合，{0}/{1}台可行動",
                "自軍フェイズ、{0}/{1}機行動可能",
                "아군 페이즈, {0}/{1}기 행동 가능");

            Add("turn_summary_none",
                "Player turn",
                "我方回合",
                "我方回合",
                "自軍フェイズ",
                "아군 페이즈");

            // ===== CUSTOM ROBOT (機體改造) =====
            // {0}=robot name, {1}=1-based index, {2}=total count
            Add("custom_robot_switch",
                "{0} ({1}/{2})",
                "{0} ({1}/{2})",
                "{0} ({1}/{2})",
                "{0} ({1}/{2})",
                "{0} ({1}/{2})");

            // {0}=stat label, {1}=current value
            Add("custom_stat",
                "{0}: {1}",
                "{0}: {1}",
                "{0}: {1}",
                "{0}: {1}",
                "{0}: {1}");

            // {0}=stat label, {1}=before value, {2}=after value
            Add("custom_stat_change",
                "{0}: {1} -> {2}",
                "{0}: {1} -> {2}",
                "{0}: {1} -> {2}",
                "{0}: {1} → {2}",
                "{0}: {1} -> {2}");

            // ===== MISSION POINT DESTINATION (Alt+\) =====
            Add("mission_point_none",
                "No destination points",
                "没有目标地点",
                "沒有目標地點",
                "目標地点なし",
                "목표 지점 없음");

            // {0}=index, {1}=total, {2}=name, {3}=direction, {4}=distance, {5}=detailed path
            Add("mission_point_info_named",
                "Dest {0}/{1}: {2}, {3} distance {4}, {5}",
                "目标 {0}/{1}：{2}，{3} 距离 {4}，{5}",
                "目標 {0}/{1}：{2}，{3} 距離 {4}，{5}",
                "目標 {0}/{1}：{2}、{3} 距離 {4}、{5}",
                "목표 {0}/{1}: {2}, {3} 거리 {4}, {5}");

            // {0}=index, {1}=total, {2}=direction, {3}=distance, {4}=detailed path
            Add("mission_point_info",
                "Dest {0}/{1}: {2} distance {3}, {4}",
                "目标 {0}/{1}：{2} 距离 {3}，{4}",
                "目標 {0}/{1}：{2} 距離 {3}，{4}",
                "目標 {0}/{1}：{2} 距離 {3}、{4}",
                "목표 {0}/{1}: {2} 거리 {3}, {4}");

            // ===== ENEMY NEAREST TO MISSION POINT (Ctrl+\) =====
            // {0}=name, {1}=dist to point, {2}=dir from cursor, {3}=dist from cursor,
            // {4}=hpNow, {5}=hpMax, {6}=point index, {7}=total points
            Add("enemy_near_dest",
                "Nearest to dest {6}/{7}: {0}, {1} from point, {2} distance {3}, HP {4}/{5}",
                "最接近目标 {6}/{7}：{0}，距目标 {1}，{2} 距离 {3}，HP {4}/{5}",
                "最接近目標 {6}/{7}：{0}，距目標 {1}，{2} 距離 {3}，HP {4}/{5}",
                "目標{6}/{7}に最も近い敵：{0}、目標まで{1}、{2} 距離{3}、HP {4}/{5}",
                "목표 {6}/{7}에 가장 가까운 적: {0}, 목표까지 {1}, {2} 거리 {3}, HP {4}/{5}");

            // {0}=name, {1}=dist to point, {2}=dir from cursor, {3}=dist from cursor,
            // {4}=point index, {5}=total points
            Add("enemy_near_dest_simple",
                "Nearest to dest {4}/{5}: {0}, {1} from point, {2} distance {3}",
                "最接近目标 {4}/{5}：{0}，距目标 {1}，{2} 距离 {3}",
                "最接近目標 {4}/{5}：{0}，距目標 {1}，{2} 距離 {3}",
                "目標{4}/{5}に最も近い敵：{0}、目標まで{1}、{2} 距離{3}",
                "목표 {4}/{5}에 가장 가까운 적: {0}, 목표까지 {1}, {2} 거리 {3}");

            // EButtonIndex stat names (order: HP=0, EN=1, AR=2, MO=3, SI=4, WP=5)
            Add("custom_stat_hp", "HP", "HP", "HP", "HP", "HP");
            Add("custom_stat_en", "EN", "EN", "EN", "EN", "EN");
            Add("custom_stat_ar", "Armor", "\u88C5\u7532", "\u88DD\u7532", "\u88C5\u7532", "\uC7A5\uAC11");
            Add("custom_stat_mo", "Mobility", "\u8FD0\u52A8\u6027", "\u904B\u52D5\u6027", "\u904B\u52D5\u6027", "\uC6B4\uB3D9\uC131");
            Add("custom_stat_si", "Accuracy", "\u7167\u51C6", "\u7167\u6E96", "\u7167\u6E96", "\uC870\uC900");
            Add("custom_stat_wp", "Weapons", "\u6B66\u5668", "\u6B66\u5668", "\u6B66\u5668", "\uBB34\uAE30");

            // ===== SEARCH UNIT SCREEN =====
            Add("search_unit_screen",
                "Unit Search",
                "单位搜索",
                "單位搜尋",
                "ユニット検索",
                "유닛 검색");

            Add("search_mode_category",
                "Category Selection",
                "类别选择",
                "類別選擇",
                "カテゴリー選択",
                "카테고리 선택");

            Add("search_mode_item",
                "Item Selection",
                "项目选择",
                "項目選擇",
                "アイテム選択",
                "항목 선택");

            Add("search_mode_result",
                "Search Results",
                "搜索结果",
                "搜尋結果",
                "検索結果",
                "검색 결과");

            Add("search_mode_unknown",
                "Unknown Mode",
                "未知模式",
                "未知模式",
                "不明なモード",
                "알 수 없는 모드");

            Add("search_category_spirit",
                "Spirit",
                "精神",
                "精神",
                "精神",
                "정신");

            Add("search_category_skill",
                "Skill",
                "技能",
                "技能",
                "スキル",
                "스킬");

            Add("search_category_ability",
                "Ability",
                "能力",
                "能力",
                "アビリティ",
                "어빌리티");

            Add("search_category_unknown",
                "Unknown Category",
                "未知类别",
                "未知類別",
                "不明なカテゴリー",
                "알 수 없는 카테고리");

        }
    }
}
