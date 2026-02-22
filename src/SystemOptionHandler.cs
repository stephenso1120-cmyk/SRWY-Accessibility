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
    ///   OptionUIHandler.basePages[0..2] → PageOptionGame / PageOptionSound / PageOptionScreen
    ///   Each page has columnCommons (ColumnCommons extends List&lt;ColumnCommon&gt;)
    ///   ColumnCommons.LightIndex() → which column is highlighted
    ///   ColumnSelect: labelControl.meshLabel.textMesh = value TMP, textMesh = value TMP
    ///   ColumnVolume: volValue = value TMP, label from TMP children
    ///   ColumnString: nameText = label TMP, Name property = value string
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

        // Localization keys for the 3 option pages
        private static readonly string[] PageKeys = { "option_page_game", "option_page_sound", "option_page_screen" };

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

            // Find active page (only first 3: Game=0, Sound=1, Screen=2)
            int activePageIndex = -1;
            PageCommon activePage = null;

            int checkCount = Math.Min(pageCount, 3);
            for (int i = 0; i < checkCount; i++)
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

                string pageKey = activePageIndex < PageKeys.Length ? PageKeys[activePageIndex] : "option_page_game";
                ScreenReaderOutput.Say(Loc.Get(pageKey));
                DebugHelper.Write($"SystemOption: page={activePageIndex}");
            }

            // Get columnCommons for cursor tracking
            var colCommons = activePage.columnCommons;
            if ((object)colCommons == null) return;
            if (!SafeCall.ProbeObject(colCommons.Pointer)) return;

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
