using System.Diagnostics;

namespace PoFootball.Models
{
    /// <summary>
    /// Informational logging that a release build does not contain at all.
    ///
    /// .claude/rules/performance.md: no Debug.Log in production, stripped by
    /// scripting define rather than a runtime check. [Conditional] removes the CALL
    /// SITE, arguments included, so the string interpolation behind each message
    /// costs a release build nothing — Debug.isDebugBuild around a Debug.Log still
    /// pays for evaluating the branch and leaves the strings in the binary.
    ///
    /// INFO ONLY. Warnings and errors stay on UnityEngine.Debug everywhere:
    /// Systems_StatusHudView counts them through Application.logMessageReceived to
    /// tell a device tester that something went wrong, and stripping them would
    /// blind exactly the build that sheet exists for. The FINAL and REALISM lines
    /// in Systems_GameFlowSystem stay on Debug.Log as well, because
    /// Editor_RealismEval and a bug report from a device both read them.
    /// </summary>
    public static class Systems_Log
    {
        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Info(string message)
        {
            UnityEngine.Debug.Log(message);
        }
    }
}
