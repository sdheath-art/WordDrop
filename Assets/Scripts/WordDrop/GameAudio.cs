using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Central SFX manager for WordDrop. Loads all game sounds from Resources/SFX
    /// and provides Play methods for each game event. Supports volume control and mute toggle.
    /// Persists via PlayerPrefs: "SFXVolume" (0-1) and "SFXMuted" (0/1).
    /// </summary>
    public class GameAudio : MonoBehaviour
    {
        public static GameAudio Instance { get; private set; }

        // ── Volume ─────────────────────────────────────────────────────────────
        private float _volume = 1f;
        private bool  _muted  = false;
        private const string PREF_VOLUME = "SFXVolume";
        private const string PREF_MUTED  = "SFXMuted";

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(PREF_VOLUME, _volume);
                PlayerPrefs.Save();
            }
        }

        public bool Muted
        {
            get => _muted;
            set
            {
                _muted = value;
                PlayerPrefs.SetInt(PREF_MUTED, _muted ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        // ── Clips ──────────────────────────────────────────────────────────────
        private AudioClip _tileDrop;
        private AudioClip _tileDropAlt;
        private AudioClip _tileSelect;
        private AudioClip _tileSelectAlt;
        private AudioClip _wordScored;
        private AudioClip _wordScoredAlt;
        private AudioClip _tilePrimed;
        private AudioClip _tilePrimedAlt;
        private AudioClip _detonation;
        private AudioClip _detonationAlt;
        private AudioClip _chainReaction;
        private AudioClip _chainReactionAlt;
        private AudioClip _meltdown;
        private AudioClip _meltdownAlt;
        private AudioClip _uiClick;
        private AudioClip _uiClickAlt;
        private AudioClip _lightTick;
        private AudioClip _reorderTick;
        private AudioClip[] _whooshClips;
        private AudioClip _gameOver;
        private AudioClip _swap;
        private AudioClip _rewrite;
        private AudioClip _chargeBack;
        private AudioClip _scoreTick;
        private AudioClip _scoreMedium;
        private AudioClip _scoreBig;
        private AudioClip _scoreMassive;
        private AudioClip _menuAppear;
        private AudioClip _wordPopup;
        private AudioClip _wordPopupAlt;
        private AudioClip _bonusPopup;
        private AudioClip _bonusPopupAlt;
        private AudioClip _shuffle;
        private AudioClip _shuffleAlt;

        private AudioSource _source;
        private AudioSource _pitchedSource; // separate source for pitch-shifted sounds

        // ── Bootstrap ──────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("GameAudio");
                go.AddComponent<GameAudio>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _pitchedSource = gameObject.AddComponent<AudioSource>();
            _pitchedSource.playOnAwake = false;

            // Load saved prefs
            _volume = PlayerPrefs.GetFloat(PREF_VOLUME, 1f);
            _muted  = PlayerPrefs.GetInt(PREF_MUTED, 0) == 1;

            // Load all clips
            _tileDrop        = Resources.Load<AudioClip>("SFX/tile_drop");
            _tileDropAlt     = Resources.Load<AudioClip>("SFX/tile_drop_alt");
            _tileSelect      = Resources.Load<AudioClip>("SFX/tile_select");
            _tileSelectAlt   = Resources.Load<AudioClip>("SFX/tile_select_alt");
            _wordScored      = Resources.Load<AudioClip>("SFX/word_scored");
            _wordScoredAlt   = Resources.Load<AudioClip>("SFX/word_scored_alt");
            _tilePrimed      = Resources.Load<AudioClip>("SFX/tile_primed");
            _tilePrimedAlt   = Resources.Load<AudioClip>("SFX/tile_primed_alt");
            _detonation      = Resources.Load<AudioClip>("SFX/detonation");
            _detonationAlt   = Resources.Load<AudioClip>("SFX/detonation_alt");
            _chainReaction   = Resources.Load<AudioClip>("SFX/chain_reaction");
            _chainReactionAlt= Resources.Load<AudioClip>("SFX/chain_reaction_alt");
            _meltdown        = Resources.Load<AudioClip>("SFX/meltdown");
            _meltdownAlt     = Resources.Load<AudioClip>("SFX/meltdown_alt");
            _uiClick         = Resources.Load<AudioClip>("SFX/ui_click");
            _uiClickAlt      = Resources.Load<AudioClip>("SFX/ui_click_alt");
            _lightTick       = Resources.Load<AudioClip>("SFX/tick_woody"); // Wood block hit small — column cycling
            _reorderTick     = Resources.Load<AudioClip>("SFX/tick_reorder"); // Gun button press — reorder swap
            _whooshClips = new[] {
                Resources.Load<AudioClip>("SFX/whoosh"),
                Resources.Load<AudioClip>("SFX/whoosh_alt1"),
                Resources.Load<AudioClip>("SFX/whoosh_alt2"),
                Resources.Load<AudioClip>("SFX/whoosh_alt3"),
            };
            _gameOver        = Resources.Load<AudioClip>("SFX/game_over");
            _swap            = Resources.Load<AudioClip>("SFX/swap");
            _rewrite         = Resources.Load<AudioClip>("SFX/rewrite");
            _chargeBack      = Resources.Load<AudioClip>("SFX/charge_back");
            _scoreTick       = Resources.Load<AudioClip>("SFX/score_tick");
            _scoreMedium     = Resources.Load<AudioClip>("SFX/score_medium");
            _scoreBig        = Resources.Load<AudioClip>("SFX/score_big");
            _scoreMassive    = Resources.Load<AudioClip>("SFX/score_massive");
            _menuAppear      = Resources.Load<AudioClip>("SFX/menu_appear");
            _wordPopup       = Resources.Load<AudioClip>("SFX/word_popup");
            _wordPopupAlt    = Resources.Load<AudioClip>("SFX/word_popup_alt");
            _bonusPopup      = Resources.Load<AudioClip>("SFX/bonus_popup");
            _bonusPopupAlt   = Resources.Load<AudioClip>("SFX/bonus_popup_alt");
            _shuffle         = Resources.Load<AudioClip>("SFX/shuffle");
            _shuffleAlt      = Resources.Load<AudioClip>("SFX/shuffle_alt");

            int count = 0;
            if (_tileDrop != null) count++;
            if (_tileSelect != null) count++;
            if (_wordScored != null) count++;
            if (_tilePrimed != null) count++;
            if (_detonation != null) count++;
            if (_chainReaction != null) count++;
            if (_meltdown != null) count++;
            if (_uiClick != null) count++;
            if (_whooshClips != null && _whooshClips.Length > 0 && _whooshClips[0] != null) count++;
            if (_gameOver != null) count++;
            if (_swap != null) count++;
            if (_rewrite != null) count++;
            Debug.Log($"[GameAudio] Loaded {count}/12 SFX clips. Vol={_volume:F1} Muted={_muted}");
        }

        // ── Core play method ───────────────────────────────────────────────────

        private void Play(AudioClip clip, float volumeMult = 1f, float pitch = 1f)
        {
            if (_muted || clip == null) return;
            if (Mathf.Approximately(pitch, 1f))
            {
                // Normal pitch — use main source (no pitch interference)
                if (_source != null)
                    _source.PlayOneShot(clip, _volume * volumeMult);
            }
            else
            {
                // Pitched sound — use separate source so it doesn't corrupt main
                if (_pitchedSource != null)
                {
                    _pitchedSource.pitch = pitch;
                    _pitchedSource.PlayOneShot(clip, _volume * volumeMult);
                }
            }
        }

        private AudioClip PickRandom(AudioClip a, AudioClip b)
        {
            if (b == null) return a;
            return Random.value > 0.5f ? a : b;
        }

        // ── Public play methods ────────────────────────────────────────────────

        public void PlayTileDrop()
        {
            Play(PickRandom(_tileDrop, _tileDropAlt), 0.7f);
        }

        public void PlayTileSelect()
        {
            Play(PickRandom(_tileSelect, _tileSelectAlt), 0.5f);
        }

        public void PlayWordScored()
        {
            Play(PickRandom(_wordScored, _wordScoredAlt), 0.8f);
        }

        public void PlayTilePrimed()
        {
            Play(PickRandom(_tilePrimed, _tilePrimedAlt), 0.6f);
        }

        public void PlayDetonation(int chainDepth = 0)
        {
            // Pitch up slightly with chain depth for escalation
            float pitch = Mathf.Min(1f + chainDepth * 0.08f, 1.5f);
            Play(PickRandom(_detonation, _detonationAlt), 0.85f, pitch);
        }

        public void PlayChainReaction()
        {
            Play(PickRandom(_chainReaction, _chainReactionAlt), 0.9f);
        }

        public void PlayMeltdown()
        {
            Play(PickRandom(_meltdown, _meltdownAlt), 1f);
        }

        /// <summary>Rising tension SFX for meltdown build-up. Uses pitched-up detonation as placeholder.</summary>
        public void PlayMeltdownRising()
        {
            // Use detonation clip pitched down for a rumble feel. Replace with dedicated clip later.
            var clip = PickRandom(_detonation, _detonationAlt);
            if (clip != null) Play(clip, 0.5f, 0.55f); // low pitch = deep rumble
        }

        public void PlayUIClick()
        {
            Play(PickRandom(_uiClick, _uiClickAlt), 0.5f);
        }

        public void PlayLightTick()
        {
            Play(_lightTick != null ? _lightTick : _uiClickAlt, 0.25f);
        }

        public void PlayReorderTick()
        {
            Play(_reorderTick != null ? _reorderTick : _uiClickAlt, 0.3f);
        }

        public void PlayWhoosh()
        {
            if (_whooshClips == null || _whooshClips.Length == 0) return;
            var clip = _whooshClips[Random.Range(0, _whooshClips.Length)];
            Play(clip, 0.6f);
        }

        /// <summary>Deep rumble for rising rows — whoosh pitched down.</summary>
        public void PlayRisingRow()
        {
            if (_whooshClips == null || _whooshClips.Length == 0) return;
            var clip = _whooshClips[Random.Range(0, _whooshClips.Length)];
            Play(clip, 0.8f, 0.6f); // low pitch = rumble feel
        }

        public void PlayGameOver()
        {
            Play(_gameOver, 0.8f);
        }

        public void PlaySwap()
        {
            Play(_swap, 0.7f);
        }

        public void PlayRewrite()
        {
            Play(_rewrite, 0.7f);
        }

        public void PlayChargeBack()
        {
            Play(_chargeBack, 0.8f);
        }

        public void PlayScoreTick(float pitch = 1f)
        {
            Play(_scoreTick, 0.2f, pitch);
        }

        /// <summary>
        /// Plays a tiered impact sound based on points scored.
        /// Small (1-7): just the tick. Medium (8-15): chime. Big (16-24): shimmer. Massive (25+): treasure hit.
        /// </summary>
        public void PlayScoreImpact(int points)
        {
            if (points >= 25)
                Play(_scoreMassive, 1f);
            else if (points >= 16)
                Play(_scoreBig, 0.85f);
            else if (points >= 8)
                Play(_scoreMedium, 0.7f);
            // Under 8: no extra impact, just the count-up ticks
        }

        public void PlayMenuAppear()
        {
            Play(_menuAppear, 0.7f);
        }

        public void PlayWordPopup()
        {
            Play(PickRandom(_wordPopup, _wordPopupAlt), 0.5f);
        }

        public void PlayBonusPopup()
        {
            Play(PickRandom(_bonusPopup, _bonusPopupAlt), 0.6f);
        }

        public void PlayShuffle()
        {
            Play(PickRandom(_shuffle, _shuffleAlt), 0.7f);
        }
    }
}
