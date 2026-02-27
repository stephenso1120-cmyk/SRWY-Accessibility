using System;
using System.Text;
using UnityEngine;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppCom.BBStudio.SRTeam.Mission;
using Il2CppInterop.Runtime;

namespace SRWYAccess
{
    /// <summary>
    /// 处理任务选择界面的详细信息报读。
    /// 当用户在任务列表中移动光标时，自动读取选中任务的详细信息，
    /// 包括任务描述、推荐等级、地点等。
    ///
    /// Strategy: Monitor mission NAME content changes with minimal delays
    /// to ensure reliable reading during fast cursor movement.
    ///
    /// SAFETY: All IL2CPP object null checks use (object)x != null to bypass
    /// Unity's overloaded == operator, which accesses native pointers and can
    /// crash on destroyed objects.
    /// </summary>
    public class MissionDetailHandler
    {
        private MissionDetailInfo _cachedDetailInfo;
        private IntPtr _detailInfoPtr = IntPtr.Zero;
        private string _lastMissionName = "";

        // Init skip: REMOVED - was causing missed readings
        // Cooldown: REMOVED - was blocking fast cursor movement

        // Fault tracking: disable handler after repeated errors
        private int _faultCount;

        // Search throttle: only search every N cycles when not found
        private int _searchThrottle;

        // Frame counter: track when we last successfully read
        private int _framesSinceLastRead;

        // Re-read threshold: force re-read after N frames even if name unchanged
        // (handles case where user leaves and returns to same mission)
        private const int REREAD_THRESHOLD = 60; // ~2 seconds

        public void ReleaseHandler()
        {
            _cachedDetailInfo = null;
            _detailInfoPtr = IntPtr.Zero;
            _lastMissionName = "";
            _searchThrottle = 0;
            _framesSinceLastRead = 0;
        }

        /// <summary>
        /// Update: 检查任务列表界面状态并读取详细信息
        /// canSearch: if true, may call FindObjectOfType this cycle (round-robin controlled).
        /// </summary>
        public void Update(bool canSearch)
        {
            // Stop processing after too many faults
            if (_faultCount >= 5)
            {
                return;
            }

            // SafeCall must be available for safe text reading
            if (!SafeCall.TmpTextMethodAvailable)
            {
                return;
            }

            try
            {
                UpdateInner(canSearch);
            }
            catch (Exception ex)
            {
                _faultCount++;
                ReleaseHandler();
                DebugHelper.Write($"MissionDetailHandler: FAULT #{_faultCount}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void UpdateInner(bool canSearch)
        {
            _framesSinceLastRead++;

            // Search throttle: reduce FOT frequency when object not found
            if (canSearch && _searchThrottle > 0)
            {
                _searchThrottle--;
                return;
            }

            // Try to get or find MissionDetailInfo
            var detailInfo = GetOrFindDetailInfo(canSearch);
            if ((object)detailInfo == null)
            {
                // Clear state when UI is closed
                if (!string.IsNullOrEmpty(_lastMissionName))
                {
                    _lastMissionName = "";
                    _framesSinceLastRead = 0;
                }
                return;
            }

            // Validate pointer before each read
            if (!SafeCall.ProbeObject(_detailInfoPtr))
            {
                // Object destroyed, clear cache
                _cachedDetailInfo = null;
                _detailInfoPtr = IntPtr.Zero;
                _lastMissionName = "";
                _framesSinceLastRead = 0;
                return;
            }

            // Check if data is loading
            try
            {
                if (detailInfo.MissionDetailLoading)
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            // Read current mission name CONTENT
            string currentName = ReadMissionName(detailInfo);

            // Mission changed: name content different
            bool nameChanged = !string.IsNullOrEmpty(currentName) && currentName != _lastMissionName;

            // Force re-read: same mission but enough time passed (user may have left and returned)
            bool forceReread = !string.IsNullOrEmpty(currentName)
                && currentName == _lastMissionName
                && _framesSinceLastRead >= REREAD_THRESHOLD;

            if (nameChanged || forceReread)
            {
                if (nameChanged)
                {
                    DebugHelper.Write($"MissionDetailHandler: Name changed: '{_lastMissionName}' -> '{currentName}'");
                }
                else
                {
                    DebugHelper.Write($"MissionDetailHandler: Force re-read after {_framesSinceLastRead} frames");
                }

                _lastMissionName = currentName;
                _framesSinceLastRead = 0;
                ReadMissionDetail(detailInfo, currentName);
            }
            else if (string.IsNullOrEmpty(currentName) && !string.IsNullOrEmpty(_lastMissionName))
            {
                // Mission cleared (e.g., switched to a view with no detail panel)
                _lastMissionName = "";
                _framesSinceLastRead = 0;
            }
        }

        /// <summary>
        /// Get or find MissionDetailInfo object
        /// </summary>
        private MissionDetailInfo GetOrFindDetailInfo(bool canSearch)
        {
            // Check cached object validity
            if ((object)_cachedDetailInfo != null)
            {
                try
                {
                    // Verify pointer still matches
                    if (_cachedDetailInfo.Pointer != IntPtr.Zero && _cachedDetailInfo.Pointer == _detailInfoPtr)
                    {
                        return _cachedDetailInfo;
                    }
                }
                catch
                {
                    // Object destroyed
                }

                // Cache invalid
                _cachedDetailInfo = null;
                _detailInfoPtr = IntPtr.Zero;
            }

            // Search only when allowed
            if (!canSearch)
            {
                return null;
            }

            try
            {
                _cachedDetailInfo = UnityEngine.Object.FindObjectOfType<MissionDetailInfo>();

                if ((object)_cachedDetailInfo != null)
                {
                    _detailInfoPtr = _cachedDetailInfo.Pointer;
                    _searchThrottle = 5; // Wait 5 cycles before next search (~250ms)
                    _lastMissionName = ""; // Reset to trigger immediate read
                    _framesSinceLastRead = 0;
                    DebugHelper.Write($"MissionDetailHandler: Found MissionDetailInfo at {_detailInfoPtr:X}");
                }
                else
                {
                    _searchThrottle = 10; // Wait longer when not found (~500ms)
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MissionDetailHandler: FindObjectOfType error: {ex.Message}");
                _cachedDetailInfo = null;
                _detailInfoPtr = IntPtr.Zero;
                _searchThrottle = 10;
            }

            return _cachedDetailInfo;
        }

        /// <summary>
        /// 读取并报读任务详细信息
        /// </summary>
        private void ReadMissionDetail(MissionDetailInfo detailInfo, string missionName)
        {
            if (!SafeCall.ProbeObject(_detailInfoPtr))
            {
                return;
            }

            var sb = new StringBuilder();

            // 1. 任务名称
            if (!string.IsNullOrEmpty(missionName))
            {
                sb.Append(missionName);
                sb.Append(". ");
            }

            // 2. 任务描述
            string description = ReadDescription(detailInfo);
            if (!string.IsNullOrEmpty(description))
            {
                sb.Append(description);
                sb.Append(" ");
            }

            // 3. 地点名称
            string pointName = ReadPointName(detailInfo);
            if (!string.IsNullOrEmpty(pointName))
            {
                sb.Append(Loc.Get("mission_location"));
                sb.Append(": ");
                sb.Append(pointName);
                sb.Append(". ");
            }

            // 4. 推荐等级
            string recommendRank = ReadRecommendRank(detailInfo);
            if (!string.IsNullOrEmpty(recommendRank))
            {
                sb.Append(Loc.Get("mission_recommend_rank"));
                sb.Append(": ");
                sb.Append(recommendRank);
                sb.Append(". ");
            }

            if (sb.Length > 0)
            {
                string announcement = sb.ToString().Trim();
                ScreenReaderOutput.Say(announcement);
                DebugHelper.Write($"MissionDetailHandler: Announced: {announcement}");
            }
        }

        /// <summary>
        /// 读取任务名称
        /// </summary>
        private string ReadMissionName(MissionDetailInfo detailInfo)
        {
            try
            {
                var nameComp = detailInfo.dtMissionName;
                if ((object)nameComp != null && SafeCall.ProbeObject(nameComp.Pointer))
                {
                    IntPtr textPtr = SafeCall.ReadTmpTextSafe(nameComp.Pointer);
                    if (textPtr != IntPtr.Zero)
                    {
                        string text = SafeCall.SafeIl2CppStringToManaged(textPtr);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return TextUtils.CleanRichText(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MissionDetailHandler: ReadMissionName error: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// 读取任务描述
        /// </summary>
        private string ReadDescription(MissionDetailInfo detailInfo)
        {
            try
            {
                var descComp = detailInfo.dtDescription;
                if ((object)descComp != null && SafeCall.ProbeObject(descComp.Pointer))
                {
                    IntPtr textPtr = SafeCall.ReadTmpTextSafe(descComp.Pointer);
                    if (textPtr != IntPtr.Zero)
                    {
                        string text = SafeCall.SafeIl2CppStringToManaged(textPtr);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return TextUtils.CleanRichText(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MissionDetailHandler: ReadDescription error: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// 读取地点名称
        /// </summary>
        private string ReadPointName(MissionDetailInfo detailInfo)
        {
            try
            {
                var pointComp = detailInfo.dtPointName;
                if ((object)pointComp != null && SafeCall.ProbeObject(pointComp.Pointer))
                {
                    IntPtr textPtr = SafeCall.ReadTmpTextSafe(pointComp.Pointer);
                    if (textPtr != IntPtr.Zero)
                    {
                        string text = SafeCall.SafeIl2CppStringToManaged(textPtr);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return TextUtils.CleanRichText(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MissionDetailHandler: ReadPointName error: {ex.Message}");
            }
            return "";
        }

        /// <summary>
        /// 读取推荐等级
        /// </summary>
        private string ReadRecommendRank(MissionDetailInfo detailInfo)
        {
            try
            {
                var rankComp = detailInfo.dtRecommendRank;
                if ((object)rankComp != null && SafeCall.ProbeObject(rankComp.Pointer))
                {
                    IntPtr textPtr = SafeCall.ReadTmpTextSafe(rankComp.Pointer);
                    if (textPtr != IntPtr.Zero)
                    {
                        string text = SafeCall.SafeIl2CppStringToManaged(textPtr);
                        if (!string.IsNullOrEmpty(text))
                        {
                            return TextUtils.CleanRichText(text);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"MissionDetailHandler: ReadRecommendRank error: {ex.Message}");
            }
            return "";
        }
    }
}
