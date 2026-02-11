using System;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppTMPro;
using UnityEngine;

namespace SRWYAccess
{
    /// <summary>
    /// Reads battle animation subtitles from BattleSceneUI.
    /// During BATTLE_SCENE, voice lines play with text shown via dialogText
    /// (subtitle TMP) and pilotName (speaker TMP) fields on BattleSceneUI.
    ///
    /// SAFETY: FindObjectOfType is called ONCE on initial entry (when scene is
    /// stable after stabilization). The ref is then cached for all subsequent
    /// reads. This avoids repeated FindObjectOfType calls from the background
    /// thread - FindObjectOfType iterates Unity's internal object list, which
    /// isn't thread-safe and crashes when the main thread is modifying it during
    /// scene destruction (uncatchable AccessViolationException in .NET 6).
    ///
    /// Cached ref risk: Pointer stays non-zero after Unity destroys the native
    /// object. Mitigated by aggressive stale detection (stops all IL2CPP access
    /// within 600ms of last subtitle change) and permanent stop after timeout.
    /// Each field access has only ~1μs TOCTOU window vs ~100μs for FindObjectOfType.
    ///
    /// All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator.
    /// </summary>
    public class BattleSubtitleHandler
    {
        private BattleSceneUI _cachedSceneUI;
        private string _lastDialogText = "";
        private string _lastPilotName = "";

        // Public access for screen review
        public string LastDialogText => _lastDialogText;
        public string LastPilotName => _lastPilotName;
        private int _namesRetries;       // retry count for unit name reads (GetComponentsInChildren is dangerous)
        private int _staleDialogCount;   // consecutive polls with unchanged/empty dialog text
        private int _readStatsCount;     // number of full stats reads this battle
        private bool _initialFind;       // true after first successful find

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
            _readStatsCount = 0;
            _initialFind = false;
        }

        /// <summary>
        /// Poll for battle subtitles.
        /// Called at reduced rate (~300ms) during BATTLE_SCENE.
        /// Uses FindObjectOfType ONCE on initial entry, then cached ref for reads.
        /// FindObjectOfType iterates Unity's internal object list and isn't
        /// thread-safe from the background thread - crashes during scene
        /// destruction when the main thread is modifying the list.
        /// Cached ref has ~1μs TOCTOU per field access vs ~100μs for FOT iteration.
        /// readStats: if true, also refresh HP/EN/state data.
        /// </summary>
        public void Update(bool readStats)
        {
            try
            {
                UpdateInner(readStats);
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"BattleSubtitle: error: {ex.GetType().Name}: {ex.Message}");
                _staleDialogCount = ModConfig.BattleStalePermanentStop;
            }
        }

        private void UpdateInner(bool readStats)
        {
            // Stale dialog detection: if subtitle hasn't changed, reduce poll
            // frequency to minimize IL2CPP accesses during battle ending.
            bool isStaleProbe = false;
            if (_staleDialogCount >= ModConfig.BattleStaleLimit)
            {
                if (_staleDialogCount >= ModConfig.BattleStalePermanentStop) return;
                _staleDialogCount++;
                if (_staleDialogCount % ModConfig.BattleStaleProbeInterval != 0) return;
                readStats = false; // No stats during stale probes
                isStaleProbe = true;
            }

            // First call: FindObjectOfType to find and cache BattleSceneUI.
            // Only called ONCE per battle, right after scene stabilization
            // (safe because no objects are being destroyed at this point).
            // All subsequent reads use the cached ref to avoid FOT's
            // thread-safety issues during active battle and scene destruction.
            if (!_initialFind)
            {
                BattleSceneUI found;
                try
                {
                    found = UnityEngine.Object.FindObjectOfType<BattleSceneUI>();
                }
                catch
                {
                    _staleDialogCount = ModConfig.BattleStalePermanentStop;
                    return;
                }

                if ((object)found == null) return;

                _cachedSceneUI = found;
                _initialFind = true;
                DebugHelper.Write("BattleSubtitle: Found BattleSceneUI");
                ReadBattleStats(_cachedSceneUI);
            }

            // Safety: managed null check (no IL2CPP access)
            if (_cachedSceneUI == null)
            {
                _staleDialogCount = ModConfig.BattleStalePermanentStop;
                return;
            }

            // Read dialogText from cached ref.
            // TOCTOU risk: ~1μs per field access. If Unity destroyed the native
            // object between polls, Pointer stays non-zero but the field access
            // hits freed memory → AV. Mitigated by aggressive stale detection
            // (stops all access within 600ms of last subtitle change).
            string dialogText = null;
            string pilotName = null;

            try
            {
                var dialogTmp = _cachedSceneUI.dialogText;
                if ((object)dialogTmp != null)
                    dialogText = dialogTmp.text;
            }
            catch
            {
                _staleDialogCount = ModConfig.BattleStalePermanentStop;
                return;
            }

            // During stale probes: only read dialogText (1 IL2CPP access).
            // Skip pilotName and stats to minimize exposure.
            if (!isStaleProbe)
            {
                try
                {
                    var pilotTmp = _cachedSceneUI.pilotName;
                    if ((object)pilotTmp != null)
                        pilotName = pilotTmp.text;
                }
                catch
                {
                    _staleDialogCount = ModConfig.BattleStalePermanentStop;
                    return;
                }

                if (readStats && _readStatsCount < ModConfig.BattleMaxStatsReads)
                {
                    _readStatsCount++;
                    ReadBattleStats(_cachedSceneUI);
                }
            }

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

                ScreenReaderOutput.Say(announcement);
                DebugHelper.Write($"BattleSubtitle: [{pilotName}] {dialogText}");
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

        private void ReadBattleStats(BattleSceneUI sceneUI)
        {
            // Read each stat independently. Individual field failures should NOT
            // invalidate the entire BattleSceneUI cache - a momentarily null TMP
            // field is not a sign that the sceneUI object is destroyed.
            try
            {
                var tmp = sceneUI.leftHpText;
                if ((object)tmp != null) LeftHp = tmp.text ?? "";
            }
            catch { }
            try
            {
                var tmp = sceneUI.rightHpText;
                if ((object)tmp != null) RightHp = tmp.text ?? "";
            }
            catch { }
            try
            {
                var tmp = sceneUI.leftEnText;
                if ((object)tmp != null) LeftEn = tmp.text ?? "";
            }
            catch { }
            try
            {
                var tmp = sceneUI.rightEnText;
                if ((object)tmp != null) RightEn = tmp.text ?? "";
            }
            catch { }

            // Battle state text (e.g. "攻撃" / "反撃")
            try
            {
                var tmp = sceneUI.leftBattleStateText;
                if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) LeftBattleState = t; }
            }
            catch { }
            try
            {
                var tmp = sceneUI.rightBattleStateText;
                if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) RightBattleState = t; }
            }
            catch { }

            // Bullet/ammo text
            try
            {
                var tmp = sceneUI.leftEnBulletText;
                if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) LeftBullet = t; }
            }
            catch { }
            try
            {
                var tmp = sceneUI.rightEnBulletText;
                if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) RightBullet = t; }
            }
            catch { }

            // Damage/critical text
            try
            {
                var tmp = sceneUI.damageCriticalText;
                if ((object)tmp != null) { string t = TextUtils.CleanRichText(tmp.text); if (!string.IsNullOrWhiteSpace(t)) DamageCritical = t; }
            }
            catch { }

            // Read unit names from info panel GameObjects (leftInfoGo/rightInfoGo).
            // GetComponentsInChildren is a Unity native call that iterates the
            // component hierarchy - extremely dangerous from a background thread
            // if the object is being destroyed (uncatchable AV). Retry up to 3
            // times (across readStats calls = ~9s window) to handle late UI loading.
            bool needNames = string.IsNullOrWhiteSpace(LeftName) || string.IsNullOrWhiteSpace(RightName);
            if (needNames && _namesRetries < ModConfig.BattleNamesMaxRetries)
            {
                _namesRetries++;
                try
                {
                    var go = sceneUI.leftInfoGo;
                    if ((object)go != null && go.Pointer != IntPtr.Zero)
                    {
                        string n = ReadBestNameText(go);
                        if (!string.IsNullOrWhiteSpace(n)) LeftName = n;
                    }
                }
                catch { }
                try
                {
                    var go = sceneUI.rightInfoGo;
                    if ((object)go != null && go.Pointer != IntPtr.Zero)
                    {
                        string n = ReadBestNameText(go);
                        if (!string.IsNullOrWhiteSpace(n)) RightName = n;
                    }
                }
                catch { }
            }
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
        /// </summary>
        private static string ReadBestNameText(GameObject go)
        {
            try
            {
                var tmps = go.GetComponentsInChildren<TextMeshProUGUI>(false);
                if (tmps == null || tmps.Count == 0) return null;

                string bestText = null;
                foreach (var tmp in tmps)
                {
                    if ((object)tmp == null) continue;
                    try
                    {
                        string t = TextUtils.CleanRichText(tmp.text);
                        if (string.IsNullOrWhiteSpace(t)) continue;
                        if (IsNumericOrSlash(t)) continue;
                        if (bestText == null || t.Length > bestText.Length)
                            bestText = t;
                    }
                    catch { }
                }
                return bestText;
            }
            catch { }
            return null;
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
