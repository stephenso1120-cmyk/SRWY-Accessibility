using System;
using Il2CppInterop.Runtime.InteropTypes;

namespace SRWYAccess
{
    /// <summary>
    /// Extension methods for Il2Cpp objects to reduce code duplication.
    /// Provides common validation patterns used throughout the codebase.
    /// </summary>
    internal static class Il2CppExtensions
    {
        /// <summary>
        /// Checks if an Il2Cpp object is valid and safe to access.
        /// Combines three checks: Unity null, native pointer, and VEH probe.
        /// </summary>
        /// <param name="obj">The Il2Cpp object to validate</param>
        /// <param name="probeNative">If true, performs VEH probe (slower but safer)</param>
        /// <returns>True if object is safe to access</returns>
        public static bool IsValidIl2CppObject(this Il2CppObjectBase obj, bool probeNative = true)
        {
            // Check 1: Unity's overloaded == operator (checks managed wrapper)
            if ((object)obj == null)
                return false;

            // Check 2: Native pointer check (fast, but may point to freed memory)
            if (obj.Pointer == IntPtr.Zero)
                return false;

            // Check 3: VEH probe (catches access violations, but has overhead)
            if (probeNative && SafeCall.IsAvailable)
            {
                if (!SafeCall.ProbeObject(obj.Pointer))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Fast validation without VEH probe.
        /// Use when performance is critical and AV risk is low.
        /// </summary>
        public static bool IsValidIl2CppObjectFast(this Il2CppObjectBase obj)
        {
            return (object)obj != null && obj.Pointer != IntPtr.Zero;
        }

        /// <summary>
        /// Validates and returns the object, or null if invalid.
        /// Useful for chaining: var handler = obj.ValidateOrNull()?.someField;
        /// </summary>
        public static T ValidateOrNull<T>(this T obj, bool probeNative = true) where T : Il2CppObjectBase
        {
            return obj.IsValidIl2CppObject(probeNative) ? obj : null;
        }
    }
}
