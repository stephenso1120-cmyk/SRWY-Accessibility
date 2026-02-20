/*
 * SRWYSafe.dll - SEH-protected wrappers for IL2CPP native calls.
 *
 * Uses Vectored Exception Handler + setjmp/longjmp to catch
 * AccessViolationException during IL2CPP pointer access.
 * .NET 6 cannot catch AV; this native DLL can.
 *
 * Also includes game crash recovery for a known IL2CPP runtime bug
 * at GameAssembly.dll+0x338959 (class init null pointer dereference).
 *
 * Build (MinGW-w64 x86_64):
 *   gcc -shared -O2 -o SRWYSafe.dll SRWYSafe.c
 *
 * Deploy: copy to game root alongside Tolk.dll
 */

#include <windows.h>
#include <setjmp.h>

/* Thread-local storage for safe-mode flag and recovery buffer */
static DWORD g_tls_safe_mode = TLS_OUT_OF_INDEXES;
static DWORD g_tls_jmp_buf   = TLS_OUT_OF_INDEXES;

/* VEH handle for cleanup */
static PVOID g_veh_handle = NULL;

/*
 * Game crash recovery for known IL2CPP runtime bug.
 *
 * The game crashes at GameAssembly.dll+0x338959 with EXCEPTION_ACCESS_VIOLATION.
 * This is an IL2CPP runtime class initialization wrapper that dereferences a
 * null/dangling Il2CppClass pointer: test byte [rcx+4Ch], 10h
 *
 * The function (at +0x338930) has a clean "return 0" path at +0x33898B
 * (xor eax,eax followed by register restore epilogue). We redirect
 * execution there to let the caller handle the null return gracefully.
 *
 * This crash occurs ~1-2x per hour during tactical gameplay and is 100%
 * reproducible across sessions (same fault bucket hash).
 */
static ULONG_PTR g_game_asm_base = 0;
static ULONG_PTR g_game_asm_crash_addr = 0;
static ULONG_PTR g_game_asm_return_zero = 0;
static volatile LONG g_game_crash_recoveries = 0;

/*
 * Vectored Exception Handler.
 * Called BEFORE frame-based handlers when any exception occurs.
 * If we're in safe mode and the exception is AV, longjmp to recovery.
 * Otherwise, pass the exception through to normal handlers.
 */
static LONG WINAPI SafeVehHandler(EXCEPTION_POINTERS *ep)
{
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_ACCESS_VIOLATION)
        return EXCEPTION_CONTINUE_SEARCH;

    /*
     * Game crash recovery: check if this is the known IL2CPP runtime bug.
     * Must check BEFORE safe_mode because game crashes happen outside
     * our SafeCall wrappers (safe_mode is not set).
     */
    if (g_game_asm_crash_addr != 0 &&
        ep->ContextRecord->Rip == g_game_asm_crash_addr)
    {
        /* Redirect to the function's "return 0" path */
        ep->ContextRecord->Rip = g_game_asm_return_zero;
        InterlockedIncrement(&g_game_crash_recoveries);
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    /* Check if this thread is in safe mode */
    LPVOID mode = TlsGetValue(g_tls_safe_mode);
    if (mode == NULL)
        return EXCEPTION_CONTINUE_SEARCH;

    /* Get the recovery jmp_buf for this thread */
    jmp_buf *buf = (jmp_buf *)TlsGetValue(g_tls_jmp_buf);
    if (buf == NULL)
        return EXCEPTION_CONTINUE_SEARCH;

    /* Clear safe mode BEFORE longjmp to prevent re-entry */
    TlsSetValue(g_tls_safe_mode, NULL);

    /* Jump back to the setjmp point with value 1 (AV caught) */
    longjmp(*buf, 1);

    /* Never reached */
    return EXCEPTION_CONTINUE_SEARCH;
}

/*
 * Initialize: register VEH, allocate TLS slots.
 * Call once at mod startup.
 * Returns 1 on success, 0 on failure.
 */
__declspec(dllexport) int __cdecl SafeCall_Init(void)
{
    if (g_veh_handle != NULL)
        return 1; /* Already initialized */

    g_tls_safe_mode = TlsAlloc();
    if (g_tls_safe_mode == TLS_OUT_OF_INDEXES)
        return 0;

    g_tls_jmp_buf = TlsAlloc();
    if (g_tls_jmp_buf == TLS_OUT_OF_INDEXES)
    {
        TlsFree(g_tls_safe_mode);
        g_tls_safe_mode = TLS_OUT_OF_INDEXES;
        return 0;
    }

    /* Register as FIRST handler (priority=1) so we run before others */
    g_veh_handle = AddVectoredExceptionHandler(1, SafeVehHandler);
    if (g_veh_handle == NULL)
    {
        TlsFree(g_tls_safe_mode);
        TlsFree(g_tls_jmp_buf);
        g_tls_safe_mode = TLS_OUT_OF_INDEXES;
        g_tls_jmp_buf = TLS_OUT_OF_INDEXES;
        return 0;
    }

    /*
     * Set up game crash recovery for the known IL2CPP runtime bug.
     * Find GameAssembly.dll base address and verify the crash/recovery sites.
     */
    HMODULE hGameAsm = GetModuleHandleA("GameAssembly");
    if (hGameAsm != NULL)
    {
        ULONG_PTR base = (ULONG_PTR)hGameAsm;
        BYTE *crash_site = (BYTE *)(base + 0x338959);
        BYTE *return_zero = (BYTE *)(base + 0x33898B);

        /*
         * Verify crash site: F6 41 4C 10 = test byte [rcx+4Ch], 10h
         * Verify return-zero site: 31 C0 or 33 C0 = xor eax, eax
         */
        if (crash_site[0] == 0xF6 && crash_site[1] == 0x41 &&
            crash_site[2] == 0x4C && crash_site[3] == 0x10 &&
            ((return_zero[0] == 0x31 || return_zero[0] == 0x33) &&
             return_zero[1] == 0xC0))
        {
            g_game_asm_base = base;
            g_game_asm_crash_addr = (ULONG_PTR)crash_site;
            g_game_asm_return_zero = (ULONG_PTR)return_zero;
        }
    }

    return 1;
}

/*
 * Query the number of game crash recoveries since startup.
 * Returns the total count of times we recovered from the
 * GameAssembly.dll class init crash.
 */
__declspec(dllexport) int __cdecl SafeCall_GetGameCrashRecoveries(void)
{
    return (int)g_game_crash_recoveries;
}

/*
 * Query whether game crash recovery is active.
 * Returns 1 if the crash/recovery addresses were verified, 0 if not.
 */
__declspec(dllexport) int __cdecl SafeCall_IsGameCrashRecoveryActive(void)
{
    return (g_game_asm_crash_addr != 0) ? 1 : 0;
}

/*
 * Shutdown: remove VEH, free TLS slots.
 * Call at mod unload.
 */
__declspec(dllexport) void __cdecl SafeCall_Shutdown(void)
{
    if (g_veh_handle != NULL)
    {
        RemoveVectoredExceptionHandler(g_veh_handle);
        g_veh_handle = NULL;
    }
    if (g_tls_safe_mode != TLS_OUT_OF_INDEXES)
    {
        TlsFree(g_tls_safe_mode);
        g_tls_safe_mode = TLS_OUT_OF_INDEXES;
    }
    if (g_tls_jmp_buf != TLS_OUT_OF_INDEXES)
    {
        TlsFree(g_tls_jmp_buf);
        g_tls_jmp_buf = TLS_OUT_OF_INDEXES;
    }
}

/*
 * IL2CPP instance method call with 0 extra args.
 * Signature: void* fn(void* thisPtr, void* methodInfo)
 * Used for: GetCurrentInputBehaviour()
 *
 * Returns the method's return value, or NULL if AV occurred.
 */
typedef void *(__cdecl *fn_pp)(void *, void *);

__declspec(dllexport) void *__cdecl SafeCall_PP(
    void *fnPtr, void *thisPtr, void *methodInfo)
{
    jmp_buf buf;

    /* Set up recovery point */
    TlsSetValue(g_tls_jmp_buf, &buf);
    TlsSetValue(g_tls_safe_mode, (LPVOID)1);

    if (setjmp(buf) != 0)
    {
        /* AV was caught by VEH handler, longjmp'd here */
        return NULL;
    }

    void *result = ((fn_pp)fnPtr)(thisPtr, methodInfo);

    /* Clear safe mode after successful call */
    TlsSetValue(g_tls_safe_mode, NULL);
    return result;
}

/*
 * IL2CPP instance method call with 1 int arg.
 * Signature: void* fn(void* thisPtr, int arg, void* methodInfo)
 * Used for: GetInputBehaviour(InputMode mode)
 *
 * Returns the method's return value, or NULL if AV occurred.
 */
typedef void *(__cdecl *fn_pip)(void *, int, void *);

__declspec(dllexport) void *__cdecl SafeCall_PIP(
    void *fnPtr, void *thisPtr, int arg, void *methodInfo)
{
    jmp_buf buf;

    TlsSetValue(g_tls_jmp_buf, &buf);
    TlsSetValue(g_tls_safe_mode, (LPVOID)1);

    if (setjmp(buf) != 0)
    {
        return NULL;
    }

    void *result = ((fn_pip)fnPtr)(thisPtr, arg, methodInfo);

    TlsSetValue(g_tls_safe_mode, NULL);
    return result;
}

/*
 * Safe pointer read at (basePtr + offset).
 * Used for: controlBehaviour field read, .Pointer access.
 *
 * Returns the pointer value, or NULL if AV occurred.
 */
__declspec(dllexport) void *__cdecl SafeReadPtr(void *basePtr, int offset)
{
    jmp_buf buf;

    TlsSetValue(g_tls_jmp_buf, &buf);
    TlsSetValue(g_tls_safe_mode, (LPVOID)1);

    if (setjmp(buf) != 0)
    {
        return NULL;
    }

    void *result = *(void **)((char *)basePtr + offset);

    TlsSetValue(g_tls_safe_mode, NULL);
    return result;
}

/*
 * Safe int32 read at (basePtr + offset).
 * Used for: currentCursorIndex field read.
 *
 * Returns 1 on success (value written to *outValue), 0 on AV.
 */
__declspec(dllexport) int __cdecl SafeReadInt32(
    void *basePtr, int offset, int *outValue)
{
    jmp_buf buf;

    TlsSetValue(g_tls_jmp_buf, &buf);
    TlsSetValue(g_tls_safe_mode, (LPVOID)1);

    if (setjmp(buf) != 0)
    {
        if (outValue) *outValue = 0;
        return 0;
    }

    int val = *(int *)((char *)basePtr + offset);

    TlsSetValue(g_tls_safe_mode, NULL);
    if (outValue) *outValue = val;
    return 1;
}

/*
 * IL2CPP instance method call with 0 extra args, returning bool.
 * Signature: bool fn(void* thisPtr, void* methodInfo)
 * Used for: PawnUnit.get_IsPlayerSide(), PawnUnit.get_IsAlive()
 *
 * Unlike SafeCall_PP (which can't distinguish "returned false" from "AV"),
 * this writes the bool result to *outValue and returns 1=success, 0=AV.
 */
__declspec(dllexport) int __cdecl SafeCall_PP_Bool(
    void *fnPtr, void *thisPtr, void *methodInfo, int *outValue)
{
    jmp_buf buf;

    TlsSetValue(g_tls_jmp_buf, &buf);
    TlsSetValue(g_tls_safe_mode, (LPVOID)1);

    if (setjmp(buf) != 0)
    {
        if (outValue) *outValue = 0;
        return 0; /* AV caught */
    }

    /* Call via fn_pp (returns void*). The actual native function returns
     * bool (1 byte in AL). Upper bytes of RAX may contain garbage because
     * fn_pp reads the full 8-byte register but the callee only sets AL.
     * Mask to low byte to extract the correct boolean value. */
    void *raw = ((fn_pp)fnPtr)(thisPtr, methodInfo);

    TlsSetValue(g_tls_safe_mode, NULL);
    if (outValue) *outValue = ((size_t)raw & 0xFF) ? 1 : 0;
    return 1; /* success */
}

BOOL WINAPI DllMain(HINSTANCE hinstDLL, DWORD fdwReason, LPVOID lpvReserved)
{
    (void)hinstDLL;
    (void)lpvReserved;

    if (fdwReason == DLL_THREAD_DETACH)
    {
        /* Clean up TLS for detaching threads */
        if (g_tls_safe_mode != TLS_OUT_OF_INDEXES)
            TlsSetValue(g_tls_safe_mode, NULL);
        if (g_tls_jmp_buf != TLS_OUT_OF_INDEXES)
            TlsSetValue(g_tls_jmp_buf, NULL);
    }
    return TRUE;
}
