using System.Collections;
using UnityEngine;

namespace TbilisiFly.Tools
{
    /// <summary>
    /// Ticks the memory census in a running player: once as soon as the first
    /// scene is up, then again at 60 s when the city, the fleet and the hero
    /// are all built. Spawned only by MemoryCensus.Install under TF_MEMCENSUS=1,
    /// so a normal run never has it.
    ///
    /// ONE MonoBehaviour PER FILE, NAMED AFTER IT — this class exists in its own
    /// file for that reason alone (see the port's AddComponent trap).
    /// </summary>
    public sealed class MemoryCensusRunner : MonoBehaviour
    {
        void Start() { StartCoroutine(Run()); }

        IEnumerator Run()
        {
            yield return null;
            MemoryCensus.Print("startup");
            yield return new WaitForSecondsRealtime(60f);
            MemoryCensus.Print("t60");
        }
    }
}
