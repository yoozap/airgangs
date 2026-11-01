using UnityEditor;
using UnityEngine;

namespace TbilisiFly.Probes
{
    /// <summary>
    /// One indirection so an aggregator can run MANY probes in ONE Unity launch.
    ///
    /// The project lock is EXCLUSIVE and shared with several live agents, so the
    /// regression sweep cannot afford one editor launch per probe (86 paired
    /// probes = 86 launches = hours of lock contention). The blocker was that
    /// every probe's Run() ends in EditorApplication.Exit(0), which kills the
    /// editor and with it every probe queued behind it. VerifyPass1/2/3 worked
    /// around that by hand-splitting Run() into Print()+Run() one probe at a
    /// time; only 19 of 116 probes ever got the split.
    ///
    /// Instead: every probe calls ProbeExit.Exit(code). Standalone
    /// (-executeMethod Foo.Run) that is EditorApplication.Exit, byte-for-byte
    /// the old behaviour. Inside an aggregator, Suppress is set and the call
    /// becomes a logged no-op, so the next probe runs. Nonzero codes are still
    /// surfaced as PROBEEXIT lines so a probe that bailed cannot read as a pass.
    /// </summary>
    public static class ProbeExit
    {
        /// <summary>Set by an aggregator for the duration of its run.</summary>
        public static bool Suppress;

        /// <summary>Highest nonzero code seen while suppressed (0 = none).</summary>
        public static int LastNonZero;

        public static void Exit(int code)
        {
            if (!Suppress)
            {
                EditorApplication.Exit(code);
                return;
            }
            if (code != 0)
            {
                LastNonZero = code;
                Debug.Log($"PROBEEXIT suppressed_code={code}");
            }
        }
    }
}
