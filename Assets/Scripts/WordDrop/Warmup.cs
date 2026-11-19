using System.Collections;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// 2026-11-18 FIRST-USE HITCHES (Spencer, phone: "on the making a bomb animation ... im losing frames" - "probably just the
    /// first time cause it fired again and it was fine"). Power-up art and the sound kit were loaded from disk at the moment
    /// they were first needed: the kit's clips are compressed and not preloaded, so the first play of each decodes on that
    /// frame, and each power-up's picture is read on its first appearance. On the Mac that is invisible; on the phone it is a
    /// dropped frame or two right on the moment that should feel best.
    /// This loads them shortly after the game starts, a few per frame, so nothing is first-used mid-play.
    /// </summary>
    public static class Warmup
    {
        public static bool ON = true;
        public static int  PER_FRAME = 2;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!ON) return;
            var go = new GameObject("Warmup") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go); go.AddComponent<Runner>();
        }

        private class Runner : MonoBehaviour
        {
            private IEnumerator Start()
            {
                yield return new WaitForSecondsRealtime(1.5f);   // let the first screen settle
                int n = 0;
                // sound kit: read + decode now, not on the first play
                // 11-19: ALL of Resources/Audio (it is sound effects only, ~13 MB), not just the kit - the bomb's explosion_big,
                // rocket_fire, the whooshes and stage-up stingers live outside the kit and were still decoded on their first
                // play (Spencer: "the bomb is often off in timing with the sound").
                var clips = Resources.LoadAll<AudioClip>("Audio");
                foreach (var c in clips)
                {
                    if (c != null && c.loadState == AudioDataLoadState.Unloaded) c.LoadAudioData();
                    if (++n % PER_FRAME == 0) yield return null;
                }
                // power-up pictures (rocket, bomb, orb, tome, dynamite and their parts)
                Resources.LoadAll<Texture2D>("FX");   // 11-19: blast cloud / puff / wisp / chunk textures (BLAST_V2)
                var art = Resources.LoadAll<Texture2D>("Specials");
                for (int i = 0; i < art.Length; i++) if (i % PER_FRAME == 0) yield return null;
                foreach (int kind in new[] { RulesEngine.SPECIAL_ROCKET_H, RulesEngine.SPECIAL_ROCKET_V, RulesEngine.SPECIAL_BOMB, RulesEngine.SPECIAL_DISCO, RulesEngine.SPECIAL_AIRPLANE, RulesEngine.SPECIAL_TNT })
                { Tile.SpecialIcon(kind); yield return null; }
                WDLog.Log($"[Warmup] {clips.Length} sound clip(s), {art.Length} power-up texture(s) loaded ahead of first use");
                Destroy(gameObject);
            }
        }
    }
}
