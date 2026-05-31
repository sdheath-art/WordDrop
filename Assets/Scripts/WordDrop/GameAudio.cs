using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

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
        private float _volume = 0.80f; // SFX default — AAA mobile mix
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
                ApplyMixerLevels();
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
                ApplyMixerLevels();
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
        private AudioClip _earthquake;
        // Dedicated source for stoppable rumble loops (earthquake) so we can
        // cut them off when the detonation bang plays.
        private AudioSource _rumbleSource;
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
        private AudioClip _entry;   // modal entry SFX — stage clear, boss reward, etc.
        private AudioClip _wood2;   // wooden thunk — TopOutPanel landing impact
        // Continue button on stage clear modal — original floraphonic
        // multi-pop split into two halves so the first pop fires on press-
        // down and the second on release for a tactile click feel.
        private AudioClip _multiPopPress;
        private AudioClip _multiPopRelease;
        // Settings button (gear icon on the booster HUD row) — otherpop
        // split into press/release halves for the same tactile click feel.
        private AudioClip _otherPopPress;
        private AudioClip _otherPopRelease;
        private AudioClip[] _bloopVariants;  // row-rise bloop pop — random pick across bloop/bloop2/3/4 for variety
        private AudioClip[] _whooshBigVariants;
        private AudioClip[] _whooshFastVariants;
        private AudioClip[] _cardDealVariants;
        private AudioClip[] _cardDropHandVariants;
        private AudioClip[] _matchLineVariants;
        // Faint overlay clip — plays UNDER the standard match line on cascade
        // pops 2+ (not on the first pop). Adds chord layering as the chain
        // climbs without overpowering the base pop sound.
        private AudioClip _matchLine13;

        private AudioSource _source;
        private AudioSource _pitchedSource; // separate source for pitch-shifted sounds
        private AudioSource _musicSource;   // looping music layer (Survival BGM)

        // ── AudioMixer routing (Phase 11g) ─────────────────────────────────────
        // The mixer asset (Resources/Audio/WordDropMixer) owns final attenuation
        // for Music / SFX / UI groups via exposed dB params (MusicVol/SFXVol/
        // UIVol). If the asset is missing at runtime, ApplyMixerLevels falls
        // back to direct AudioSource.volume so audio still plays sanely.
        [SerializeField] private AudioMixer _mixer;
        private AudioMixerGroup _musicGroup;
        private AudioMixerGroup _sfxGroup;
        private AudioMixerGroup _uiGroup;
        private AudioMixerSnapshot _baseSnapshot;
        private AudioMixerSnapshot _duckSnapshot;

        private float _uiVolume = 0.65f;
        private bool  _uiMuted  = false;
        private const string PREF_UI_VOLUME = "UIVolume";
        private const string PREF_UI_MUTED  = "UIMuted";

        public float UIVolume
        {
            get => _uiVolume;
            set
            {
                _uiVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(PREF_UI_VOLUME, _uiVolume);
                PlayerPrefs.Save();
                ApplyMixerLevels();
            }
        }

        public bool UIMuted
        {
            get => _uiMuted;
            set
            {
                _uiMuted = value;
                PlayerPrefs.SetInt(PREF_UI_MUTED, _uiMuted ? 1 : 0);
                PlayerPrefs.Save();
                ApplyMixerLevels();
            }
        }

        private static float LinearToDb(float linear)
        {
            if (linear <= 0.0001f) return -80f;
            return Mathf.Log10(Mathf.Clamp01(linear)) * 20f;
        }

        private System.Collections.IEnumerator ReapplyMixerNextFrame()
        {
            yield return null;        // wait one frame for mixer to stabilize
            yield return null;        // and another, just to be safe
            ApplyMixerLevels();
        }

        private void ApplyMixerLevels()
        {
            if (_mixer != null)
            {
                // 2026-05-29: music attenuation moved from mixer.SetFloat
                // to source.volume. Previously _musicSource.volume = 1f and
                // the mixer handled attenuation via MusicVol param. Problem:
                // at startup the AudioMixer's snapshot is still loading and
                // SetFloat doesn't take effect for ~1-2 seconds, so music
                // played at FULL source volume with no attenuation, then
                // snapped to the saved level once the snapshot settled.
                // Setting source.volume directly is immediate.
                //
                // Mixer's MusicVol still toggles mute (-80dB) so the
                // DuckMusicBriefly snapshot transition keeps working.
                _mixer.SetFloat("MusicVol", _musicMuted ? -80f : 0f);
                _mixer.SetFloat("SFXVol",   _muted      ? -80f : LinearToDb(_volume));
                _mixer.SetFloat("UIVol",    _uiMuted    ? -80f : LinearToDb(_uiVolume));
                if (_musicSource != null)
                    _musicSource.volume = _musicMuted ? 0f : _musicVolume;
                return;
            }
            // Fallback path — mixer asset missing. Drive music attenuation on
            // the source directly so volume controls still work end-to-end.
            // SFX volume is already applied per-call in Play() via volumeMult.
            if (_musicSource != null)
                _musicSource.volume = _musicMuted ? 0f : _musicVolume;
        }

        /// <summary>
        /// Phase 11g: briefly transition to the DuckMusic snapshot so the music
        /// dips during a big detonation, then transitions back. No-op if the
        /// mixer / snapshots aren't loaded.
        /// </summary>
        public void DuckMusicBriefly(float durationSeconds = 0.30f)
        {
            if (_mixer == null || _baseSnapshot == null || _duckSnapshot == null) return;
            _duckSnapshot.TransitionTo(0.05f);
            StartCoroutine(RestoreSnapshotAfter(durationSeconds));
        }

        private System.Collections.IEnumerator RestoreSnapshotAfter(float t)
        {
            yield return new WaitForSeconds(t);
            if (_baseSnapshot != null) _baseSnapshot.TransitionTo(0.15f);
        }

        // ── Music clips ────────────────────────────────────────────────────────
        // 2026-05-16: upgraded from 2 fixed survival tracks + 1 menu track
        // to folder-based pools loaded via Resources.LoadAll.
        // Drop new MP3s into the folders and they're auto-included on next build.
        private AudioClip[] _survivalMusicPool;   // Resources/Music/Gameplay/ — random pick per track, infinite rotation
        private AudioClip[] _menuMusicPool;       // Resources/Music/Menu/ — main_menu is signature
        private AudioClip[] _victoryMusicPool;    // Resources/Music/Victory/ — Skybound is canonical
        private AudioClip   _menuSignatureClip;   // cached "main_menu" track for signature-priority menu playback
        private AudioClip   _victorySkyboundClip; // cached "Skybound_Victory" for canonical victory
        private AudioClip   _lastStageClearClip;  // for stage-clear rotation, avoid immediate-repeat across modal opens
        private Coroutine   _musicSequenceRoutine; // watches current track and rotates to a fresh random pick when it ends

        // Music layer volume + mute, persisted separately from SFX so a player
        // can turn music off without losing feedback sounds.
        private const string PREF_MUSIC_VOLUME = "MusicVolume";
        private const string PREF_MUSIC_MUTED  = "MusicMuted";
        private float _musicVolume = 0.30f; // music sits behind SFX in AAA mobile mix
        private bool  _musicMuted  = false;

        public float MusicVolume
        {
            get => _musicVolume;
            set
            {
                _musicVolume = Mathf.Clamp01(value);
                PlayerPrefs.SetFloat(PREF_MUSIC_VOLUME, _musicVolume);
                PlayerPrefs.Save();
                ApplyMixerLevels();
            }
        }

        // Hotkey toggle for music — press M to mute/unmute. Quick option
        // while a proper settings UI doesn't exist yet. Logs to console so
        // it's clear whether a key press registered.
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.M))
            {
                MusicMuted = !MusicMuted;
                Debug.Log($"[GameAudio] Music {(MusicMuted ? "MUTED" : "UNMUTED")} (press M to toggle)");
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
                ApplyMixerLevels();
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
            // Dedicated source for stoppable rumble (earthquake meltdown wind-up).
            _rumbleSource = gameObject.AddComponent<AudioSource>();
            _rumbleSource.playOnAwake = false;

            // Load saved prefs
            _volume      = PlayerPrefs.GetFloat(PREF_VOLUME, 0.80f);
            _muted       = PlayerPrefs.GetInt(PREF_MUTED, 0) == 1;
            _musicVolume = PlayerPrefs.GetFloat(PREF_MUSIC_VOLUME, 0.30f);
            _musicMuted  = PlayerPrefs.GetInt(PREF_MUSIC_MUTED, 0) == 1;
            _uiVolume    = PlayerPrefs.GetFloat(PREF_UI_VOLUME, 0.65f);
            _uiMuted     = PlayerPrefs.GetInt(PREF_UI_MUTED, 0) == 1;

            // Phase 11g: bind to WordDropMixer if present. Group routing
            // ensures Music / SFX / UI hit the correct attenuator + the
            // DuckMusic snapshot can dip music under big detonations.
            if (_mixer == null) _mixer = Resources.Load<AudioMixer>("Audio/WordDropMixer");
            if (_mixer != null)
            {
                var musicGroups = _mixer.FindMatchingGroups("Master/Music");
                var sfxGroups   = _mixer.FindMatchingGroups("Master/SFX");
                var uiGroups    = _mixer.FindMatchingGroups("Master/UI");
                _musicGroup = musicGroups.Length > 0 ? musicGroups[0] : null;
                _sfxGroup   = sfxGroups.Length   > 0 ? sfxGroups[0]   : null;
                _uiGroup    = uiGroups.Length    > 0 ? uiGroups[0]    : null;
                _baseSnapshot = _mixer.FindSnapshot("Snapshot");
                _duckSnapshot = _mixer.FindSnapshot("DuckMusic");
            }
            _source.outputAudioMixerGroup        = _sfxGroup;
            _pitchedSource.outputAudioMixerGroup = _sfxGroup;
            _musicSource.outputAudioMixerGroup   = _musicGroup;
            _rumbleSource.outputAudioMixerGroup  = _sfxGroup;

            ApplyMixerLevels();
            // 2026-05-29: persistence bug fix — the AudioMixer can be mid-
            // snapshot-transition when Awake's SetFloat fires, causing it
            // to be overridden once the base snapshot settles. Re-applying
            // after one frame catches this. Without it, saved music/SFX
            // volumes were getting ignored on reboot.
            StartCoroutine(ReapplyMixerNextFrame());

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

            // Music tracks (2026-05-16): folder-based pools. Each folder loads
            // every AudioClip inside via Resources.LoadAll. Add/remove MP3s
            // without touching code.
            //
            //   Resources/Music/Gameplay/  → 11 survival tracks
            //   Resources/Music/Menu/      → 5 menu tracks (main_menu = signature)
            //   Resources/Music/Victory/   → 3 victory tracks (Skybound = canonical)
            _survivalMusicPool = Resources.LoadAll<AudioClip>("Music/Gameplay");
            _menuMusicPool     = Resources.LoadAll<AudioClip>("Music/Menu");
            _victoryMusicPool  = Resources.LoadAll<AudioClip>("Music/Victory");

            // Cache the signature/canonical tracks for priority playback.
            // Names must match the file basenames in their respective folders.
            // 2026-05-28: switched menu signature from "main_menu" to
            // "Jewel_Parade__1" per Spencer. Filename uses double underscore
            // between "Parade" and "1" — must match exactly.
            _menuSignatureClip   = FindClipByName(_menuMusicPool,    "Jewel_Parade__1");
            _victorySkyboundClip = FindClipByName(_victoryMusicPool, "Skybound_Victory");

            // New SFX (April 7)
            _chainRumble     = Resources.Load<AudioClip>("SFX/chain_rumble");
            _earthquake      = Resources.Load<AudioClip>("SFX/earthquake");
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
            _entry           = Resources.Load<AudioClip>("SFX/entry");
            _wood2           = Resources.Load<AudioClip>("SFX/wood2");
            _multiPopPress   = Resources.Load<AudioClip>("SFX/multipop_press");
            _multiPopRelease = Resources.Load<AudioClip>("SFX/multipop_release");
            _otherPopPress   = Resources.Load<AudioClip>("SFX/otherpop_press");
            _otherPopRelease = Resources.Load<AudioClip>("SFX/otherpop_release");
            _bloopVariants = new[] {
                Resources.Load<AudioClip>("SFX/bloop"),
                Resources.Load<AudioClip>("SFX/bloop2"),
                Resources.Load<AudioClip>("SFX/bloop3"),
                Resources.Load<AudioClip>("SFX/bloop4"),
            };
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

            _matchLineVariants = new[] {
                Resources.Load<AudioClip>("SFX/matchline6"),
                Resources.Load<AudioClip>("SFX/matchline7"),
                Resources.Load<AudioClip>("SFX/matchline8"),
            };
            _matchLine13 = Resources.Load<AudioClip>("SFX/matchline13");

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

        public void PlayWordScored(int cascadeStep = 0)
        {
            // Shares the same burst counter as PlayMatchLine so the chime and
            // pop climb the same chord together rather than diverging. Each
            // call within MATCH_LINE_BURST_WINDOW pitches up one semitone.
            float now = Time.unscaledTime;
            if (now - _matchLineBurstLastTime > MATCH_LINE_BURST_WINDOW)
                _matchLineBurstStep = 0;
            else
                _matchLineBurstStep++;
            _matchLineBurstLastTime = now;

            int effectiveStep = Mathf.Max(cascadeStep, _matchLineBurstStep);
            int clampedStep = Mathf.Clamp(effectiveStep, 0, 8);
            // 2 semitones per chain step — matches PlayMatchLine
            float pitch = Mathf.Pow(2f, (clampedStep * 2) / 12f);
            // 2026-05-29: volume reduced 0.8 → 0.55 — same anti-clipping
            // rationale as PlayMatchLine. word_scored stacks with matchline
            // pops during cascade chains.
            Play(PickRandom(_wordScored, _wordScoredAlt), 0.55f, pitch);
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
                // Tier 4: massive layered boom — natural pitch (Spencer wanted
                // the original pitch for meltdown, not pitched-down). Layered
                // volume gives weight without losing the clip's natural bite.
                volume = 1.0f;
                pitch = 1.0f;
                Play(_detonation, volume, pitch);
                Play(_detonationAlt, volume * 0.7f, pitch);
                // Crisp punch layer on top.
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

        /// <summary>Rising-row pop sound — random pick from bloop / bloop2 /
        /// bloop3 / bloop4 for variety across repeated row rises.
        /// <paramref name="pitch"/> defaults to 1.0; the staggered hand deal
        /// passes a per-card semitone climb so the deal sweeps up musically.</summary>
        public void PlayRisingRow(float pitch = 1f)
        {
            if (_bloopVariants == null || _bloopVariants.Length == 0) return;
            Play(PickRandom(_bloopVariants), 1.0f, pitch);
        }

        /// <summary>
        /// Canonical "tile/card arrives" sound — single source of truth for
        /// every site where a new tile or hand card pops into existence.
        /// Currently delegates to PlayRisingRow. SWAP THE BODY of this method
        /// to change the arrival sound globally:
        ///   • Row-rise new tiles (RisingRowManager)
        ///   • Full hand deal at level start / post-turn (HandManager.StaggeredHandPopIn — passes a semitone-climbing pitch per card)
        ///   • Bag-swap single-card refill (HandManager.SwapViaBagDrop)
        ///
        /// Per-tile-drop single-card refill (HandManager.CompleteDropBookkeeping)
        /// is INTENTIONALLY SILENT — it shares the animation but not the audio
        /// because the row-rise bloop doubles up with the other SFX firing on
        /// the same frame. The pop animation still uses NewTilePop; only audio
        /// diverges there.
        ///
        /// The <paramref name="pitch"/> arg is forwarded so the hand-deal
        /// climb keeps working regardless of which clip lives underneath.
        /// </summary>
        public void PlayTileArrival(float pitch = 1f)
        {
            PlayRisingRow(pitch);
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
            // 0.55 mult lands at ~0.22 final at default master 0.4 — audible
            // alongside music and other SFX. Was 0.2 (final ~0.08) which read
            // as silent in playtest. PlayScoreImpact still uses 0.7 for the
            // discrete mid/big hits so those still pop above the count-up tick.
            Play(_scoreTick, 0.55f, pitch);
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
            if (_survivalMusicPool == null || _survivalMusicPool.Length == 0)
            {
                if (!_warnedMissingSurvivalMusic)
                {
                    Debug.LogWarning("[GameAudio] No survival music clips found at Resources/Music/Gameplay/. " +
                                     "Drop .mp3/.ogg/.wav files there to enable Survival BGM.");
                    _warnedMissingSurvivalMusic = true;
                }
                return;
            }
            // Idempotent — if any survival pool clip is already playing, leave it.
            if (_musicSource.isPlaying && IsClipInPool(_musicSource.clip, _survivalMusicPool))
                return;

            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }

            // Random pick from the pool — Candy-Crush-style rotation.
            // Each track plays once (loop=false), then SurvivalMusicSequence
            // picks a fresh random track from the pool indefinitely.
            _musicSource.clip = PickRandomClip(_survivalMusicPool, exclude: null);
            _musicSource.loop = false;
            _musicSource.Play();

            // Always start rotation coroutine — even with 1 track it'll re-pick
            // the same one when it ends, giving consistent loop behavior.
            _musicSequenceRoutine = StartCoroutine(SurvivalMusicSequence());
        }

        /// <summary>
        /// Survival music rotation. When the current track ends, picks a new
        /// random clip from _survivalMusicPool (avoiding immediate repeat of
        /// the same track when the pool has 2+ entries) and plays it. Runs
        /// indefinitely until StopMusic / scene change / pool emptied.
        /// 2026-05-16: upgraded from 2-track alternation to N-track pool.
        /// </summary>
        private System.Collections.IEnumerator SurvivalMusicSequence()
        {
            while (_musicSource != null)
            {
                // Wait for the current track to finish (or be stopped externally).
                while (_musicSource != null && _musicSource.isPlaying)
                {
                    yield return null;
                }

                // Bail if state shifted under us (StopMusic, scene change, pool emptied).
                if (_musicSource == null || _survivalMusicPool == null || _survivalMusicPool.Length == 0)
                {
                    _musicSequenceRoutine = null;
                    yield break;
                }

                // Pick a fresh random clip, avoiding immediate repeat when possible.
                AudioClip nextClip = PickRandomClip(_survivalMusicPool, exclude: _musicSource.clip);
                _musicSource.clip = nextClip;
                _musicSource.loop = false;
                _musicSource.Play();
            }

            _musicSequenceRoutine = null;
        }

        /// <summary>
        /// Starts the main-menu BGM loop (Monkeys Spinning Monkeys). Idempotent
        /// against the menu clip so re-entering the Menu state mid-loop doesn't
        /// restart the track. Cancels any in-flight survival sequence so the
        /// post-survival return-to-menu lands on the menu loop cleanly.
        /// </summary>
        // 2026-05-28: bias raised 0.80 → 1.00 so Jewel_Parade__1 is the ONLY
        // menu track (Spencer locked it as THE main menu song). Drop back to
        // 0.80 if you want occasional variety from the rest of the pool.
        private const float MENU_SIGNATURE_BIAS = 1.00f;

        public void PlayMenuMusic()
        {
            if (_musicSource == null) return;
            if (_menuMusicPool == null || _menuMusicPool.Length == 0)
            {
                Debug.LogWarning("[GameAudio] No menu music clips at Resources/Music/Menu/.");
                return;
            }
            // Idempotent — if any menu pool clip is already playing, leave it.
            if (_musicSource.isPlaying && IsClipInPool(_musicSource.clip, _menuMusicPool)) return;

            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }

            // Signature-priority pick: usually play main_menu, occasionally rotate.
            AudioClip chosen;
            if (_menuSignatureClip != null && Random.value < MENU_SIGNATURE_BIAS)
            {
                chosen = _menuSignatureClip;
            }
            else
            {
                chosen = PickRandomClip(_menuMusicPool, exclude: null);
            }

            _musicSource.clip = chosen;
            _musicSource.loop = true; // menu music loops the chosen track for the menu session
            _musicSource.Play();
        }

        /// <summary>
        /// Plays victory/end-of-session music. Canonical pick is Skybound_Victory
        /// (marked as the chosen one). Falls back to a random pool pick if Skybound
        /// is missing. Plays ONCE (no loop) — fire when the score panel flies up
        /// after game-over and you want the player's session-summary moment.
        /// 2026-05-16.
        /// </summary>
        public void PlayVictoryMusic()
        {
            if (_musicSource == null) return;
            if (_victoryMusicPool == null || _victoryMusicPool.Length == 0)
            {
                Debug.LogWarning("[GameAudio] No victory music clips at Resources/Music/Victory/.");
                return;
            }

            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }

            // Canonical pick = Skybound. Fallback = first pool clip.
            AudioClip chosen = _victorySkyboundClip != null
                ? _victorySkyboundClip
                : _victoryMusicPool[0];

            _musicSource.clip = chosen;
            _musicSource.loop = false; // fires once, doesn't loop
            _musicSource.Play();
        }

        /// <summary>
        /// Stage-clear celebration music. Picks a random clip from the victory
        /// pool, avoiding immediate repeats across consecutive modal opens.
        /// Distinct from PlayVictoryMusic (which is canonical-Skybound for the
        /// end-of-run game-over screen) because stage clears fire many times
        /// per run and need variety.
        /// </summary>
        public void PlayStageClearMusic()
        {
            if (_musicSource == null) return;
            if (_victoryMusicPool == null || _victoryMusicPool.Length == 0)
            {
                Debug.LogWarning("[GameAudio] No stage-clear music clips at Resources/Music/Victory/.");
                return;
            }

            if (_musicSequenceRoutine != null)
            {
                StopCoroutine(_musicSequenceRoutine);
                _musicSequenceRoutine = null;
            }

            // Pick a random clip, excluding the last one we played so the
            // player doesn't hear the same celebration track twice in a row.
            AudioClip chosen = PickRandomClip(_victoryMusicPool, exclude: _lastStageClearClip);
            _lastStageClearClip = chosen;

            _musicSource.clip = chosen;
            _musicSource.loop = false; // fires once per stage clear, doesn't loop
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
            // Phase 11g: fade by lerping the mixer's MusicVol param so the fade
            // happens at the bus stage; ApplyMixerLevels at the end restores
            // the user's saved music level. If the mixer isn't loaded, fall
            // back to lerping the AudioSource volume directly.
            if (_mixer == null)
            {
                float startVol = src.volume;
                float tFallback = 0f;
                while (tFallback < dur && src != null && src.isPlaying)
                {
                    tFallback += Time.unscaledDeltaTime;
                    src.volume = Mathf.Lerp(startVol, 0f, tFallback / dur);
                    yield return null;
                }
                if (src != null) src.Stop();
                ApplyMixerLevels();
                yield break;
            }

            _mixer.GetFloat("MusicVol", out float startDb);
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01(t / dur);
                _mixer.SetFloat("MusicVol", Mathf.Lerp(startDb, -80f, k));
                yield return null;
            }
            if (src != null) src.Stop();
            ApplyMixerLevels(); // restore to user's setting
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
        public void PlayChainRumble(float volume = 0.9f)
        {
            Play(_chainRumble, volume);
        }

        /// <summary>
        /// LOUD layered version for the start of meltdown — fires the rumble
        /// 3× simultaneously so PlayOneShot's per-call ~1.0 cap stacks into
        /// genuinely-loud output (Unity sums simultaneous PlayOneShots).
        /// </summary>
        public void PlayChainRumbleLayered()
        {
            if (_chainRumble == null) return;
            Play(_chainRumble, 1f);
            Play(_chainRumble, 1f);
            Play(_chainRumble, 1f);
        }

        /// <summary>
        /// Earthquake rumble for the meltdown windup — plays on a dedicated
        /// AudioSource so it can be stopped programmatically when the
        /// detonation bang fires (so the rumble cuts off cleanly at impact).
        /// </summary>
        public void PlayEarthquake(float volume = 1f)
        {
            if (_earthquake == null || _rumbleSource == null || _muted) return;
            _rumbleSource.clip   = _earthquake;
            _rumbleSource.volume = _volume * volume;
            _rumbleSource.loop   = false;
            _rumbleSource.Play();
        }

        /// <summary>
        /// Stops the earthquake rumble. Call at the explosion-impact frame so
        /// the rumble cuts cleanly into the detonation bang.
        /// </summary>
        public void StopEarthquake()
        {
            if (_rumbleSource != null && _rumbleSource.isPlaying)
                _rumbleSource.Stop();
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
                    // 2026-05-30: whoosh_buildup_1 removed per Spencer — it
                    // was firing during animations where the buildup feel
                    // didn't fit. Other 4 variants kept; PlayTileFall still
                    // picks one randomly at 10% rate.
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

        /// <summary>Tier-1 pop SFX — randomized matchline6/7/8. Used by the
        /// Candy-Crush-style PlayTier1Pop sequence instead of PlayDetonation.
        /// Volume mult 1.0 (full unity gain) so the pop reads punchy against
        /// the rest of the SFX mix. Routes through _source → _sfxGroup so any
        /// AudioMixer ducking on the SFX bus applies identically to other SFX.</summary>
        // Burst-counter for chord climb. Reset on a quiet gap so the chord
        // restarts at root pitch on the next event burst.
        private static int _matchLineBurstStep = 0;
        private static float _matchLineBurstLastTime = -10f;
        private const float MATCH_LINE_BURST_WINDOW = 0.50f; // 500ms: enough for parallel detonations, short enough to reset between turns

        public void PlayMatchLine(int cascadeStep = 0)
        {
            // Chord-climb pitch escalation. Uses MAX of (incoming cascadeStep,
            // burst-counter step) so:
            //   - Sequential cascade chains (where cascadeStep increments per
            //     chain depth) still climb based on chainStep.
            //   - Parallel detonations (multiple primed words triggered together,
            //     all firing at the same chainStep) ALSO climb via the burst
            //     counter — each consecutive pop within MATCH_LINE_BURST_WINDOW
            //     gets pitched one extra semitone. Solves the "3 pops at same
            //     pitch" perceptual bug.
            // Caps at 2× (octave = 12 semitones).
            float now = Time.unscaledTime;
            if (now - _matchLineBurstLastTime > MATCH_LINE_BURST_WINDOW)
                _matchLineBurstStep = 0;
            else
                _matchLineBurstStep++;
            _matchLineBurstLastTime = now;

            int effectiveStep = Mathf.Max(cascadeStep, _matchLineBurstStep);
            int clampedStep = Mathf.Clamp(effectiveStep, 0, 8); // cap at 8 (16 semitones = +octave-and-a-fourth)
            // 2 semitones per chain step — clearly audible musical interval
            // (was 1 semitone, too subtle for phone speakers per Spencer 2026-05-19)
            float pitch = Mathf.Pow(2f, (clampedStep * 2) / 12f);
            // 2026-05-29: volume reduced 1.0 → 0.65 to prevent polyphonic
            // clipping during cascades. When matchline pop + matchline13
            // underlay + PlayWordScored chord all sum at the master bus,
            // pitched-up high-frequency content was distorting. Each
            // individual sound is now well under 0dB so the sum stays
            // under clipping.
            Play(PickRandom(_matchLineVariants), 0.65f, pitch);
            // Faint matchline13 underlay on cascade pops 2+. Volume 0.35 →
            // 0.22 to further reduce the cumulative HF sum during chains.
            if (effectiveStep > 0 && _matchLine13 != null)
                Play(_matchLine13, 0.22f, pitch);
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
            // 2026-05-29: bumped 0.7 → 1.1 for the stage-clear sweep. The
            // sparkle_whoosh clip's source recording is on the quiet side;
            // the celebration moment needs the SFX to read clearly above
            // the music + UI swoosh.
            Play(PickRandom(_sparkleWhoosh, _sparkleWhooshAlt), 1.1f);
        }

        /// <summary>Big whoosh — for major menu transitions (game over panel, etc).</summary>
        public void PlayWhooshBig()
        {
            Play(PickRandom(_whooshBigVariants), 0.7f);
        }

        /// <summary>Entry sting — for modal panels appearing (stage clear, boss reward).
        /// Distinct from sparkle_whoosh and whoosh_big — gives modals a dedicated audio signature.</summary>
        public void PlayEntry()
        {
            Play(_entry, 1.0f);
        }

        /// <summary>Wooden thunk — TopOutPanel landing impact. Plays alongside
        /// the drop animation so the bounce reads as "panel hitting a wood surface".</summary>
        public void PlayWood2()
        {
            Play(_wood2, 1.0f);
        }

        /// <summary>First pop of the Continue-button split clip — fires on
        /// PointerDown for tactile press feel.</summary>
        public void PlayMultiPopPress()
        {
            Play(_multiPopPress, 1.0f);
        }

        /// <summary>Second pop of the Continue-button split clip — fires on
        /// pointer release / onClick for the "commit" beat after press.</summary>
        public void PlayMultiPopRelease()
        {
            Play(_multiPopRelease, 1.0f);
        }

        /// <summary>Fast whoosh — for smaller UI transitions (panels, popups).</summary>
        public void PlayWhooshFast()
        {
            Play(PickRandom(_whooshFastVariants), 0.6f);
        }

        /// <summary>
        /// Whoosh that accompanies the bench+hand icons converging out of
        /// existence when a menu opens. Currently whoosh_fast_alt (index 1
        /// of _whooshFastVariants); swap here to change the collapse sound
        /// globally. Pairs with PlayUIGroupExpand on the reverse animation.
        /// </summary>
        public void PlayUIGroupCollapse()
        {
            if (_whooshFastVariants == null || _whooshFastVariants.Length < 2) return;
            Play(_whooshFastVariants[1], 0.6f);
        }

        /// <summary>
        /// Whoosh that accompanies the bench+hand icons popping back into
        /// existence when a menu closes. Currently whoosh_fast_alt2 (index
        /// 2 of _whooshFastVariants). Pairs with PlayUIGroupCollapse.
        /// </summary>
        public void PlayUIGroupExpand()
        {
            if (_whooshFastVariants == null || _whooshFastVariants.Length < 3) return;
            Play(_whooshFastVariants[2], 0.6f);
        }

        /// <summary>Settings-button press half — otherpop_press. Fires on
        /// PointerDown of the gear icon for tactile click feel.</summary>
        public void PlaySettingsPress()
        {
            Play(_otherPopPress, 1.0f);
        }

        /// <summary>Settings-button release half — otherpop_release. Fires
        /// on pointer release / onClick, paired with PlaySettingsPress.</summary>
        public void PlaySettingsRelease()
        {
            Play(_otherPopRelease, 1.0f);
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

        // ── Music pool helpers (2026-05-16) ───────────────────────────────────

        /// <summary>
        /// Pick a random clip from the pool. If exclude is non-null and the pool
        /// has 2+ entries, avoids picking the same clip as exclude (so two-in-a-row
        /// repetition is dodged for variety). Returns null only if pool is empty.
        /// </summary>
        private static AudioClip PickRandomClip(AudioClip[] pool, AudioClip exclude)
        {
            if (pool == null || pool.Length == 0) return null;
            if (pool.Length == 1) return pool[0];
            if (exclude == null) return pool[Random.Range(0, pool.Length)];

            // Pool of 2+ — pick a different one than exclude up to 4 tries
            for (int attempt = 0; attempt < 4; attempt++)
            {
                AudioClip pick = pool[Random.Range(0, pool.Length)];
                if (pick != exclude) return pick;
            }
            // Last resort: return whatever we got
            return pool[Random.Range(0, pool.Length)];
        }

        /// <summary>True iff clip is one of the entries in pool. O(N), N is small.</summary>
        private static bool IsClipInPool(AudioClip clip, AudioClip[] pool)
        {
            if (clip == null || pool == null) return false;
            for (int i = 0; i < pool.Length; i++)
                if (pool[i] == clip) return true;
            return false;
        }

        /// <summary>
        /// Find a clip in the pool by its asset name (case-insensitive). Returns
        /// null if not found. Used to cache signature/canonical tracks.
        /// </summary>
        private static AudioClip FindClipByName(AudioClip[] pool, string name)
        {
            if (pool == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i] != null
                    && string.Equals(pool[i].name, name, System.StringComparison.OrdinalIgnoreCase))
                    return pool[i];
            }
            return null;
        }
    }
}
