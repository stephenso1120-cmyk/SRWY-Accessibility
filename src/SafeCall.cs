using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppAdvSDemoDlgEv;
using Il2CppCom.BBStudio.SRTeam.Common;
using Il2CppCom.BBStudio.SRTeam.Inputs;
using Il2CppCom.BBStudio.SRTeam.Map;
using Il2CppCom.BBStudio.SRTeam.UIs;
using Il2CppCom.BBStudio.SRTeam.UI.StrategyPart.Custom;
using Il2CppTMPro;

namespace SRWYAccess
{
    /// <summary>
    /// SEH-protected wrappers for IL2CPP native calls and field reads.
    /// Uses SRWYSafe.dll (native C with VEH + setjmp/longjmp) to catch
    /// AccessViolationException that .NET 6 cannot catch.
    ///
    /// When available, callers use SafeCall methods instead of direct
    /// IL2CPP managed calls. On fault (freed memory), returns IntPtr.Zero
    /// or (false, 0) instead of crashing the process.
    ///
    /// Falls back gracefully: if DLL missing, IsAvailable = false and
    /// callers use the original try/catch + guard mode approach.
    /// </summary>
    internal static class SafeCall
    {
        private static bool _available;
        private static bool _initialized;

        // Native method pointers (read from Il2CppMethodInfo offset 0)
        private static IntPtr _fnGetCurrentInputBehaviour;
        private static IntPtr _fnGetInputBehaviour;

        // MethodInfo pointers (passed as last arg to native IL2CPP calls)
        private static IntPtr _miGetCurrentInputBehaviour;
        private static IntPtr _miGetInputBehaviour;

        // Field offsets for UIHandlerBase (computed once from IL2CPP metadata)
        private static int _offsetControlBehaviour = -1;
        private static int _offsetCurrentCursorIndex = -1;
        private static bool _fieldsAvailable;

        // Field offsets for tactical map (direct reads bypass il2cpp_runtime_invoke)
        private static int _offsetFloatingCursor = -1;   // MapManager.floatingCursor
        private static int _offsetPawnController = -1;    // MapManager.<PawnController>k__BackingField
        private static int _offsetCurrentCoord = -1;      // MapUnit.currentCoord (Vector2Int)
        private static IntPtr _fnGetPawnHere;             // FloatingCursor.GetPawnHere native fn
        private static IntPtr _miGetPawnHere;             // FloatingCursor.GetPawnHere method info
        private static int _offsetSelectedPawnInfo = -1;  // PawnController.<SelectedPawnInfo>k__BackingField
        private static int _offsetPawnUnit = -1;          // PawnInfo.PawnUnit (direct field)
        private static bool _tacticalFieldsAvailable;

        // Battle UI field offsets (BattleSceneUI TMP/GameObject references)
        private static int _offsetDialogText = -1;
        private static int _offsetPilotName = -1;
        private static int _offsetLeftHpText = -1;
        private static int _offsetRightHpText = -1;
        private static int _offsetLeftEnText = -1;
        private static int _offsetRightEnText = -1;
        private static int _offsetLeftBattleStateText = -1;
        private static int _offsetRightBattleStateText = -1;
        private static int _offsetLeftEnBulletText = -1;
        private static int _offsetRightEnBulletText = -1;
        private static int _offsetDamageCriticalText = -1;
        private static int _offsetLeftInfoGo = -1;
        private static int _offsetRightInfoGo = -1;
        private static bool _battleFieldsAvailable;

        // NormalBattleUIHandler field offsets
        private static int _offsetNormalDialogText = -1;
        private static int _offsetNormalPilotName = -1;
        private static int _offsetNormalLeftHPText = -1;
        private static int _offsetNormalRightHpText = -1;
        private static int _offsetNormalLeftEnText = -1;
        private static int _offsetNormalRightEnText = -1;
        private static bool _normalBattleFieldsAvailable;

        // PawnUnit method pointers (computed properties, no backing fields)
        private static IntPtr _fnGetIsPlayerSide;
        private static IntPtr _miGetIsPlayerSide;
        private static IntPtr _fnGetIsAlive;
        private static IntPtr _miGetIsAlive;
        private static IntPtr _fnGetPawnData;
        private static IntPtr _miGetPawnData;
        private static bool _pawnMethodsAvailable;

        // BattleCheckMenuHandler field offset (SEH-protected btnType read)
        private static int _offsetCurBattleCheckBtnType = -1;
        private static bool _battleCheckFieldAvailable;

        // TMP_Text.get_text method pointer (for SEH-protected text reads)
        private static IntPtr _fnTmpGetText;
        private static IntPtr _miTmpGetText;
        private static bool _tmpTextMethodAvailable;

        // Robot.GetName and Pilot.GetName method pointers (for SEH-protected name reads)
        private static IntPtr _fnRobotGetName;
        private static IntPtr _miRobotGetName;
        private static IntPtr _fnPilotGetName;
        private static IntPtr _miPilotGetName;
        private static bool _nameMethodsAvailable;

        // CustomRobotUIHandler field offsets and RobotIndex method pointers
        private static int _offsetCustomRobotIndex = -1;
        private static int _offsetCustomCustom = -1;
        private static int _offsetRobotIndexIndex = -1;  // RobotIndex.index field
        private static IntPtr _fnRobotIndexCurrent;
        private static IntPtr _miRobotIndexCurrent;
        private static IntPtr _fnRobotIndexCount;
        private static IntPtr _miRobotIndexCount;
        private static IntPtr _fnCustomGetButtons;
        private static IntPtr _miCustomGetButtons;
        private static bool _customRobotFieldsAvailable;

        // Adventure dialogue string field offsets (protect Il2CppStringToManaged from AV on freed strings)
        private static int _offsetSDemoCurrentShowingMessage = -1;
        private static int _offsetSDemoSpeakerName = -1;
        private static bool _adventureFieldsAvailable;

        public static bool IsAvailable => _available;
        public static bool FieldsAvailable => _fieldsAvailable;
        public static bool BattleFieldsAvailable => _battleFieldsAvailable;
        public static bool NormalBattleFieldsAvailable => _normalBattleFieldsAvailable;
        public static bool TmpTextMethodAvailable => _tmpTextMethodAvailable;
        public static bool PawnMethodsAvailable => _pawnMethodsAvailable;
        public static bool BattleCheckFieldAvailable => _battleCheckFieldAvailable;
        public static bool NameMethodsAvailable => _nameMethodsAvailable;
        public static bool AdventureFieldsAvailable => _adventureFieldsAvailable;
        public static int OffsetSDemoCurrentShowingMessage => _offsetSDemoCurrentShowingMessage;
        public static int OffsetSDemoSpeakerName => _offsetSDemoSpeakerName;

        /// <summary>
        /// Returns the number of times we recovered from the known GameAssembly.dll
        /// IL2CPP runtime crash (class init null pointer bug at +0x338959).
        /// </summary>
        public static int GameCrashRecoveries
        {
            get
            {
                if (!_available) return 0;
                try { return SafeCall_GetGameCrashRecoveries(); }
                catch { return 0; }
            }
        }

        #region P/Invoke to SRWYSafe.dll

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SafeCall_Init();

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SafeCall_Shutdown();

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SafeCall_PP(IntPtr fnPtr, IntPtr thisPtr, IntPtr methodInfo);

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SafeCall_PIP(IntPtr fnPtr, IntPtr thisPtr, int arg, IntPtr methodInfo);

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr SafeReadPtr(IntPtr basePtr, int offset);

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SafeReadInt32(IntPtr basePtr, int offset, out int outValue);

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SafeCall_PP_Bool(IntPtr fnPtr, IntPtr thisPtr, IntPtr methodInfo, out int outValue);

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SafeCall_GetGameCrashRecoveries();

        [DllImport("SRWYSafe.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SafeCall_IsGameCrashRecoveryActive();

        #endregion

        /// <summary>
        /// Initialize: load DLL, register VEH, look up method pointers and field offsets.
        /// Call after IL2CPP classes are loaded (after Phase 5 in ModCore).
        /// </summary>
        public static unsafe void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            // Step 1: Load and init the native DLL
            try
            {
                int initResult = SafeCall_Init();
                if (initResult == 0)
                {
                    DebugHelper.Write("SafeCall: SafeCall_Init failed (VEH registration error)");
                    return;
                }
                DebugHelper.Write("SafeCall: SRWYSafe.dll loaded and VEH registered");

                // Check game crash recovery status
                try
                {
                    int recoveryActive = SafeCall_IsGameCrashRecoveryActive();
                    DebugHelper.Write($"SafeCall: Game crash recovery {(recoveryActive != 0 ? "active (IL2CPP bug workaround)" : "NOT active (signature mismatch)")}");
                }
                catch { /* Older DLL without this export */ }
            }
            catch (DllNotFoundException)
            {
                DebugHelper.Write("SafeCall: SRWYSafe.dll not found - SEH protection disabled");
                return;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: DLL load error: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // Step 2: Look up InputManager method pointers
            try
            {
                IntPtr inputMgrClass = Il2CppClassPointerStore<InputManager>.NativeClassPtr;
                if (inputMgrClass == IntPtr.Zero)
                {
                    DebugHelper.Write("SafeCall: InputManager class pointer is zero");
                    return;
                }

                // GetCurrentInputBehaviour(): 0 params
                _miGetCurrentInputBehaviour = IL2CPP.il2cpp_class_get_method_from_name(
                    inputMgrClass, "GetCurrentInputBehaviour", 0);

                if (_miGetCurrentInputBehaviour != IntPtr.Zero)
                    _fnGetCurrentInputBehaviour = *(IntPtr*)_miGetCurrentInputBehaviour;

                // GetInputBehaviour(InputMode): 1 param
                _miGetInputBehaviour = IL2CPP.il2cpp_class_get_method_from_name(
                    inputMgrClass, "GetInputBehaviour", 1);

                if (_miGetInputBehaviour != IntPtr.Zero)
                    _fnGetInputBehaviour = *(IntPtr*)_miGetInputBehaviour;

                DebugHelper.Write($"SafeCall: GetCurrentInputBehaviour mi=0x{_miGetCurrentInputBehaviour:X} fn=0x{_fnGetCurrentInputBehaviour:X}");
                DebugHelper.Write($"SafeCall: GetInputBehaviour mi=0x{_miGetInputBehaviour:X} fn=0x{_fnGetInputBehaviour:X}");

                if (_fnGetCurrentInputBehaviour == IntPtr.Zero || _fnGetInputBehaviour == IntPtr.Zero)
                {
                    DebugHelper.Write("SafeCall: Method pointer lookup returned zero - disabling");
                    return;
                }

                _available = true;
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: Method lookup error: {ex.GetType().Name}: {ex.Message}");
                return;
            }

            // Step 3: Look up UIHandlerBase field offsets (optional, non-fatal)
            try
            {
                IntPtr handlerClass = Il2CppClassPointerStore<UIHandlerBase>.NativeClassPtr;
                if (handlerClass == IntPtr.Zero)
                {
                    DebugHelper.Write("SafeCall: UIHandlerBase class pointer is zero - field protection disabled");
                    return;
                }

                IntPtr fieldCB = IL2CPP.il2cpp_class_get_field_from_name(handlerClass, "controlBehaviour");
                if (fieldCB != IntPtr.Zero)
                    _offsetControlBehaviour = (int)IL2CPP.il2cpp_field_get_offset(fieldCB);

                IntPtr fieldCI = IL2CPP.il2cpp_class_get_field_from_name(handlerClass, "currentCursorIndex");
                if (fieldCI != IntPtr.Zero)
                    _offsetCurrentCursorIndex = (int)IL2CPP.il2cpp_field_get_offset(fieldCI);

                DebugHelper.Write($"SafeCall: controlBehaviour offset={_offsetControlBehaviour}");
                DebugHelper.Write($"SafeCall: currentCursorIndex offset={_offsetCurrentCursorIndex}");

                if (_offsetControlBehaviour > 0 && _offsetCurrentCursorIndex > 0)
                {
                    _fieldsAvailable = true;
                    DebugHelper.Write("SafeCall: Field protection enabled");
                }
                else
                {
                    DebugHelper.Write("SafeCall: Field offsets invalid - field protection disabled");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: Field offset lookup error: {ex.GetType().Name}: {ex.Message}");
                // Non-fatal: method protection still works
            }

            // Step 4: Look up tactical map field offsets (optional, non-fatal)
            // These allow direct field reads via SafeReadPtr/SafeReadInt32,
            // bypassing il2cpp_runtime_invoke which can crash on freed objects
            // even when ProbeObject passes (vtable corruption).
            try
            {
                IntPtr mmClass = Il2CppClassPointerStore<MapManager>.NativeClassPtr;
                IntPtr mapUnitClass = Il2CppClassPointerStore<MapUnit>.NativeClassPtr;
                IntPtr fcClass = Il2CppClassPointerStore<FloatingCursor>.NativeClassPtr;

                if (mmClass != IntPtr.Zero && mapUnitClass != IntPtr.Zero && fcClass != IntPtr.Zero)
                {
                    IntPtr fCursor = IL2CPP.il2cpp_class_get_field_from_name(mmClass, "floatingCursor");
                    if (fCursor != IntPtr.Zero)
                        _offsetFloatingCursor = (int)IL2CPP.il2cpp_field_get_offset(fCursor);

                    IntPtr fPC = IL2CPP.il2cpp_class_get_field_from_name(mmClass, "<PawnController>k__BackingField");
                    if (fPC != IntPtr.Zero)
                        _offsetPawnController = (int)IL2CPP.il2cpp_field_get_offset(fPC);

                    IntPtr fCoord = IL2CPP.il2cpp_class_get_field_from_name(mapUnitClass, "currentCoord");
                    if (fCoord != IntPtr.Zero)
                        _offsetCurrentCoord = (int)IL2CPP.il2cpp_field_get_offset(fCoord);

                    _miGetPawnHere = IL2CPP.il2cpp_class_get_method_from_name(fcClass, "GetPawnHere", 0);
                    if (_miGetPawnHere != IntPtr.Zero)
                        unsafe { _fnGetPawnHere = *(IntPtr*)_miGetPawnHere; }

                    _tacticalFieldsAvailable = _offsetFloatingCursor > 0
                        && _offsetPawnController > 0
                        && _offsetCurrentCoord > 0;

                    DebugHelper.Write($"SafeCall: Tactical fields: cursor={_offsetFloatingCursor} pc={_offsetPawnController} coord={_offsetCurrentCoord} pawnHere={(_fnGetPawnHere != IntPtr.Zero ? "ok" : "missing")}");
                }

                // PawnController.<SelectedPawnInfo>k__BackingField and PawnInfo.PawnUnit
                // Used by CheckUnitChange() to track selected unit without il2cpp_runtime_invoke
                IntPtr pcClass = Il2CppClassPointerStore<PawnController>.NativeClassPtr;
                if (pcClass != IntPtr.Zero)
                {
                    IntPtr fSPI = IL2CPP.il2cpp_class_get_field_from_name(pcClass, "<SelectedPawnInfo>k__BackingField");
                    if (fSPI != IntPtr.Zero)
                        _offsetSelectedPawnInfo = (int)IL2CPP.il2cpp_field_get_offset(fSPI);
                }

                IntPtr piClass = Il2CppClassPointerStore<PawnController.PawnInfo>.NativeClassPtr;
                if (piClass != IntPtr.Zero)
                {
                    IntPtr fPU = IL2CPP.il2cpp_class_get_field_from_name(piClass, "PawnUnit");
                    if (fPU != IntPtr.Zero)
                        _offsetPawnUnit = (int)IL2CPP.il2cpp_field_get_offset(fPU);
                }

                DebugHelper.Write($"SafeCall: Pawn fields: selectedPawnInfo={_offsetSelectedPawnInfo} pawnUnit={_offsetPawnUnit} available={(_offsetSelectedPawnInfo > 0 && _offsetPawnUnit > 0)}");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: Tactical field lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 5: Battle UI field offsets (BattleSceneUI TMP references)
            // These allow reading TMP field pointers via SafeReadPtr (VEH-protected)
            // instead of through IL2CPP property accessors that can AV on freed memory.
            try
            {
                IntPtr bsuiClass = Il2CppClassPointerStore<BattleSceneUI>.NativeClassPtr;
                if (bsuiClass != IntPtr.Zero)
                {
                    _offsetDialogText = GetFieldOffsetSafe(bsuiClass, "dialogText");
                    _offsetPilotName = GetFieldOffsetSafe(bsuiClass, "pilotName");
                    _offsetLeftHpText = GetFieldOffsetSafe(bsuiClass, "leftHpText");
                    _offsetRightHpText = GetFieldOffsetSafe(bsuiClass, "rightHpText");
                    _offsetLeftEnText = GetFieldOffsetSafe(bsuiClass, "leftEnText");
                    _offsetRightEnText = GetFieldOffsetSafe(bsuiClass, "rightEnText");
                    _offsetLeftBattleStateText = GetFieldOffsetSafe(bsuiClass, "leftBattleStateText");
                    _offsetRightBattleStateText = GetFieldOffsetSafe(bsuiClass, "rightBattleStateText");
                    _offsetLeftEnBulletText = GetFieldOffsetSafe(bsuiClass, "leftEnBulletText");
                    _offsetRightEnBulletText = GetFieldOffsetSafe(bsuiClass, "rightEnBulletText");
                    _offsetDamageCriticalText = GetFieldOffsetSafe(bsuiClass, "damageCriticalText");
                    _offsetLeftInfoGo = GetFieldOffsetSafe(bsuiClass, "leftInfoGo");
                    _offsetRightInfoGo = GetFieldOffsetSafe(bsuiClass, "rightInfoGo");
                    _battleFieldsAvailable = _offsetDialogText > 0 && _offsetPilotName > 0;
                    DebugHelper.Write($"SafeCall: BattleSceneUI fields: dialogText={_offsetDialogText} pilotName={_offsetPilotName} available={_battleFieldsAvailable}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: BattleSceneUI field lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 6: NormalBattleUIHandler field offsets
            try
            {
                IntPtr nbuhClass = Il2CppClassPointerStore<NormalBattleUIHandler>.NativeClassPtr;
                if (nbuhClass != IntPtr.Zero)
                {
                    _offsetNormalDialogText = GetFieldOffsetSafe(nbuhClass, "dialogText");
                    _offsetNormalPilotName = GetFieldOffsetSafe(nbuhClass, "pilotName");
                    _offsetNormalLeftHPText = GetFieldOffsetSafe(nbuhClass, "leftHPText");
                    _offsetNormalRightHpText = GetFieldOffsetSafe(nbuhClass, "rightHpText");
                    _offsetNormalLeftEnText = GetFieldOffsetSafe(nbuhClass, "leftEnText");
                    _offsetNormalRightEnText = GetFieldOffsetSafe(nbuhClass, "rightEnText");
                    _normalBattleFieldsAvailable = _offsetNormalDialogText > 0 && _offsetNormalPilotName > 0;
                    DebugHelper.Write($"SafeCall: NormalBattleUI fields: dialogText={_offsetNormalDialogText} pilotName={_offsetNormalPilotName} available={_normalBattleFieldsAvailable}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: NormalBattleUI field lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 7: TMP_Text.get_text method pointer for SEH-protected text reads
            try
            {
                IntPtr tmpClass = Il2CppClassPointerStore<TMP_Text>.NativeClassPtr;
                if (tmpClass != IntPtr.Zero)
                {
                    _miTmpGetText = IL2CPP.il2cpp_class_get_method_from_name(tmpClass, "get_text", 0);
                    if (_miTmpGetText != IntPtr.Zero)
                        unsafe { _fnTmpGetText = *(IntPtr*)_miTmpGetText; }
                    _tmpTextMethodAvailable = _fnTmpGetText != IntPtr.Zero;
                    DebugHelper.Write($"SafeCall: TMP_Text.get_text fn=0x{_fnTmpGetText:X} available={_tmpTextMethodAvailable}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: TMP_Text method lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 7.5: Robot.GetName and Pilot.GetName method pointers for SEH-protected name reads
            // Protects against AV when calling GetName() on partially-destroyed objects
            try
            {
                IntPtr robotClass = Il2CppClassPointerStore<Il2CppCom.BBStudio.SRTeam.Data.Robot>.NativeClassPtr;
                IntPtr pilotClass = Il2CppClassPointerStore<Il2CppCom.BBStudio.SRTeam.Data.Pilot>.NativeClassPtr;

                if (robotClass != IntPtr.Zero)
                {
                    _miRobotGetName = IL2CPP.il2cpp_class_get_method_from_name(robotClass, "GetName", 0);
                    if (_miRobotGetName != IntPtr.Zero)
                        unsafe { _fnRobotGetName = *(IntPtr*)_miRobotGetName; }
                }

                if (pilotClass != IntPtr.Zero)
                {
                    _miPilotGetName = IL2CPP.il2cpp_class_get_method_from_name(pilotClass, "GetName", 0);
                    if (_miPilotGetName != IntPtr.Zero)
                        unsafe { _fnPilotGetName = *(IntPtr*)_miPilotGetName; }
                }

                _nameMethodsAvailable = _fnRobotGetName != IntPtr.Zero && _fnPilotGetName != IntPtr.Zero;
                DebugHelper.Write($"SafeCall: Name methods: Robot=0x{_fnRobotGetName:X} Pilot=0x{_fnPilotGetName:X} available={_nameMethodsAvailable}");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: Name method lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 8: BattleCheckMenuHandler field offset
            // Protects against AV when reading curBattleCheckMenuButtonType on
            // a freed handler during scene transitions (uncatchable in .NET 6).
            try
            {
                IntPtr bchClass = Il2CppClassPointerStore<BattleCheckMenuHandler>.NativeClassPtr;
                if (bchClass != IntPtr.Zero)
                {
                    _offsetCurBattleCheckBtnType = GetFieldOffsetSafe(
                        bchClass, "<curBattleCheckMenuButtonType>k__BackingField");
                    _battleCheckFieldAvailable = _available && _offsetCurBattleCheckBtnType > 0;
                    DebugHelper.Write($"SafeCall: BattleCheckMenu btnType offset={_offsetCurBattleCheckBtnType} available={_battleCheckFieldAvailable}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: BattleCheckMenu field lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 9: PawnUnit method pointers (IsPlayerSide, IsAlive, PawnData)
            // These are computed properties with no backing fields - must use method calls.
            // SafeCall_PP_Bool distinguishes "returned false" from "AV occurred".
            try
            {
                IntPtr pawnUnitClass = Il2CppClassPointerStore<PawnUnit>.NativeClassPtr;
                if (pawnUnitClass != IntPtr.Zero)
                {
                    _miGetIsPlayerSide = IL2CPP.il2cpp_class_get_method_from_name(
                        pawnUnitClass, "get_IsPlayerSide", 0);
                    if (_miGetIsPlayerSide != IntPtr.Zero)
                        unsafe { _fnGetIsPlayerSide = *(IntPtr*)_miGetIsPlayerSide; }

                    _miGetIsAlive = IL2CPP.il2cpp_class_get_method_from_name(
                        pawnUnitClass, "get_IsAlive", 0);
                    if (_miGetIsAlive != IntPtr.Zero)
                        unsafe { _fnGetIsAlive = *(IntPtr*)_miGetIsAlive; }

                    _miGetPawnData = IL2CPP.il2cpp_class_get_method_from_name(
                        pawnUnitClass, "get_PawnData", 0);
                    if (_miGetPawnData != IntPtr.Zero)
                        unsafe { _fnGetPawnData = *(IntPtr*)_miGetPawnData; }

                    _pawnMethodsAvailable = _available
                        && _fnGetIsPlayerSide != IntPtr.Zero
                        && _fnGetIsAlive != IntPtr.Zero
                        && _fnGetPawnData != IntPtr.Zero;

                    DebugHelper.Write($"SafeCall: PawnUnit methods: IsPlayerSide=0x{_fnGetIsPlayerSide:X} IsAlive=0x{_fnGetIsAlive:X} PawnData=0x{_fnGetPawnData:X} available={_pawnMethodsAvailable}");
                }
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: PawnUnit method lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 10: Adventure dialogue string field offsets
            // IL2CPP string field access internally calls Il2CppStringToManaged which reads
            // the string's native memory (length + characters). If the string has been freed
            // during a scene transition, this causes an uncatchable AccessViolationException.
            // By reading the string pointer via SafeReadPtr (VEH protected) and probing it
            // before Il2CppStringToManaged, we prevent the crash.
            try
            {
                IntPtr msgClass = Il2CppClassPointerStore<SDemoDialogueMessage>.NativeClassPtr;
                if (msgClass != IntPtr.Zero)
                    _offsetSDemoCurrentShowingMessage = GetFieldOffsetSafe(msgClass, "_currentShowingMessage");

                IntPtr spkClass = Il2CppClassPointerStore<SDemoSpeakerName>.NativeClassPtr;
                if (spkClass != IntPtr.Zero)
                    _offsetSDemoSpeakerName = GetFieldOffsetSafe(spkClass, "_speakerName");

                _adventureFieldsAvailable = _available
                    && _offsetSDemoCurrentShowingMessage > 0;
                DebugHelper.Write($"SafeCall: Adventure fields: msg={_offsetSDemoCurrentShowingMessage} spk={_offsetSDemoSpeakerName} available={_adventureFieldsAvailable}");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: Adventure field lookup error: {ex.GetType().Name}: {ex.Message}");
            }

            // Step 11: CustomRobotUIHandler field offsets + RobotIndex methods
            try
            {
                IntPtr cruhClass = Il2CppClassPointerStore<CustomRobotUIHandler>.NativeClassPtr;
                if (cruhClass != IntPtr.Zero)
                {
                    _offsetCustomRobotIndex = GetFieldOffsetSafe(cruhClass, "robotIndex");
                    _offsetCustomCustom = GetFieldOffsetSafe(cruhClass, "custom");
                }

                IntPtr riClass = Il2CppClassPointerStore<RobotIndex>.NativeClassPtr;
                if (riClass != IntPtr.Zero)
                {
                    _offsetRobotIndexIndex = GetFieldOffsetSafe(riClass, "index");

                    _miRobotIndexCurrent = IL2CPP.il2cpp_class_get_method_from_name(riClass, "Current", 0);
                    if (_miRobotIndexCurrent != IntPtr.Zero)
                        unsafe { _fnRobotIndexCurrent = *(IntPtr*)_miRobotIndexCurrent; }

                    _miRobotIndexCount = IL2CPP.il2cpp_class_get_method_from_name(riClass, "Count", 0);
                    if (_miRobotIndexCount != IntPtr.Zero)
                        unsafe { _fnRobotIndexCount = *(IntPtr*)_miRobotIndexCount; }
                }

                IntPtr customClass = Il2CppClassPointerStore<Custom>.NativeClassPtr;
                if (customClass != IntPtr.Zero)
                {
                    _miCustomGetButtons = IL2CPP.il2cpp_class_get_method_from_name(customClass, "GetCustomButtons", 0);
                    if (_miCustomGetButtons != IntPtr.Zero)
                        unsafe { _fnCustomGetButtons = *(IntPtr*)_miCustomGetButtons; }
                }

                _customRobotFieldsAvailable = _available
                    && _offsetCustomRobotIndex > 0
                    && _offsetCustomCustom > 0
                    && _offsetRobotIndexIndex > 0
                    && _fnRobotIndexCurrent != IntPtr.Zero
                    && _fnRobotIndexCount != IntPtr.Zero;

                DebugHelper.Write($"SafeCall: CustomRobot fields: robotIndex={_offsetCustomRobotIndex} custom={_offsetCustomCustom} riIndex={_offsetRobotIndexIndex} available={_customRobotFieldsAvailable}");
            }
            catch (Exception ex)
            {
                DebugHelper.Write($"SafeCall: CustomRobot field lookup error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Shutdown: remove VEH handler.
        /// </summary>
        public static void Shutdown()
        {
            if (!_available) return;
            try
            {
                SafeCall_Shutdown();
            }
            catch { }
            _available = false;
            _fieldsAvailable = false;
        }

        // ===== Object Probing =====

        /// <summary>
        /// Probe whether a native IL2CPP object's memory is still accessible.
        /// Reads the klass pointer at offset 0 with VEH protection.
        /// If the native memory has been freed (page unmapped or zeroed),
        /// SEH catches the AV and returns false.
        ///
        /// Use before calling instance methods/properties on IL2CPP objects
        /// that may have been destroyed during scene transitions (e.g.
        /// MapManager, FloatingCursor, PawnUnit during battle transitions).
        ///
        /// When SRWYSafe.dll is not available, returns true (trust caller's
        /// existing null/Pointer checks).
        /// </summary>
        public static bool ProbeObject(IntPtr objPtr)
        {
            if (objPtr == IntPtr.Zero)
                return false;
            if (!_available)
                return true; // Can't verify, assume alive
            return SafeReadPtr(objPtr, 0) != IntPtr.Zero;
        }

        /// <summary>
        /// Read the IL2CPP type name of an object via VEH-protected klass pointer read.
        /// Reads obj->klass (offset 0) under VEH, then calls il2cpp_class_get_name
        /// on the klass pointer (static metadata, never freed).
        /// Returns null if the object is freed or the read fails.
        /// </summary>
        public static string ReadObjectTypeName(IntPtr objPtr)
        {
            if (!_available || objPtr == IntPtr.Zero)
                return null;

            // Read klass pointer (offset 0) under VEH protection
            IntPtr klassPtr = SafeReadPtr(objPtr, 0);
            if (klassPtr == IntPtr.Zero)
                return null;

            // Read type name from class metadata (static, never freed)
            try
            {
                IntPtr namePtr = IL2CPP.il2cpp_class_get_name(klassPtr);
                if (namePtr == IntPtr.Zero) return null;
                return Marshal.PtrToStringAnsi(namePtr);
            }
            catch { return null; }
        }

        // ===== High-level safe APIs =====

        /// <summary>
        /// Safe call to InputManager.GetCurrentInputBehaviour().
        /// Returns the native Il2CppObject* of the returned IInputBehaviour,
        /// or IntPtr.Zero if the call faulted (freed memory) or returned null.
        /// </summary>
        public static IntPtr GetCurrentInputBehaviourSafe(IntPtr inputMgrPtr)
        {
            if (!_available || inputMgrPtr == IntPtr.Zero)
                return IntPtr.Zero;

            return SafeCall_PP(
                _fnGetCurrentInputBehaviour,
                inputMgrPtr,
                _miGetCurrentInputBehaviour);
        }

        /// <summary>
        /// Safe call to InputManager.GetInputBehaviour(InputMode mode).
        /// Returns the native Il2CppObject* of the returned IInputBehaviour,
        /// or IntPtr.Zero if the call faulted.
        /// </summary>
        public static IntPtr GetInputBehaviourSafe(IntPtr inputMgrPtr, int mode)
        {
            if (!_available || inputMgrPtr == IntPtr.Zero)
                return IntPtr.Zero;

            return SafeCall_PIP(
                _fnGetInputBehaviour,
                inputMgrPtr,
                mode,
                _miGetInputBehaviour);
        }

        /// <summary>
        /// Safe read of handler.controlBehaviour's native pointer.
        /// Returns the Il2CppObject* stored in the controlBehaviour field,
        /// or IntPtr.Zero if the read faulted or field is null.
        /// </summary>
        public static IntPtr ReadControlBehaviourPtrSafe(IntPtr handlerPtr)
        {
            if (!_fieldsAvailable || handlerPtr == IntPtr.Zero)
                return IntPtr.Zero;

            return SafeReadPtr(handlerPtr, _offsetControlBehaviour);
        }

        /// <summary>
        /// Safe read of handler.currentCursorIndex.
        /// Returns (true, value) on success, (false, 0) on fault.
        /// </summary>
        public static (bool ok, int value) ReadCursorIndexSafe(IntPtr handlerPtr)
        {
            if (!_fieldsAvailable || handlerPtr == IntPtr.Zero)
                return (false, 0);

            int result = SafeReadInt32(handlerPtr, _offsetCurrentCursorIndex, out int value);
            return (result != 0, value);
        }

        // ===== Tactical map safe field reads =====
        // These read backing fields directly via SafeReadPtr/SafeReadInt32 under VEH,
        // avoiding il2cpp_runtime_invoke which can crash even when ProbeObject passes
        // (freed objects may have valid klass pointers but corrupted vtables).

        public static bool TacticalFieldsAvailable => _tacticalFieldsAvailable;
        public static bool PawnFieldsAvailable => _available && _offsetSelectedPawnInfo > 0 && _offsetPawnUnit > 0;

        /// <summary>
        /// Safe read of MapManager.floatingCursor (backing field for Cursor property).
        /// Returns the native FloatingCursor pointer, or IntPtr.Zero on fault/null.
        /// </summary>
        public static IntPtr ReadMapCursorPtrSafe(IntPtr mmPtr)
        {
            if (!_tacticalFieldsAvailable || mmPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeReadPtr(mmPtr, _offsetFloatingCursor);
        }

        /// <summary>
        /// Safe read of MapManager's PawnController backing field.
        /// Returns the native PawnController pointer, or IntPtr.Zero on fault/null.
        /// </summary>
        public static IntPtr ReadPawnControllerPtrSafe(IntPtr mmPtr)
        {
            if (!_tacticalFieldsAvailable || mmPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeReadPtr(mmPtr, _offsetPawnController);
        }

        /// <summary>
        /// Safe read of MapUnit.currentCoord (Vector2Int stored inline as x,y int32 pair).
        /// Returns (true, x, y) on success, (false, 0, 0) on fault.
        /// Works on FloatingCursor (inherits from MapUnit) - offset accounts for inheritance.
        /// </summary>
        public static (bool ok, int x, int y) ReadCurrentCoordSafe(IntPtr mapUnitPtr)
        {
            if (!_tacticalFieldsAvailable || mapUnitPtr == IntPtr.Zero)
                return (false, 0, 0);
            int rx = SafeReadInt32(mapUnitPtr, _offsetCurrentCoord, out int x);
            if (rx == 0) return (false, 0, 0);
            int ry = SafeReadInt32(mapUnitPtr, _offsetCurrentCoord + 4, out int y);
            if (ry == 0) return (false, 0, 0);
            return (true, x, y);
        }

        /// <summary>
        /// Safe read of PawnController's SelectedPawnInfo backing field.
        /// Returns native PawnInfo pointer, or IntPtr.Zero on fault/null.
        /// Bypasses il2cpp_runtime_invoke which can crash on freed PawnController during enemy turns.
        /// </summary>
        public static IntPtr ReadSelectedPawnInfoPtrSafe(IntPtr pcPtr)
        {
            if (!_available || _offsetSelectedPawnInfo <= 0 || pcPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeReadPtr(pcPtr, _offsetSelectedPawnInfo);
        }

        /// <summary>
        /// Safe read of PawnInfo.PawnUnit field.
        /// Returns native PawnUnit pointer, or IntPtr.Zero on fault/null.
        /// Bypasses il2cpp_runtime_invoke which can crash on freed PawnInfo during enemy turns.
        /// </summary>
        public static IntPtr ReadPawnUnitPtrSafe(IntPtr pawnInfoPtr)
        {
            if (!_available || _offsetPawnUnit <= 0 || pawnInfoPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeReadPtr(pawnInfoPtr, _offsetPawnUnit);
        }

        /// <summary>
        /// Safe read of BattleCheckMenuHandler.curBattleCheckMenuButtonType.
        /// Returns (true, value) on success, (false, 0) on fault.
        /// Prevents uncatchable AV when reading this property on a freed handler
        /// during scene transitions (the IL2CPP property getter dereferences
        /// native pointers that may be invalid on partially-freed objects).
        /// </summary>
        public static (bool ok, int value) ReadBattleCheckBtnTypeSafe(IntPtr handlerPtr)
        {
            if (!_battleCheckFieldAvailable || handlerPtr == IntPtr.Zero)
                return (false, 0);
            int result = SafeReadInt32(handlerPtr, _offsetCurBattleCheckBtnType, out int value);
            return (result != 0, value);
        }

        // ===== PawnUnit safe method calls =====
        // PawnUnit.IsPlayerSide, IsAlive, and PawnData are computed properties
        // (no backing fields). They use il2cpp_runtime_invoke internally, which
        // can crash on freed PawnUnit objects during tactical transitions.
        // SafeCall_PP_Bool distinguishes "returned false" from "AV occurred".

        /// <summary>
        /// Safe call to PawnUnit.get_IsPlayerSide().
        /// Returns (true, boolValue) on success, (false, false) on AV.
        /// </summary>
        public static (bool ok, bool value) ReadIsPlayerSideSafe(IntPtr pawnUnitPtr)
        {
            if (!_pawnMethodsAvailable || pawnUnitPtr == IntPtr.Zero)
                return (false, false);
            int result = SafeCall_PP_Bool(_fnGetIsPlayerSide, pawnUnitPtr, _miGetIsPlayerSide, out int val);
            return (result != 0, val != 0);
        }

        /// <summary>
        /// Safe call to PawnUnit.get_IsAlive().
        /// Returns (true, boolValue) on success, (false, false) on AV.
        /// </summary>
        public static (bool ok, bool value) ReadIsAliveSafe(IntPtr pawnUnitPtr)
        {
            if (!_pawnMethodsAvailable || pawnUnitPtr == IntPtr.Zero)
                return (false, false);
            int result = SafeCall_PP_Bool(_fnGetIsAlive, pawnUnitPtr, _miGetIsAlive, out int val);
            return (result != 0, val != 0);
        }

        /// <summary>
        /// Safe call to PawnUnit.get_PawnData().
        /// Returns the native Pawn pointer, or IntPtr.Zero on AV/null.
        /// </summary>
        public static IntPtr ReadPawnDataSafe(IntPtr pawnUnitPtr)
        {
            if (!_pawnMethodsAvailable || pawnUnitPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeCall_PP(_fnGetPawnData, pawnUnitPtr, _miGetPawnData);
        }

        /// <summary>
        /// Safe call to FloatingCursor.GetPawnHere().
        /// Returns native PawnUnit pointer, or IntPtr.Zero on fault/null.
        /// Uses SafeCall_PP (VEH-protected native call) instead of il2cpp_runtime_invoke.
        /// </summary>
        public static IntPtr GetPawnHereSafe(IntPtr cursorPtr)
        {
            if (!_available || _fnGetPawnHere == IntPtr.Zero || cursorPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeCall_PP(_fnGetPawnHere, cursorPtr, _miGetPawnHere);
        }

        // ===== Battle UI safe field reads =====
        // Read TMP/GameObject field pointers from BattleSceneUI via VEH-protected memory reads.
        // Prevents AV when the BattleSceneUI's native memory is partially corrupted or
        // its TMP children have been destroyed mid-battle-animation.

        /// <summary>
        /// Read an IL2CPP object reference field at the given offset, with VEH protection.
        /// Returns the field's pointer value, or IntPtr.Zero on fault/null.
        /// </summary>
        public static IntPtr ReadFieldPtrSafe(IntPtr objPtr, int fieldOffset)
        {
            if (!_available || objPtr == IntPtr.Zero || fieldOffset <= 0)
                return IntPtr.Zero;
            return SafeReadPtr(objPtr, fieldOffset);
        }

        /// <summary>
        /// Safe read of TMP_Text.text via native method call under VEH.
        /// Returns the IL2CPP string pointer, or IntPtr.Zero on fault/null.
        /// Caller must convert to C# string via IL2CPP.Il2CppStringToManaged.
        /// </summary>
        public static IntPtr ReadTmpTextSafe(IntPtr tmpPtr)
        {
            if (!_tmpTextMethodAvailable || tmpPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeCall_PP(_fnTmpGetText, tmpPtr, _miTmpGetText);
        }

        /// <summary>
        /// Safe read of an IL2CPP string field at a known offset.
        /// Reads the string pointer via VEH-protected SafeReadPtr, probes the
        /// string object, then converts to C# string. Returns null on any failure.
        /// Prevents uncatchable AV from Il2CppStringToManaged reading freed string memory.
        /// </summary>
        public static string ReadIl2CppStringFieldSafe(IntPtr objPtr, int fieldOffset)
        {
            if (!_available || objPtr == IntPtr.Zero || fieldOffset <= 0)
                return null;

            // Read the IL2CPP string pointer at the field offset (VEH protected)
            IntPtr strPtr = SafeReadPtr(objPtr, fieldOffset);
            if (strPtr == IntPtr.Zero) return null;

            return SafeIl2CppStringToManaged(strPtr);
        }

        /// <summary>
        /// Read a TMP field from BattleSceneUI safely: read field pointer, probe it,
        /// then read the text. Returns null on any failure.
        /// </summary>
        public static string ReadBattleTmpFieldText(IntPtr sceneUIPtr, int fieldOffset)
        {
            if (!_available || sceneUIPtr == IntPtr.Zero || fieldOffset <= 0)
                return null;

            IntPtr tmpPtr = SafeReadPtr(sceneUIPtr, fieldOffset);
            if (tmpPtr == IntPtr.Zero) return null;

            // Probe the TMP object
            if (SafeReadPtr(tmpPtr, 0) == IntPtr.Zero) return null;

            // Read text via protected native call
            if (_tmpTextMethodAvailable)
            {
                IntPtr strPtr = SafeCall_PP(_fnTmpGetText, tmpPtr, _miTmpGetText);
                if (strPtr == IntPtr.Zero) return null;
                return SafeIl2CppStringToManaged(strPtr);
            }

            // Fallback: create managed wrapper and read (less safe)
            try
            {
                var tmp = new TextMeshProUGUI(tmpPtr);
                return tmp.text;
            }
            catch { return null; }
        }

        // BattleSceneUI field offset accessors
        public static int OffsetDialogText => _offsetDialogText;
        public static int OffsetPilotName => _offsetPilotName;
        public static int OffsetLeftHpText => _offsetLeftHpText;
        public static int OffsetRightHpText => _offsetRightHpText;
        public static int OffsetLeftEnText => _offsetLeftEnText;
        public static int OffsetRightEnText => _offsetRightEnText;
        public static int OffsetLeftBattleStateText => _offsetLeftBattleStateText;
        public static int OffsetRightBattleStateText => _offsetRightBattleStateText;
        public static int OffsetLeftEnBulletText => _offsetLeftEnBulletText;
        public static int OffsetRightEnBulletText => _offsetRightEnBulletText;
        public static int OffsetDamageCriticalText => _offsetDamageCriticalText;
        public static int OffsetLeftInfoGo => _offsetLeftInfoGo;
        public static int OffsetRightInfoGo => _offsetRightInfoGo;

        // NormalBattleUIHandler field offset accessors
        public static int OffsetNormalDialogText => _offsetNormalDialogText;
        public static int OffsetNormalPilotName => _offsetNormalPilotName;
        public static int OffsetNormalLeftHPText => _offsetNormalLeftHPText;
        public static int OffsetNormalRightHpText => _offsetNormalRightHpText;
        public static int OffsetNormalLeftEnText => _offsetNormalLeftEnText;
        public static int OffsetNormalRightEnText => _offsetNormalRightEnText;

        /// <summary>
        /// Safe call to Robot.GetName() via VEH protection.
        /// Returns C# string or null on fault/null.
        /// </summary>
        public static string ReadRobotNameSafe(IntPtr robotPtr)
        {
            if (!_nameMethodsAvailable || robotPtr == IntPtr.Zero)
                return null;
            if (!ProbeObject(robotPtr))
                return null;

            IntPtr strPtr = SafeCall_PP(_fnRobotGetName, robotPtr, _miRobotGetName);
            if (strPtr == IntPtr.Zero) return null;
            return SafeIl2CppStringToManaged(strPtr);
        }

        /// <summary>
        /// Safe call to Pilot.GetName() via VEH protection.
        /// Returns C# string or null on fault/null.
        /// </summary>
        public static string ReadPilotNameSafe(IntPtr pilotPtr)
        {
            if (!_nameMethodsAvailable || pilotPtr == IntPtr.Zero)
                return null;
            if (!ProbeObject(pilotPtr))
                return null;

            IntPtr strPtr = SafeCall_PP(_fnPilotGetName, pilotPtr, _miPilotGetName);
            if (strPtr == IntPtr.Zero) return null;
            return SafeIl2CppStringToManaged(strPtr);
        }

        /// <summary>
        /// VEH-protected wrapper around IL2CPP.Il2CppStringToManaged.
        /// Probes the string's native memory (klass pointer at offset 0 and
        /// length at offset 0x10) before reading, preventing uncatchable AV
        /// when the IL2CPP string has been freed during scene transitions or
        /// battle animations.
        ///
        /// IL2CPP string layout: [klass:8][monitor:8][length:4][chars...]
        /// Il2CppStringToManaged reads length then length*2 bytes of chars,
        /// which can AV if memory is freed. try-catch CANNOT catch AV in .NET 6.
        /// </summary>
        public static string SafeIl2CppStringToManaged(IntPtr strPtr)
        {
            if (strPtr == IntPtr.Zero) return null;

            // Probe klass pointer at offset 0 (validates basic object structure)
            if (SafeReadPtr(strPtr, 0) == IntPtr.Zero) return null;

            // Probe string length at offset 0x10 (IL2CPP string layout)
            int result = SafeReadInt32(strPtr, 0x10, out int length);
            if (result == 0) return null; // AV reading length - string freed

            // Validate length - reject obviously bad values (freed memory garbage)
            if (length < 0 || length > 100000) return null;

            // Memory validated - safe to read
            try { return IL2CPP.Il2CppStringToManaged(strPtr); }
            catch { return null; }
        }

        // ===== CustomRobotUIHandler Accessors =====

        public static bool CustomRobotFieldsAvailable => _customRobotFieldsAvailable;

        /// <summary>
        /// Read the robotIndex object pointer from a CustomRobotUIHandler.
        /// Returns IntPtr.Zero on fault/null.
        /// </summary>
        public static IntPtr ReadCustomRobotIndexPtr(IntPtr handlerPtr)
        {
            if (!_customRobotFieldsAvailable || handlerPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeReadPtr(handlerPtr, _offsetCustomRobotIndex);
        }

        /// <summary>
        /// Read the Custom object pointer from a CustomRobotUIHandler.
        /// Returns IntPtr.Zero on fault/null.
        /// </summary>
        public static IntPtr ReadCustomCustomPtr(IntPtr handlerPtr)
        {
            if (!_customRobotFieldsAvailable || handlerPtr == IntPtr.Zero)
                return IntPtr.Zero;
            return SafeReadPtr(handlerPtr, _offsetCustomCustom);
        }

        /// <summary>
        /// Read the current robot index (int) from a RobotIndex object.
        /// Returns -1 on fault.
        /// </summary>
        public static int ReadRobotIndexValue(IntPtr robotIndexPtr)
        {
            if (!_customRobotFieldsAvailable || robotIndexPtr == IntPtr.Zero)
                return -1;
            int result = SafeReadInt32(robotIndexPtr, _offsetRobotIndexIndex, out int value);
            return result != 0 ? value : -1;
        }

        /// <summary>
        /// Call RobotIndex.Current() via VEH. Returns the Robot pointer or IntPtr.Zero.
        /// </summary>
        public static IntPtr ReadRobotIndexCurrentPtr(IntPtr robotIndexPtr)
        {
            if (!_customRobotFieldsAvailable || robotIndexPtr == IntPtr.Zero)
                return IntPtr.Zero;
            if (!ProbeObject(robotIndexPtr))
                return IntPtr.Zero;
            return SafeCall_PP(_fnRobotIndexCurrent, robotIndexPtr, _miRobotIndexCurrent);
        }

        /// <summary>
        /// Call RobotIndex.Count() via VEH. Returns count or -1 on fault.
        /// </summary>
        public static int ReadRobotIndexCount(IntPtr robotIndexPtr)
        {
            if (!_customRobotFieldsAvailable || robotIndexPtr == IntPtr.Zero)
                return -1;
            if (!ProbeObject(robotIndexPtr))
                return -1;
            // Count() returns int - SafeCall_PP returns raw RAX value, not boxed object
            IntPtr result = SafeCall_PP(_fnRobotIndexCount, robotIndexPtr, _miRobotIndexCount);
            return (int)(long)result;
        }

        /// <summary>
        /// Call Custom.GetCustomButtons() via VEH. Returns list pointer or IntPtr.Zero.
        /// </summary>
        public static IntPtr ReadCustomButtonsPtr(IntPtr customPtr)
        {
            if (!_customRobotFieldsAvailable || customPtr == IntPtr.Zero)
                return IntPtr.Zero;
            if (_fnCustomGetButtons == IntPtr.Zero)
                return IntPtr.Zero;
            if (!ProbeObject(customPtr))
                return IntPtr.Zero;
            return SafeCall_PP(_fnCustomGetButtons, customPtr, _miCustomGetButtons);
        }

        /// <summary>
        /// Helper: look up a field offset from an IL2CPP class.
        /// Returns -1 if the field is not found.
        /// </summary>
        private static int GetFieldOffsetSafe(IntPtr classPtr, string fieldName)
        {
            IntPtr field = IL2CPP.il2cpp_class_get_field_from_name(classPtr, fieldName);
            if (field == IntPtr.Zero) return -1;
            return (int)IL2CPP.il2cpp_field_get_offset(field);
        }
    }
}
