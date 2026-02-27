using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.UI.StrategyPart.Option;
using Il2CppTMPro;
using Il2CppInterop.Runtime;

namespace SRWYAccess
{
    /// <summary>
    /// Handles system option screen (OptionUIHandler / OptionUIHandlerV):
    ///   - Announces setting name + current value when cursor moves (up/down)
    ///   - Announces setting name + new value when value changes (left/right)
    ///   - Announces page name when tab switches (Q/E or L1/R1)
    ///
    /// Architecture:
    ///   OptionUIHandler.basePages → all pages (Game/Sound/Screen/KeyBind/BGM/Analysis)
    ///   Each page has columnCommons (ColumnCommons extends List&lt;ColumnCommon&gt;)
    ///   ColumnCommons.LightIndex() → which column is highlighted
    ///   Column types: ColumnSelect, ColumnVolume, ColumnString, ColumnKeyBind,
    ///                 ColumnMusic, ColumnBattleSound, ColumnPoint, ColumnButton
    /// </summary>
    public class SystemOptionHandler
    {
        private OptionUIHandler _handler;
        private IntPtr _lastHandlerPtr;
        private int _lastLightIndex = -1;
        private int _lastPageIndex = -1;
        private string _lastValue = "";
        private int _initSkipCount;
        private IntPtr _explanationTmpPtr;
        private string _lastDescription = "";
        private int _pendingAnnounceFrames; // defer announcement to let game update explanation text

        // Localization keys for the 3 main option pages (index 0-2)
        private static readonly string[] PageKeys = { "option_page_game", "option_page_sound", "option_page_screen" };

        // Map gameObject name keywords to localization keys for sub-pages (index 3+)
        private static readonly (string keyword, string locKey)[] SubPageMap =
        {
            ("KeyBind", "option_page_keybind"),
            ("KeyGuide", "option_page_help"),
            ("BGM", "option_page_bgm"),
            ("Analysis", "option_page_analysis"),
        };

        public bool HasHandler => (object)_handler != null;

        public void ReleaseHandler()
        {
            _handler = null;
            _lastHandlerPtr = IntPtr.Zero;
            _lastLightIndex = -1;
            _lastPageIndex = -1;
            _lastValue = "";
            _lastDescription = "";
            _explanationTmpPtr = IntPtr.Zero;
            _pendingAnnounceFrames = 0;
            _initSkipCount = 0;
        }

        public void Update(bool canSearch)
        {
            if (canSearch)
                FindHandler();

            if ((object)_handler == null) return;

            if (!SafeCall.ProbeObject(_handler.Pointer))
            {
                DebugHelper.Write("SystemOption: handler freed");
                ReleaseHandler();
                return;
            }

            if (_initSkipCount > 0)
            {
                _initSkipCount--;
                return;
            }

            try
            {
                ReadCurrentState();
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SystemOption: error: {ex.GetType().Name}: {ex.Message}");
                ReleaseHandler();
            }
        }

        private void FindHandler()
        {
            try
            {
                var handler = UnityEngine.Object.FindObjectOfType<OptionUIHandler>();
                if ((object)handler == null)
                {
                    if ((object)_handler != null)
                        ReleaseHandler();
                    return;
                }

                if (handler.Pointer != _lastHandlerPtr)
                {
                    _handler = handler;
                    _lastHandlerPtr = handler.Pointer;
                    _lastLightIndex = -1;
                    _lastPageIndex = -1;
                    _lastValue = "";
                    _initSkipCount = 3;
                    DebugHelper.Write("SystemOption: handler found");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SystemOption: find error: {ex.GetType().Name}");
            }
        }

        private void ReadCurrentState()
        {
            var basePages = _handler.basePages;
            if ((object)basePages == null) return;

            int pageCount;
            try { pageCount = basePages.Count; }
            catch { return; }

            // Find active page (check all pages: Game/Sound/Screen/KeyBind/BGM/Analysis)
            int activePageIndex = -1;
            PageCommon activePage = null;

            for (int i = 0; i < pageCount; i++)
            {
                PageCommon page;
                try { page = basePages[i]; }
                catch { continue; }
                if ((object)page == null) continue;
                if (!SafeCall.ProbeObject(page.Pointer)) continue;

                try
                {
                    if (page.IsActive())
                    {
                        activePageIndex = i;
                        activePage = page;
                        break;
                    }
                }
                catch { continue; }
            }

            if (activePage == null || activePageIndex < 0) return;

            // Page change → announce page name
            if (activePageIndex != _lastPageIndex)
            {
                _lastPageIndex = activePageIndex;
                _lastLightIndex = -1;
                _lastValue = "";
                _lastDescription = "";
                _explanationTmpPtr = IntPtr.Zero;

                string pageName = GetPageName(activePageIndex, activePage);
                if (!string.IsNullOrEmpty(pageName))
                    ScreenReaderOutput.Say(pageName);
                DebugHelper.Write($"SystemOption: page={activePageIndex}");
            }

            // Get columnCommons for cursor tracking
            var colCommons = activePage.columnCommons;
            if ((object)colCommons == null || !SafeCall.ProbeObject(colCommons.Pointer))
            {
                // BGM pages use different column controllers instead of columnCommons
                ReadBgmPageState(activePage);
                return;
            }

            int lightIndex;
            try { lightIndex = colCommons.LightIndex(); }
            catch { return; }
            if (lightIndex < 0) return;

            int colCount;
            try { colCount = colCommons.Count; }
            catch { return; }
            if (lightIndex >= colCount) return;

            // Get the highlighted column
            ColumnCommon currentCol;
            try { currentCol = colCommons[lightIndex]; }
            catch { return; }
            if ((object)currentCol == null) return;
            if (!SafeCall.ProbeObject(currentCol.Pointer)) return;

            // Handle pending deferred announcement (wait for game to update explanation text)
            if (_pendingAnnounceFrames > 0)
            {
                _pendingAnnounceFrames--;
                if (_pendingAnnounceFrames == 0)
                {
                    // Now read description - game has had time to update it
                    string desc = ReadExplanationText(currentCol);
                    string val = ReadColumnValue(currentCol);
                    _lastValue = val ?? "";
                    _lastDescription = desc ?? "";

                    string text = FormatLabelValue(desc, val);
                    if (!string.IsNullOrEmpty(text))
                    {
                        ScreenReaderOutput.Say(text);
                        DebugHelper.Write($"SystemOption: [{lightIndex}] {text}");
                    }
                }
                return;
            }

            string value = ReadColumnValue(currentCol);

            if (lightIndex != _lastLightIndex)
            {
                // Cursor moved → defer announcement by 2 frames to let game update explanation
                _lastLightIndex = lightIndex;
                _pendingAnnounceFrames = 2;
            }
            else if ((value ?? "") != _lastValue)
            {
                // Value changed (left/right) → announce immediately with cached description
                _lastValue = value ?? "";
                string description = ReadExplanationText(currentCol);
                _lastDescription = description ?? "";

                string text = FormatLabelValue(description, value);
                if (!string.IsNullOrEmpty(text))
                {
                    ScreenReaderOutput.Say(text);
                    DebugHelper.Write($"SystemOption: change [{lightIndex}] {text}");
                }
            }
        }

        /// <summary>
        /// Format "label, value" string. Either part may be null.
        /// </summary>
        private string FormatLabelValue(string label, string value)
        {
            if (!string.IsNullOrEmpty(label) && !string.IsNullOrEmpty(value))
                return label + ", " + value;
            if (!string.IsNullOrEmpty(label))
                return label;
            if (!string.IsNullOrEmpty(value))
                return value;
            return null;
        }

        /// <summary>
        /// Read the description text from the Option_Explanation sibling of the column.
        /// Setting names are rendered as images (not TMP text), so we use the
        /// description text which updates when the cursor moves to a different setting.
        /// The column's parent (Window_Option_Game) contains an "Option_Explanation" child.
        /// </summary>
        private string ReadExplanationText(ColumnCommon col)
        {
            try
            {
                // Try cached explanation TMP first
                if (_explanationTmpPtr != IntPtr.Zero && SafeCall.ProbeObject(_explanationTmpPtr))
                {
                    IntPtr strPtr = SafeCall.ReadTmpTextSafe(_explanationTmpPtr);
                    if (strPtr != IntPtr.Zero)
                    {
                        string text = SafeCall.SafeIl2CppStringToManaged(strPtr);
                        if (!string.IsNullOrWhiteSpace(text))
                            return TextUtils.CleanRichText(text).Trim();
                    }
                }

                // Search for Option_Explanation in the column's parent children
                var go = col.gameObject;
                if ((object)go == null) return null;

                var colTransform = go.transform;
                if ((object)colTransform == null) return null;

                var parentTransform = colTransform.parent;
                if ((object)parentTransform == null) return null;

                int childCount;
                try { childCount = parentTransform.childCount; }
                catch { return null; }

                for (int i = 0; i < childCount; i++)
                {
                    Transform child;
                    try { child = parentTransform.GetChild(i); }
                    catch { continue; }
                    if ((object)child == null) continue;

                    string childName;
                    try { childName = child.gameObject.name; }
                    catch { continue; }

                    if (childName != null && childName.Contains("Explanation"))
                    {
                        var childGo = child.gameObject;
                        if ((object)childGo == null) continue;

                        var tmps = childGo.GetComponentsInChildren<TextMeshProUGUI>(false);
                        if (tmps == null || tmps.Count == 0) continue;

                        var tmp = tmps[0];
                        if ((object)tmp == null) continue;

                        // Cache the TMP pointer for fast access
                        _explanationTmpPtr = tmp.Pointer;

                        string text = ReadTmpText(tmp);
                        if (!string.IsNullOrWhiteSpace(text))
                            return text;
                    }
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Get the display name for a page by index. Uses localization keys for known pages
        /// and falls back to gameObject name matching for sub-pages.
        /// </summary>
        private string GetPageName(int pageIndex, PageCommon page)
        {
            // Known main pages (0-2)
            if (pageIndex < PageKeys.Length)
                return Loc.Get(PageKeys[pageIndex]);

            // Sub-pages: detect from gameObject name
            try
            {
                var go = page.gameObject;
                if ((object)go == null) return null;

                string goName = go.name;
                if (string.IsNullOrEmpty(goName)) return null;

                foreach (var (keyword, locKey) in SubPageMap)
                {
                    if (goName.Contains(keyword))
                        return Loc.Get(locKey);
                }

                // Unknown page: use gameObject name as-is
                return goName;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read just the current value from a column using type-specific fields.
        /// </summary>
        private string ReadColumnValue(ColumnCommon col)
        {
            try
            {
                // ColumnSelect → textMesh
                var colSelect = col.TryCast<ColumnSelect>();
                if ((object)colSelect != null)
                {
                    var tm = colSelect.textMesh;
                    if ((object)tm != null)
                    {
                        string val = ReadTmpText(tm);
                        if (!string.IsNullOrWhiteSpace(val))
                            return val;
                    }
                    return null;
                }

                // ColumnVolume → volValue
                var colVol = col.TryCast<ColumnVolume>();
                if ((object)colVol != null)
                {
                    var vv = colVol.volValue;
                    if ((object)vv != null)
                    {
                        string val = ReadTmpText(vv);
                        if (!string.IsNullOrWhiteSpace(val))
                            return val;
                    }
                    return null;
                }

                // ColumnString → Name property
                var colStr = col.TryCast<ColumnString>();
                if ((object)colStr != null)
                {
                    try { return colStr.Name; }
                    catch { return null; }
                }

                // ColumnKeyBind → actionItem + assignName
                var colKeyBind = col.TryCast<ColumnKeyBind>();
                if ((object)colKeyBind != null)
                {
                    string action = null, key = null;
                    var ai = colKeyBind.actionItem;
                    if ((object)ai != null)
                        action = ReadTmpText(ai);
                    var an = colKeyBind.assignName;
                    if ((object)an != null)
                        key = ReadTmpText(an);
                    return FormatLabelValue(action, key);
                }

                // ColumnMusic → mesh
                var colMusic = col.TryCast<ColumnMusic>();
                if ((object)colMusic != null)
                {
                    var m = colMusic.mesh;
                    if ((object)m != null)
                    {
                        string val = ReadTmpText(m);
                        if (!string.IsNullOrWhiteSpace(val))
                            return val;
                    }
                    return null;
                }

                // ColumnBattleSound → robotName + bgmName
                var colBS = col.TryCast<ColumnBattleSound>();
                if ((object)colBS != null)
                {
                    string robot = null, bgm = null;
                    var rn = colBS.robotName;
                    if ((object)rn != null)
                        robot = ReadTmpText(rn);
                    var bn = colBS.bgmName;
                    if ((object)bn != null)
                        bgm = ReadTmpText(bn);
                    return FormatLabelValue(robot, bgm);
                }

                // ColumnPoint → title + startSelect
                var colPoint = col.TryCast<ColumnPoint>();
                if ((object)colPoint != null)
                {
                    string title = null, start = null;
                    var t = colPoint.title;
                    if ((object)t != null)
                        title = ReadTmpText(t);
                    var ss = colPoint.startSelect;
                    if ((object)ss != null)
                        start = ReadTmpText(ss);
                    return FormatLabelValue(title, start);
                }

                // Fallback: last non-empty TMP child
                var go = col.gameObject;
                if ((object)go == null) return null;
                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count < 2) return null;
                for (int i = tmps.Count - 1; i >= 1; i--)
                {
                    var tmp = tmps[i];
                    if ((object)tmp == null) continue;
                    string text = ReadTmpText(tmp);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Handle cursor tracking for BGM sub-pages where columnCommons is null.
        /// BGM pages use ColumnsControl&lt;T&gt; or ColumnControl instead.
        /// </summary>
        private void ReadBgmPageState(PageCommon activePage)
        {
            try
            {
                // PageBGM01: ColumnsControl<ColumnBattleSound>
                var bgm01 = activePage.TryCast<PageBGM01>();
                if ((object)bgm01 != null)
                {
                    ReadBgmBattleSound(bgm01.columnsControl);
                    return;
                }

                // PageBGM02: ColumnsControl<ColumnBattleSound>
                var bgm02 = activePage.TryCast<PageBGM02>();
                if ((object)bgm02 != null)
                {
                    ReadBgmBattleSound(bgm02.columnsControl);
                    return;
                }

                // PageBGM03: ColumnsControl<ColumnSound>
                var bgm03 = activePage.TryCast<PageBGM03>();
                if ((object)bgm03 != null)
                {
                    ReadBgmSound(bgm03.columnsControl);
                    return;
                }

                // PageBGM05: ColumnControl → ColumnPoint
                var bgm05 = activePage.TryCast<PageBGM05>();
                if ((object)bgm05 != null)
                {
                    ReadBgm05(bgm05.columnControl);
                    return;
                }

                // PageBGM04 (album browser): no cursor tracking available
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SystemOption: BGM error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ReadBgmBattleSound(ColumnsControl<ColumnBattleSound> ctrl)
        {
            if ((object)ctrl == null) return;
            if (!SafeCall.ProbeObject(ctrl.Pointer)) return;

            int lightIndex;
            try { lightIndex = ctrl.LightIndex(); }
            catch { return; }
            if (lightIndex < 0) return;

            ColumnBattleSound col;
            try { col = ctrl.LightedColumn(); }
            catch { return; }
            if ((object)col == null) return;
            if (!SafeCall.ProbeObject(col.Pointer)) return;

            string robot = null, bgm = null;
            var rn = col.robotName;
            if ((object)rn != null) robot = ReadTmpText(rn);
            var bn = col.bgmName;
            if ((object)bn != null) bgm = ReadTmpText(bn);

            AnnounceIfChanged(lightIndex, FormatLabelValue(robot, bgm));
        }

        private void ReadBgmSound(ColumnsControl<ColumnSound> ctrl)
        {
            if ((object)ctrl == null) return;
            if (!SafeCall.ProbeObject(ctrl.Pointer)) return;

            int lightIndex;
            try { lightIndex = ctrl.LightIndex(); }
            catch { return; }
            if (lightIndex < 0) return;

            ColumnSound col;
            try { col = ctrl.LightedColumn(); }
            catch { return; }
            if ((object)col == null) return;
            if (!SafeCall.ProbeObject(col.Pointer)) return;

            // ColumnSound has meshes (List<TextMeshProUGUI>)
            string text = null;
            var meshes = col.meshes;
            if ((object)meshes != null)
            {
                int count;
                try { count = meshes.Count; }
                catch { count = 0; }
                for (int i = 0; i < count; i++)
                {
                    TextMeshProUGUI tmp;
                    try { tmp = meshes[i]; }
                    catch { continue; }
                    if ((object)tmp == null) continue;
                    string t = ReadTmpText(tmp);
                    if (!string.IsNullOrWhiteSpace(t))
                        text = (text == null) ? t : text + ", " + t;
                }
            }

            AnnounceIfChanged(lightIndex, text);
        }

        private void ReadBgm05(ColumnControl ctrl)
        {
            if ((object)ctrl == null) return;
            if (!SafeCall.ProbeObject(ctrl.Pointer)) return;

            ColumnPoint col;
            try { col = ctrl.LightedColumn(); }
            catch { return; }
            if ((object)col == null) return;
            if (!SafeCall.ProbeObject(col.Pointer)) return;

            string title = null, start = null;
            var t = col.title;
            if ((object)t != null) title = ReadTmpText(t);
            var ss = col.startSelect;
            if ((object)ss != null) start = ReadTmpText(ss);

            // ColumnControl lacks LightIndex, track by value change only
            string value = FormatLabelValue(title, start);
            if ((value ?? "") != _lastValue)
            {
                _lastValue = value ?? "";
                if (!string.IsNullOrEmpty(value))
                {
                    ScreenReaderOutput.Say(value);
                    DebugHelper.Write($"SystemOption: BGM05 {value}");
                }
            }
        }

        /// <summary>
        /// Announce if cursor position or value changed (used by BGM pages).
        /// </summary>
        private void AnnounceIfChanged(int lightIndex, string value)
        {
            if (lightIndex != _lastLightIndex)
            {
                _lastLightIndex = lightIndex;
                _lastValue = value ?? "";
                if (!string.IsNullOrEmpty(value))
                {
                    ScreenReaderOutput.Say(value);
                    DebugHelper.Write($"SystemOption: BGM [{lightIndex}] {value}");
                }
            }
            else if ((value ?? "") != _lastValue)
            {
                _lastValue = value ?? "";
                if (!string.IsNullOrEmpty(value))
                {
                    ScreenReaderOutput.Say(value);
                    DebugHelper.Write($"SystemOption: BGM val [{lightIndex}] {value}");
                }
            }
        }

        private string ReadTmpText(TextMeshProUGUI tmp)
        {
            IntPtr strPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
            if (strPtr == IntPtr.Zero) return null;
            string text = SafeCall.SafeIl2CppStringToManaged(strPtr);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return TextUtils.CleanRichText(text).Trim();
        }

    }
}
