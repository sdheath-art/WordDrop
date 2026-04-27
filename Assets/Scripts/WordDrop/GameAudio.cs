using System.Collections.Generic;
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
        private float _volume = 0.4f; // lower default — phone speakers are loud
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
        private AudioClip[] _tileDropVariants;
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
        // _scoreMedium removed — PlayScoreImpact mid-tier now routes to _scoreTick instead.
        private AudioClip _scoreBig;
        private AudioClip _scoreMassive;
        private AudioClip _menuAppear;
        private AudioClip _wordPopup;
        private AudioClip _wordPopupAlt;
        private AudioClip _bonusPopup;
        private AudioClip _bonusPopupAlt;
        private AudioClip _shuffle;
        private AudioClip _shuffleAlt;
        private AudioClip _personalBest;
        private AudioClip _goldSpawn;

        // ── New SFX (April 7, 2026) ──────────────────────────────────────────
        private AudioClip _chainRumble;
        private AudioClip[] _deepImpactVariants;
        private AudioClip[] _scorePowerupVariants;
        private AudioClip[] _poofExplosionVariants;
        private AudioClip _eventRising;
        private AudioClip _eventRisingAlt;
        private AudioClip[] _wordMatchVariants;
        private AudioClip[] _buttonClickVariants;
        private AudioClip _confirmNewgame;
        private AudioClip _confirmNewgameAlt;
        private AudioClip[] _scoreSuckVariants;
        private AudioClip[] _scoreChimesVariants;
        private AudioClip[] _goldSpawnNewVariants;
        private AudioClip _sparkleWhoosh;
        private AudioClip _sparkleWhooshAlt;
        private AudioClip[] _whooshBigVariants;
        private AudioClip[] _whooshFastVariants;
        private AudioClip[] _cardDealVariants;
        private AudioClip[] _cardDropHandVariants;

        private AudioSource _source;
        private AudioSource _pitchedSource; // separate source for pitch-shifted sounds
        private AudioSource _musicSource;   // looping music layer (Survival BGM)

        // ── Music clips ────────────────────────────────────────────────────────
        private AudioClip _survivalMusic;
        private AudioClip _survivalMusic2;        // plays after track 1 finishes; loops thereafter
        private Coroutine _musicSequenceRoutine;  // watches track 1 → switches to track 2 on end
        private AudioClip _menuMusic;             // main-menu loop (Monkeys Spinning Monkeys)

        // Music layer volume + mute, persisted separately from SFX so a player
        // can turn music off without losing feedback sounds.
        private const string PREF_MUSIC_VOLUME = "MusicVolume";
        private const string PREF_MUSIC_MUTED  = "MusicMuted";
        private float _musicVolume = 0.35f;
        private bool  _musicMuted  = false;

        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(PREF_MUSIC_VOLUME, _musicVolume);
                PlayerPrefs.Save();
                if (_musicSource != null)
                    _musicSource.volume = _musicMuted ? 0f : _musicVolume;
            }
        }

        public bool MusicMuted
        {
            get => _musicMuted;
            set
            {
                _musicMuted = value;
                PlayerPrefs.SetInt(PREF_MUSIC_MUTED, _musicMuted ? 1 : 0);
                PlayerPrefs.Save();
                if (_musicSource != null)
                    _musicSource.volume = _musicMuted ? 0f : _musicVolume;
            }
        }

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
            // Dedicated music layer — looped, non-pitched, own volume.
            _musicSource = gameObject.AddComponent<AudioSource>();
            _musicSource.playOnAwake = false;
            _musicSource.loop        = true;
            _musicSource.priority    = 64;   // behind SFX (default 128)

            // Load saved prefs
            _volume      = PlayerPrefs.GetFloat(PREF_VOLUME, 0.4f);
            _muted       = PlayerPrefs.GetInt(PREF_MUTED, 0) == 1;
            _musicVolume = PlayerPrefs.GetFloat(PREF_MUSIC_VOLUME, 0.35f);
            _musicMuted  = PlayerPrefs.GetInt(PREF_MUSIC_MUTED, 0) == 1;

            // Load all clips
            _tileDrop        = Resources.Load<AudioClip>("SFX/tile_drop");
            _tileDropAlt     = Resources.Load<AudioClip>("SFX/tile_drop_alt");
            _tileDropVariants = new[] {
                Resources.Load<AudioClip>("Audio/tile_land_1"),
                Resources.Load<AudioClip>("Audio/tile_land_2"),
                Resources.Load<AudioClip>("Audio/tile_land_3"),
                Resources.Load<AudioClip>("Audio/tile_land_4"),
                Resources.Load<AudioClip>("Audio/tile_land_5"),
            };
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
            _scoreBig        = Resources.Load<AudioClip>("SFX/score_big");
            _scoreMassive    = Resources.Load<AudioClip>("SFX/score_massive");
            _menuAppear      = Resources.Load<AudioClip>("SFX/menu_appear");
            _wordPopup       = Resources.Load<AudioClip>("SFX/word_popup");
            _wordPopupAlt    = Resources.Load<AudioClip>("SFX/word_popup_alt");
            _bonusPopup      = Resources.Load<AudioClip>("SFX/bonus_popup");
            _bonusPopupAlt   = Resources.Load<AudioClip>("SFX/bonus_popup_alt");
            _shuffle         = Resources.Load<AudioClip>("SFX/shuffle");
            _shuffleAlt      = Resources.Load<AudioClip>("SFX/shuffle_alt");
            _personalBest    = Resources.Load<AudioClip>("SFX/personal_best");
            _goldSpawn       = Resources.Load<AudioClip>("SFX/gold_spawn");

            // Music tracks — drop the audio file at
            // Assets/Resources/Music/survival_loop.{mp3,ogg,wav} to wire.
            // If not present, Resources.Load returns null and PlaySurvivalMusic
            // becomes a quiet no-op (logs a one-time warning).
            _survivalMusic   = Resources.Load<AudioClip>("Music/survival_loop");
            _survivalMusic2  = Resources.Load<AudioClip>("Music/survival_loop_2");
            _menuMusic       = Resources.Load<AudioClip>("Music/menu_loop");

            // New SFX (April 7)
            _chainRumble     = Resources.Load<AudioClip>("SFX/chain_rumble");
            _deepImpactVariants = new[] {
                Resources.Load<AudioClip>("SFX/deep_impact"),
            };
            _scorePowerupVariants = new[] {
                Resources.Load<AudioClip>("SFX/score_powerup"),
            };
            _poofExplosionVariants = new[] {
                Resources.Load<AudioClip>("SFX/poof_explosion"),
            };
            _eventRising     = Resources.Load<AudioClip>("SFX/event_rising");
            _eventRisingAlt  = null; // removed — was silent
            _wordMatchVariants = new[] {
                Resources.Load<AudioClip>("SFX/word_match"),
                Resources.Load<AudioClip>("SFX/word_match_alt"),
                Resources.Load<AudioClip>("SFX/word_match_alt2"),
                Resources.Load<AudioClip>("SFX/word_match_alt3"),
            };
            _buttonClickVariants = new[] {
                Resources.Load<AudioClip>("SFX/button_click"),
                Resources.Load<AudioClip>("SFX/button_click_alt"),
                Resources.Load<AudioClip>("SFX/button_click_alt2"),
            };
            _confirmNewgame  = Resources.Load<AudioClip>("SFX/confirm_newgame");
            _confirmNewgameAlt = Resources.Load<AudioClip>("SFX/confirm_newgame_alt");
            _scoreSuckVariants = new[] {
                Resources.Load<AudioClip>("SFX/score_suck"),
                Resources.Load<AudioClip>("SFX/score_suck_alt"),
                Resources.Load<AudioClip>("SFX/score_suck_alt2"),
            };
            _scoreChimesVariants = new[] {
                Resources.Load<AudioClip>("SFX/score_chimes"),
            };
            _goldSpawnNewVariants = new[] {
                Resources.Load<AudioClip>("SFX/gold_spawn_new"),
            };
            _sparkleWhoosh   = Resources.Load<AudioClip>("SFX/sparkle_whoosh");
            _sparkleWhooshAlt = null; // removed — was silent
            _whooshBigVariants = new[] {
                Resources.Load<AudioClip>("SFX/whoosh_big"),
                Resources.Load<AudioClip>("SFX/whoosh_big_alt"),
            };
            _whooshFastVariants = new[] {
                Resources.Load<AudioClip>("SFX/whoosh_fast"),
                Resources.Load<AudioClip>("SFX/whoosh_fast_alt"),
                Resources.Load<AudioClip>("SFX/whoosh_fast_alt2"),
            };
            _cardDealVariants = new[] {
                Resources.Load<AudioClip>("SFX/card_deal_alt4"),
                Resources.Load<AudioClip>("SFX/card_deal_alt5"),
            };

            _cardDropHandVariants = new[] {
                Resources.Load<AudioClip>("SFX/card_drop_hand"),
                Resources.Load<AudioClip>("SFX/card_drop_hand_alt"),
                Resources.Load<AudioClip>("SFX/card_drop_hand_alt2"),
                Resources.Load<AudioClip>("SFX/card_drop_hand_alt3"),
                Resources.Load<AudioClip>("SFX/card_drop_hand_alt4"),
                Resources.Load<AudioClip>("SFX/card_drop_hand_alt5"),
            };

            // Analyze boom clips to find lead-in offset (time before the main hit)
            AnalyzeBoomOffsets();

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
//             Debug.Log($"[GameAudio] Loaded {count}/12 SFX clips. Vol={_volume:F1} Muted={_muted}");
        }

        // ── Core play method ───────────────────────────────────────────────────

        private void Play(AudioClip clip, float volumeMult = 1f, float pitch = 1f, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
        {
            if (_muted || clip == null) return;
//             Debug.Log($"[SFX] {caller} → {clip.name} (vol={_volume * volumeMult:F2} pitch={pitch:F2})");
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

        // ── Boom offset analysis ──────────────────────────────────────────────
        // Scans audio samples to find the lead-in time before the main hit.
        // This lets us pre-fire audio so the boom syncs with the visual pop.

        private Dictionary<AudioClip, float> _boomOffsets = new Dictionary<AudioClip, float>();

        private void AnalyzeBoomOffsets()
        {
            AnalyzeClip(_detonation);
            AnalyzeClip(_detonationAlt);
            AnalyzeClip(_chainReaction);
            AnalyzeClip(_chainReactionAlt);
            AnalyzeClip(_meltdown);
            AnalyzeClip(_meltdownAlt);
            AnalyzeClip(_chainRumble);
            if (_deepImpactVariants != null)
                foreach (var c in _deepImpactVariants) AnalyzeClip(c);
        }

        private void AnalyzeClip(AudioClip clip)
        {
            if (clip == null) return;

            int totalSamples = clip.samples * clip.channels;
            float[] data = new float[totalSamples];
            clip.GetData(data, 0);

            // Find peak amplitude
            float peakVal = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float abs = Mathf.Abs(data[i]);
                if (abs > peakVal) peakVal = abs;
            }

            // Boom onset = first sample exceeding 50% of peak
            float threshold = peakVal * 0.5f;
            int onsetSample = 0;
            for (int i = 0; i < data.Length; i++)
            {
                if (Mathf.Abs(data[i]) >= threshold)
                {
                    onsetSample = i / clip.channels; // convert to mono sample index
                    break;
                }
            }

            float offsetSeconds = (float)onsetSample / clip.frequency;
            _boomOffsets[clip] = offsetSeconds;
//             Debug.Log($"[GameAudio] Boom offset for {clip.name}: {offsetSeconds * 1000f:F1}ms");
        }

        /// <summary>
        /// Returns the boom lead-in offset for a clip (seconds before the main hit).
        /// Returns 0 if clip wasn't analyzed.
        /// </summary>
        public float GetBoomOffset(AudioClip clip)
        {
            if (clip != null && _boomOffsets.TryGetValue(clip, out float offset))
                return offset;
            return 0f;
        }

        // ── Public play methods ────────────────────────────────────────────────

        public void PlayTileDrop()
        {
            var clip = _tileDropVariants[Random.Range(0, _tileDropVariants.Length)];
            Play(clip != null ? clip : _tileDrop, 0.7f);
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
            Play(_tilePrimed, 0.6f);
        }

        /// <summary>Fizzle sound when a primed word expires.</summary>
        public void PlayTilePrimedAlt()
        {
            Play(_tilePrimedAlt, 0.5f);
        }

        /// <summary>Plays detonation SFX. Returns boom offset in seconds (lead-in before the hit).</summary>
        public float PlayDetonation(int chainDepth = 0)
        {
            Debug.Log($"[DetonationSFX] PlayDetonation({chainDepth}) — muted={_muted}, " +
                      $"detonation clip null? {_detonation == null}, alt null? {_detonationAlt == null}, " +
                      $"source null? {_source == null}");
            // Tiered sound — each level sounds distinctly different
            float volume;
            float pitch;

            if (chainDepth <= 0)
            {
                // Tier 1: standard detonation
                volume = 0.75f;
                pitch = 1.1f;
                Play(PickRandom(_detonation, _detonationAlt), volume, pitch);
            }
            else if (chainDepth <= 1)
            {
                // Tier 2: heavier — both clips layered at full pitch for crisp bite.
                volume = 0.9f;
                pitch = 1.0f;
                Play(_detonation, volume, pitch);
                Play(_detonationAlt, volume * 0.8f, pitch);
            }
            else if (chainDepth <= 2)
            {
                // Tier 3: layered boom — pitched-down bass body + full-pitch crisp
                // layer on top. The deep layer adds weight; the crisp layer keeps
                // the transient "bang" so the chain reaction doesn't feel muddy.
                volume = 0.9f;
                pitch = 0.9f;
                Play(_detonation, volume, pitch);
                Play(_detonationAlt, volume * 0.6f, pitch * 0.9f);
                // Crisp punch layer — full pitch, both clips together.
                Play(_detonation, 0.9f, 1.0f);
                Play(_detonationAlt, 0.85f, 1.0f);
            }
            else
            {
                // Tier 4: massive layered boom — bass + mid + crisp punch + sub-rumble.
                volume = 1.0f;
                pitch = 0.8f;
                Play(_detonation, volume, pitch);
                Play(_detonationAlt, volume * 0.7f, pitch * 0.85f);
                // Extra low rumble layer
                Play(_detonation, 0.5f, 0.4f);
                // Crisp punch layer on top — restores the bite lost to pitch-down.
                Play(_detonation, 1.0f, 1.0f);
                Play(_detonationAlt, 0.95f, 1.0f);
            }

            var clip = PickRandom(_detonation, _detonationAlt);
            float offset = GetBoomOffset(clip);
            return pitch > 0f ? offset / pitch : offset;
        }

        /// <summary>Plays chain reaction SFX. Returns boom offset in seconds.</summary>
        public float PlayChainReaction()
        {
            var clip = PickRandom(_chainReaction, _chainReactionAlt);
            Play(clip, 0.9f);
            return GetBoomOffset(clip);
        }

        /// <summary>Plays meltdown SFX. Returns boom offset in seconds.</summary>
        public float PlayMeltdown()
        {
            var clip = PickRandom(_meltdown, _meltdownAlt);
            Play(clip, 1f);
            return GetBoomOffset(clip);
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

        /// <summary>
        /// Soft wood-tick at higher volume than PlayLightTick — signals "drop
        /// didn't score" on invalid Level-mode drops. Non-punitive but audible
        /// enough to register as distinct feedback (vs. the silent 0.25 tick
        /// used for internal state cycles). Tune volume here if feel needs
        /// adjustment.
        /// </summary>
        public void PlayInvalidDrop()
        {
            Play(_lightTick != null ? _lightTick : _uiClickAlt, 0.55f);
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
                Play(_scoreBig, 1f);        // was _scoreMassive — reusing score_big at full volume for now
            else if (points >= 16)
                Play(_scoreBig, 0.85f);
            else if (points >= 8)
                Play(_scoreTick, 0.7f);  // was _scoreMedium — swapped to tick per Spencer's audio pass
            // Under 8: no extra impact, just the count-up ticks
        }

        public void PlayMenuAppear()
        {
            Play(_menuAppear, 0.7f);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Music layer (looping BGM)
        // ══════════════════════════════════════════════════════════════════════

        private static bool _warnedMissingSurvivalMusic = false;

        /// <summary>
        /// Starts the Survival BGM loop. Idempotent — if the same clip is
        /// already playing, does nothing. Called from MatchController
        /// when a Survival run begins. Quiet no-op (with one-time warning)
        /// if no clip is loaded at Assets/Resources/Music/survival_loop.*.
        /// </summary>
        public void PlaySurvivalMusic()
        {
            if (_musicSource == null) return;
            if (_survivalMusic == null)
            {
                if (!_warnedMissingSurvivalMusic)
                {
                    Debug.LogWarning("[GameAudio] No survival music clip found at Resources/Music/survival_loop. " +
                                     "Drop an .mp3/.ogg/.wav there to enable Survival BGM.");
                    _warnedMissingSurvivalMusic = true;
                }
                return;
            }
            // Idempotent across both tracks — if either survival clip is
            // already playing, leave it running.
            if (_musicSource.isPlaying
                && (_musicSource.clip == _survivalMusic || _musicSource.clip == _survivalMusic2))
                return;

            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }

            // Track 1 plays once (loop=false) iff a track 2 is queued; otherwise
            // it loops by itself the way it always did.
            _musicSource.clip   = _survivalMusic;
            _musicSource.loop   = (_survivalMusic2 == null);
            _musicSource.volume = _musicMuted ? 0f : _musicVolume;
            _musicSource.Play();

            if (_survivalMusic2 != null)
                _musicSequenceRoutine = StartCoroutine(SurvivalMusicSequence());
        }

        /// <summary>
        /// Watches track 1 — once it finishes naturally, switches the music
        /// source to track 2 (looping). External Stop cancels this coroutine
        /// before the switch can happen.
        /// </summary>
        private System.Collections.IEnumerator SurvivalMusicSequence()
        {
            // Wait until track 1 is no longer playing on this source. Either
            // it finished naturally (then we switch) or StopMusic cancels us.
            while (_musicSource != null
                   && _musicSource.clip == _survivalMusic
                   && _musicSource.isPlaying)
            {
                yield return null;
            }

            // Bail if state shifted under us (StopMusic, scene change, etc.)
            if (_musicSource == null || _survivalMusic2 == null
                || _musicSource.clip != _survivalMusic)
            {
                _musicSequenceRoutine = null;
                yield break;
            }

            _musicSource.clip   = _survivalMusic2;
            _musicSource.loop   = true;
            _musicSource.volume = _musicMuted ? 0f : _musicVolume;
            _musicSource.Play();
            _musicSequenceRoutine = null;
        }

        /// <summary>
        /// Starts the main-menu BGM loop (Monkeys Spinning Monkeys). Idempotent
        /// against the menu clip so re-entering the Menu state mid-loop doesn't
        /// restart the track. Cancels any in-flight survival sequence so the
        /// post-survival return-to-menu lands on the menu loop cleanly.
        /// </summary>
        public void PlayMenuMusic()
        {
            if (_musicSource == null) return;
            if (_menuMusic == null)
            {
                Debug.LogWarning("[GameAudio] No menu music clip at Resources/Music/menu_loop.");
                return;
            }
            if (_musicSource.isPlaying && _musicSource.clip == _menuMusic) return;

            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }

            _musicSource.clip   = _menuMusic;
            _musicSource.loop   = true;
            _musicSource.volume = _musicMuted ? 0f : _musicVolume;
            _musicSource.Play();
        }

        /// <summary>Fades music out (0.5s default) then stops the source.</summary>
        public void StopMusic(float fadeSeconds = 0.5f)
        {
            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }
            if (_musicSource == null || !_musicSource.isPlaying) return;
            if (fadeSeconds <= 0f)
            {
                _musicSource.Stop();
                return;
            }
            StartCoroutine(FadeAndStop(_musicSource, fadeSeconds));
        }

        private System.Collections.IEnumerator FadeAndStop(AudioSource src, float dur)
        {
            float start = src.volume;
            float t = 0f;
            while (t < dur && src != null && src.isPlaying)
            {
                t += Time.unscaledDeltaTime;
                src.volume = Mathf.Lerp(start, 0f, t / dur);
                yield return null;
            }
            if (src != null)
            {
                src.Stop();
                src.volume = _musicMuted ? 0f : _musicVolume;
            }
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

        public void PlayPersonalBest()
        {
            Play(_personalBest, 0.9f);
        }

        public void PlayGoldSpawn()
        {
            Play(_goldSpawn, 0.6f);
        }

        // ── New play methods (April 7) ────────────────────────────────────────

        private AudioClip PickRandom(AudioClip[] variants)
        {
            if (variants == null || variants.Length == 0) return null;
            return variants[Random.Range(0, variants.Length)];
        }

        /// <summary>Deep sub rumble for chain detonations and Meltdown.</summary>
        public void PlayChainRumble()
        {
            Play(_chainRumble, 0.9f);
        }

        /// <summary>Heavy explosion — random from 3 variations. For big detonations.</summary>
        public void PlayDeepImpact()
        {
            var clip = PickRandom(_deepImpactVariants);
            if (clip != null)
                Play(clip, 1f);
            else
                PlayDetonation(1);
        }

        /// <summary>Power-up confirm sound — for big score moments / chain cash-out.</summary>
        public void PlayScorePowerup()
        {
            Play(PickRandom(_scorePowerupVariants), 0.8f);
        }

        // ── Stage Up — shimmer/chime sound ──────────────────────────────────────
        private AudioClip[] _stageUpVariants;

        public void PlayStageUp()
        {
            if (_stageUpVariants == null)
            {
                _stageUpVariants = new AudioClip[]
                {
                    Resources.Load<AudioClip>("Audio/stage_up_1"),
                    Resources.Load<AudioClip>("Audio/stage_up_2"),
                    Resources.Load<AudioClip>("Audio/stage_up_3"),
                    Resources.Load<AudioClip>("Audio/stage_up_4"),
                    Resources.Load<AudioClip>("Audio/stage_up_5"),
                    Resources.Load<AudioClip>("Audio/stage_up_6"),
                };
//                 Debug.Log($"[GameAudio] Loaded {_stageUpVariants.Length} stage_up variants");
            }
            Play(PickRandom(_stageUpVariants), 0.5f);
        }

        // ── Tile Fall — swish sound during falling animation ──────────────
        private AudioClip[] _tileFallVariants;
        private AudioClip[] _tileFallRareVariants;

        public void PlayTileFall()
        {
            if (_tileFallVariants == null)
            {
                _tileFallVariants = new AudioClip[]
                {
                    Resources.Load<AudioClip>("Audio/tile_fall_1"),
                    Resources.Load<AudioClip>("Audio/tile_fall_2"),
                    Resources.Load<AudioClip>("Audio/tile_fall_3"),
                    Resources.Load<AudioClip>("Audio/tile_fall_4"),
                    Resources.Load<AudioClip>("Audio/tile_fall_5"),
                    Resources.Load<AudioClip>("Audio/tile_fall_6"),
                };
                _tileFallRareVariants = new AudioClip[]
                {
                    Resources.Load<AudioClip>("Audio/whoosh_buildup_1"),
                    Resources.Load<AudioClip>("Audio/whoosh_buildup_2"),
                    Resources.Load<AudioClip>("Audio/whoosh_buildup_3"),
                    Resources.Load<AudioClip>("Audio/whoosh_buildup_4"),
                    Resources.Load<AudioClip>("Audio/whoosh_buildup_5"),
                };
            }

            // 10% chance of rare whoosh variant
            if (Random.value < 0.10f)
                Play(PickRandom(_tileFallRareVariants), 0.7f);
            else
                Play(PickRandom(_tileFallVariants), 0.6f);
        }

        /// <summary>Poof explosion — lighter explosion for tile removal / smaller detonations.</summary>
        public void PlayPoofExplosion()
        {
            var clip = PickRandom(_poofExplosionVariants);
//             Debug.Log($"[GameAudio] PlayPoofExplosion clip={(clip != null ? clip.name : "NULL")}");
            if (clip != null)
                Play(clip, 0.75f);
            else
                PlayDetonation(0); // fallback to old detonation if new clips not loaded
        }

        /// <summary>Tonal rising — for Meltdown build-up and rare big events. Use sparingly.</summary>
        public void PlayEventRising()
        {
            Play(_eventRising, 1f);
        }

        /// <summary>Cute match sound — for simple word scoring (no detonation).</summary>
        public void PlayWordMatch()
        {
            Play(PickRandom(_wordMatchVariants), 0.6f);
        }

        /// <summary>Organic button click — for standard UI buttons.</summary>
        public void PlayButtonClick()
        {
            Play(PickRandom(_buttonClickVariants), 0.5f);
        }

        /// <summary>Zippy confirm — for starting a new game.</summary>
        public void PlayConfirmNewGame()
        {
            Play(PickRandom(_confirmNewgame, _confirmNewgameAlt), 0.7f);
        }

        /// <summary>Whoosh suction — for score points flying into HUD total.</summary>
        public void PlayScoreSuck()
        {
            Play(PickRandom(_scoreSuckVariants), 0.65f);
        }

        /// <summary>Crystal chimes — for score collection / banking points.</summary>
        public void PlayScoreChimes()
        {
            Play(PickRandom(_scoreChimesVariants), 0.7f);
        }

        /// <summary>Illusory bells — for gold tile spawn. Replaces old gold_spawn if preferred.</summary>
        public void PlayGoldSpawnNew()
        {
            Play(PickRandom(_goldSpawnNewVariants), 0.85f);
        }

        /// <summary>Magical sparkle whoosh — for special transitions or chain replay start.</summary>
        public void PlaySparkleWhoosh()
        {
            Play(PickRandom(_sparkleWhoosh, _sparkleWhooshAlt), 0.7f);
        }

        /// <summary>Big whoosh — for major menu transitions (game over panel, etc).</summary>
        public void PlayWhooshBig()
        {
            Play(PickRandom(_whooshBigVariants), 0.7f);
        }

        /// <summary>Fast whoosh — for smaller UI transitions (panels, popups).</summary>
        public void PlayWhooshFast()
        {
            Play(PickRandom(_whooshFastVariants), 0.6f);
        }

        /// <summary>Card deal sound — for individual card refills during gameplay.</summary>
        public void PlayCardDeal()
        {
            Play(PickRandom(_cardDealVariants), 0.55f);
        }

        /// <summary>Card drop in hand — for initial deal at game start (heavier, more satisfying).</summary>
        public void PlayCardDropHand()
        {
            Play(PickRandom(_cardDropHandVariants), 0.6f);
        }

        /// <summary>
        /// Plays depth-scaled detonation audio. Deeper chains get heavier sounds.
        /// Depth 0: poof. Depth 1: deep impact. Depth 2+: deep impact + chain rumble.
        /// </summary>
        public void PlayDetonationLayered(int chainDepth)
        {
//             Debug.Log($"[GameAudio] PlayDetonationLayered depth={chainDepth}");
            if (chainDepth <= 0)
            {
                PlayPoofExplosion();
            }
            else if (chainDepth == 1)
            {
                PlayDeepImpact();
            }
            else
            {
                PlayDeepImpact();
                PlayChainRumble();
            }
        }
    }
}
