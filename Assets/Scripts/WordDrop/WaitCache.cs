using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    public static class WaitCache
    {
        private static readonly Dictionary<float, WaitForSeconds> _cache = new Dictionary<float, WaitForSeconds>();

        public static WaitForSeconds Get(float seconds)
        {
            if (!_cache.TryGetValue(seconds, out var wait))
            {
                wait = new WaitForSeconds(seconds);
                _cache[seconds] = wait;
            }
            return wait;
        }
    }
}
