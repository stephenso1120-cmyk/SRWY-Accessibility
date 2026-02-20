using System;
using System.Runtime.CompilerServices;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using UnityEngine;

namespace SRWYAccess
{
    /// <summary>
    /// Reads battle animation subtitles from BattleSceneUI.
    /// During BATTLE_SCENE, voice lines play with text shown via dialogText
    /// (subtitle TMP) and pilotName (speaker TMP) fields on BattleSceneUI.
    ///
    /// Runs on Unity main thread via native hook. FindObjectOfType is safe.
    /// BattleSceneUI is found via FOT and cached; re-found if cache fails.
    /// Stale detection reduces poll frequency when subtitles are idle.
    ///
    /// All IL2CPP field reads use SafeCall.ReadBattleTmpFieldText() or
    /// SafeCall.ReadFieldPtrSafe() with VEH protection to prevent
    /// uncatchable AccessViolationException when native objects are freed.
    ///
    /// All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator.
    /// </summary>
    public class BattleSubtitleHandler
    {
        private BattleSceneUI _cachedSceneUI;
        private NormalBattleUIHandler _cachedNormalUI;
        private bool _usingNormalUI;
        private string _lastDialogText = "";
        private string _lastPilotName = "";

        // Public access for screen review
        public string LastDialogText => _lastDialogText;
        public string LastPilotName => _lastPilotName;
        private int _namesRetries;       // retry count for unit name reads
        private int _staleDialogCount;   // consecutive polls with unchanged/empty dialog text
        private int _initSkipCount;      // skip first N updates after finding BattleSceneUI

        // Cached battle stats (managed strings, safe to read from any thread)
        public string LeftHp { get; private set; } = "";
        public string LeftEn { get; private set; } = "";
        public string RightHp { get; private set; } = "";
        public string RightEn { get; private set; } = "";
        public string LeftName { get; private set; } = "";
        public string RightName { get; private set; } = "";
        public string LeftBattleState { get; private set; } = "";
        public string RightBattleState { get; private set; } = "";
        public string LeftBullet { get; private set; } = "";
        public string RightBullet { get; private set; } = "";
        public string DamageCritical { get; private set; } = "";

        public void ReleaseHandler()
        {
            _cachedSceneUI = null;
            _cachedNormalUI = null;
            _usingNormalUI = false;
            _lastDialogText = "";
            _lastPilotName = "";
            _namesRetries = 0;
            LeftHp = LeftEn = "";
            RightHp = RightEn = "";
            LeftName = RightName = "";
            LeftBattleState = RightBattleState = "";
            LeftBullet = RightBullet = "";
            DamageCritical = "";
            _staleDialogCount = 0;
            _initSkipCount = 0;
        }

        /// <summary>
        /// Poll for battle subtitles.
        /// Called at reduced rate (~300ms) during BATTLE_SCENE.
        /// readStats: if true, also refresh HP/EN/state data for screen review.
        /// battlePoll: current battle poll counter for danger zone detection.
        /// </summary>
        public void Update(bool readStats, int battlePoll)
        {
            // DANGER ZONE: polls 10-20 have heavy game engine initialization.
            // Skip stat reading (9+ VEH calls) but allow subtitle reading (3 VEH calls).
            // Original crashes (#12,15,17,19) were before VEH protection existed.
            // With VEH, subtitle reading is safe - AV caught and returns null.
            if (battlePoll >= 10 && battlePoll <= 20)
                readStats = false;

            // Stale dialog detection: reduce poll frequency when idle
            bool isStaleProbe = false;
            if (_staleDialogCount >= ModConfig.BattleStaleLimit)
            {
                _staleDialogCount++;
                if (_staleDialogCount % ModConfig.BattleStaleProbeInterval != 0) return;
                isStaleProbe = true;
            }

            // NormalBattleUIHandler path: fully isolated in separate method
            // to prevent JIT from resolving NormalBattleUIHandler type when
            // this method is first compiled (could cause AV on some IL2CPP configs).
            if (_usingNormalUI)
            {
                UpdateNormalBattlePath(readStats, isStaleProbe);
                return;
            }

            // Find BattleSceneUI (cached, re-found on failure)
            if ((object)_cachedSceneUI == null)
            {
                _cachedSceneUI = UnityEngine.Object.FindObjectOfType<BattleSceneUI>();
                if ((object)_cachedSceneUI != null)
                {
                    DebugHelper.Write("BattleSubtitle: Found BattleSceneUI");
                    DebugHelper.Flush();
                    _initSkipCount = 0; // VEH protection handles stale fields safely
                    return; // Skip this cycle
                }

                // BattleSceneUI not found, try NormalBattleUIHandler (separate method)
                if (TryFindNormalBattleUI())
                    return; // Skip first cycle
                return; // Neither found
            }

            // Skip first few updates after finding BattleSceneUI to allow full initialization
            if (_initSkipCount > 0)
            {
                _initSkipCount--;
                return;
            }

            // Validate Pointer before IL2CPP access (catch destroyed objects)
            if (_cachedSceneUI.Pointer == IntPtr.Zero
                || !SafeCall.ProbeObject(_cachedSceneUI.Pointer))
            {
                _cachedSceneUI = null;
                return;
            }

            IntPtr sceneUIPtr = _cachedSceneUI.Pointer;

            // Read subtitle text using VEH-protected field reads.
            string dialogText = null;
            string pilotName = null;

            // CRITICAL: Only use SafeCall protected path. Fallback path with try-catch
            // cannot catch AccessViolationException and will crash the game.
            if (SafeCall.BattleFieldsAvailable)
            {
                dialogText = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetDialogText);
                pilotName = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetPilotName);
            }
            else
            {
                // SafeCall not available - skip reading to avoid AV crash
                return;
            }

            if (!isStaleProbe && readStats)
                ReadBattleStats(sceneUIPtr);

            ProcessDialogText(dialogText, pilotName);
        }

        /// <summary>
        /// Try finding NormalBattleUIHandler as fallback. Isolated into separate
        /// method so JIT only resolves NormalBattleUIHandler type when called.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool TryFindNormalBattleUI()
        {
            try
            {
                _cachedNormalUI = UnityEngine.Object.FindObjectOfType<NormalBattleUIHandler>();
                if ((object)_cachedNormalUI != null)
                {
                    _usingNormalUI = true;
                    DebugHelper.Write("BattleSubtitle: Found NormalBattleUIHandler (fallback)");
                    DebugHelper.Flush();
                    _initSkipCount = 0; // VEH protection handles stale fields safely
                    return true; // Skip this cycle
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleSubtitle: NormalBattleUI find error: {ex.GetType().Name}");
            }
            return false;
        }

        /// <summary>
        /// Full update path for NormalBattleUIHandler. Isolated into separate
        /// method so JIT only resolves NormalBattleUIHandler type when called.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void UpdateNormalBattlePath(bool readStats, bool isStaleProbe)
        {
            // Skip first few updates after finding NormalBattleUIHandler
            if (_initSkipCount > 0)
            {
                _initSkipCount--;
                return;
            }

            if ((object)_cachedNormalUI == null || _cachedNormalUI.Pointer == IntPtr.Zero
                || !SafeCall.ProbeObject(_cachedNormalUI.Pointer))
            {
                _cachedNormalUI = null;
                _usingNormalUI = false;
                return;
            }

            IntPtr normalUIPtr = _cachedNormalUI.Pointer;
            string dialogText = null;
            string pilotName = null;

            // CRITICAL: Only use SafeCall protected path.
            if (SafeCall.NormalBattleFieldsAvailable)
            {
                dialogText = SafeCall.ReadBattleTmpFieldText(normalUIPtr, SafeCall.OffsetNormalDialogText);
                pilotName = SafeCall.ReadBattleTmpFieldText(normalUIPtr, SafeCall.OffsetNormalPilotName);
            }
            else
            {
                // SafeCall not available - skip reading to avoid AV crash
                return;
            }

            if (!isStaleProbe && readStats)
                ReadNormalBattleStats(normalUIPtr);

            ProcessDialogText(dialogText, pilotName);
        }

        /// <summary>
        /// Process dialog text change detection and announcement.
        /// Shared between BattleSceneUI and NormalBattleUIHandler paths.
        /// </summary>
        private void ProcessDialogText(string dialogText, string pilotName)
        {
            dialogText = TextUtils.CleanRichText(dialogText);
            pilotName = TextUtils.CleanRichText(pilotName);

            // Announce when dialog text changes and update stale counter
            if (!string.IsNullOrWhiteSpace(dialogText) && dialogText != _lastDialogText)
            {
                _staleDialogCount = 0; // New subtitle - reset stale counter
                _lastDialogText = dialogText;
                _lastPilotName = pilotName ?? "";

                string announcement;
                if (!string.IsNullOrWhiteSpace(pilotName))
                    announcement = Loc.Get("dialogue_line", pilotName, dialogText);
                else
                    announcement = dialogText;

                ScreenReaderOutput.SayQueued(announcement);
                DebugHelper.Write($"BattleSubtitle: [{pilotName}] {dialogText}");
                DebugHelper.Flush();
            }
            else
            {
                _staleDialogCount++; // Dialog unchanged or empty - increment stale
                if (string.IsNullOrWhiteSpace(dialogText))
                {
                    // Text cleared - reset tracking so same text can be announced again
                    _lastDialogText = "";
                    _lastPilotName = "";
                }
            }
        }

        /// <summary>
        /// Read battle stats from BattleSceneUI using VEH-protected field reads.
        /// Stats are cached for screen review (R key) only.
        /// </summary>
        private void ReadBattleStats(IntPtr sceneUIPtr)
        {
            if (!SafeCall.ProbeObject(sceneUIPtr)) return;

            if (SafeCall.BattleFieldsAvailable)
            {
                // VEH-protected path: each read catches AV silently
                string v;

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetLeftHpText);
                if (v != null) LeftHp = v;

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetRightHpText);
                if (v != null) RightHp = v;

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetLeftEnText);
                if (v != null) LeftEn = v;

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetRightEnText);
                if (v != null) RightEn = v;

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetLeftBattleStateText);
                if (v != null) { string t = TextUtils.CleanRichText(v); if (!string.IsNullOrWhiteSpace(t)) LeftBattleState = t; }

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetRightBattleStateText);
                if (v != null) { string t = TextUtils.CleanRichText(v); if (!string.IsNullOrWhiteSpace(t)) RightBattleState = t; }

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetLeftEnBulletText);
                if (v != null) { string t = TextUtils.CleanRichText(v); if (!string.IsNullOrWhiteSpace(t)) LeftBullet = t; }

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetRightEnBulletText);
                if (v != null) { string t = TextUtils.CleanRichText(v); if (!string.IsNullOrWhiteSpace(t)) RightBullet = t; }

                v = SafeCall.ReadBattleTmpFieldText(sceneUIPtr, SafeCall.OffsetDamageCriticalText);
                if (v != null) { string t = TextUtils.CleanRichText(v); if (!string.IsNullOrWhiteSpace(t)) DamageCritical = t; }

                // Unit names from info GameObjects
                bool needNames = string.IsNullOrWhiteSpace(LeftName) || string.IsNullOrWhiteSpace(RightName);
                if (needNames && _namesRetries < ModConfig.BattleNamesMaxRetries)
                {
                    _namesRetries++;
                    IntPtr leftGoPtr = SafeCall.ReadFieldPtrSafe(sceneUIPtr, SafeCall.OffsetLeftInfoGo);
                    if (leftGoPtr != IntPtr.Zero && SafeCall.ProbeObject(leftGoPtr))
                    {
                        try
                        {
                            var go = new GameObject(leftGoPtr);
                            string n = ReadBestNameText(go);
                            if (!string.IsNullOrWhiteSpace(n)) LeftName = n;
                        }
                        catch { }
                    }
                    IntPtr rightGoPtr = SafeCall.ReadFieldPtrSafe(sceneUIPtr, SafeCall.OffsetRightInfoGo);
                    if (rightGoPtr != IntPtr.Zero && SafeCall.ProbeObject(rightGoPtr))
                    {
                        try
                        {
                            var go = new GameObject(rightGoPtr);
                            string n = ReadBestNameText(go);
                            if (!string.IsNullOrWhiteSpace(n)) RightName = n;
                        }
                        catch { }
                    }
                }
            }
            // CRITICAL: Fallback path removed - try-catch cannot catch AV
        }

        /// <summary>
        /// Read battle stats from NormalBattleUIHandler using VEH-protected field reads.
        /// </summary>
        private void ReadNormalBattleStats(IntPtr normalUIPtr)
        {
            if (!SafeCall.ProbeObject(normalUIPtr)) return;

            if (SafeCall.NormalBattleFieldsAvailable)
            {
                string v;
                v = SafeCall.ReadBattleTmpFieldText(normalUIPtr, SafeCall.OffsetNormalLeftHPText);
                if (v != null) LeftHp = v;
                v = SafeCall.ReadBattleTmpFieldText(normalUIPtr, SafeCall.OffsetNormalRightHpText);
                if (v != null) RightHp = v;
                v = SafeCall.ReadBattleTmpFieldText(normalUIPtr, SafeCall.OffsetNormalLeftEnText);
                if (v != null) LeftEn = v;
                v = SafeCall.ReadBattleTmpFieldText(normalUIPtr, SafeCall.OffsetNormalRightEnText);
                if (v != null) RightEn = v;
            }
            // CRITICAL: Fallback path removed - try-catch cannot catch AV
        }

        /// <summary>
        /// Collect all current battle info items for screen review.
        /// </summary>
        public void CollectReviewItems(System.Collections.Generic.List<string> items)
        {
            // Left unit
            if (!string.IsNullOrWhiteSpace(LeftName) || !string.IsNullOrWhiteSpace(LeftHp))
            {
                string label = string.IsNullOrWhiteSpace(LeftName) ? Loc.Get("battle_left") : LeftName;
                string info = BuildUnitInfoString(LeftHp, LeftEn, LeftBullet, LeftBattleState);
                items.Add(label + ": " + info);
            }

            // Right unit
            if (!string.IsNullOrWhiteSpace(RightName) || !string.IsNullOrWhiteSpace(RightHp))
            {
                string label = string.IsNullOrWhiteSpace(RightName) ? Loc.Get("battle_right") : RightName;
                string info = BuildUnitInfoString(RightHp, RightEn, RightBullet, RightBattleState);
                items.Add(label + ": " + info);
            }

            // Damage/critical text
            if (!string.IsNullOrWhiteSpace(DamageCritical))
                items.Add(DamageCritical);

            // Last subtitle
            if (!string.IsNullOrWhiteSpace(_lastDialogText))
            {
                if (!string.IsNullOrWhiteSpace(_lastPilotName))
                    items.Add(Loc.Get("dialogue_line", _lastPilotName, _lastDialogText));
                else
                    items.Add(_lastDialogText);
            }
        }

        private static string BuildUnitInfoString(string hp, string en, string bullet, string state)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(hp)) parts.Add("HP " + hp);
            if (!string.IsNullOrWhiteSpace(en)) parts.Add("EN " + en);
            if (!string.IsNullOrWhiteSpace(bullet)) parts.Add(Loc.Get("battle_bullet") + " " + bullet);
            string result = string.Join(", ", parts);
            if (!string.IsNullOrWhiteSpace(state))
                result += " [" + state + "]";
            return result;
        }

        /// <summary>
        /// Read the best non-numeric TMP text from a GameObject's children.
        /// leftInfoGo/rightInfoGo contain HP/EN number labels mixed with
        /// pilot/robot names. We skip purely numeric text (HP/EN values)
        /// and return the longest non-numeric text (the unit name).
        /// CRITICAL: GetComponentsInChildren needs inner try-catch protection.
        /// </summary>
        private static string ReadBestNameText(GameObject go)
        {
            // CRITICAL: Inner try-catch for GetComponentsInChildren
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TextMeshProUGUI> tmps = null;
            try
            {
                tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleSubtitle: ReadBestNameText GetComponentsInChildren error: {ex.GetType().Name}");
                return null;
            }

            if (tmps == null || tmps.Count == 0) return null;

            string bestText = null;
            foreach (var tmp in tmps)
            {
                if ((object)tmp == null) continue;

                // CRITICAL: Use SafeCall to read tmp.text - direct access causes
                // uncatchable AV when TMP object is destroyed during animation
                string t = null;
                if (SafeCall.TmpTextMethodAvailable)
                {
                    IntPtr il2cppStrPtr = SafeCall.ReadTmpTextSafe(tmp.Pointer);
                    if (il2cppStrPtr != IntPtr.Zero)
                    {
                        try
                        {
                            t = IL2CPP.Il2CppStringToManaged(il2cppStrPtr);
                            t = TextUtils.CleanRichText(t);
                        }
                        catch { }
                    }
                }

                if (string.IsNullOrWhiteSpace(t)) continue;
                if (IsNumericOrSlash(t)) continue;
                if (bestText == null || t.Length > bestText.Length)
                    bestText = t;
            }
            return bestText;
        }

        /// <summary>
        /// Check if text is purely numeric (e.g. "5800", "-1", "10/150").
        /// Used to filter out HP/EN/gauge values from info panel TMPs.
        /// </summary>
        private static bool IsNumericOrSlash(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9') continue;
                if (c == '/' || c == '-' || c == ' ' || c == '.') continue;
                return false;
            }
            return true;
        }

    }
}
