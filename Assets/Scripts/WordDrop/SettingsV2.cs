using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// SETTINGS REVAMP (2026-11-17, Spencer approved mock v1: ~/Documents/WordDrop_Art/settings_mock/settings_mock_sheet.png,
    /// reference = Royal Kingdom's settings).
    ///
    ///  IN A LEVEL  - the gear opens no card. The screen dims, the gear stays lit and four round buttons stack above it:
    ///                red exit, then green music / sound / vibration (red slash = off). Exit -> "Quit Level?" card.
    ///  HOME SCREEN - one card in the level card's kit: a recessed row of four round toggles (Music, Sound, Vibration, Hints,
    ///                word above each), Support, Terms &amp; Privacy, version line. No tabs, no sliders, no Help.
    ///  Terms &amp; Privacy -> a second small card: Terms of Service, Privacy Policy, "Reset Progress" text link (-> confirm card).
    ///
    /// ENABLED = false puts the old SettingsModal back everywhere. Quit rules are SettingsModal's (QUIT_LIKE_RM / QUIT_TO_HOME).
    /// Cards never close on an outside tap (locked 2026-05-30). The flyout is not a card: gear or outside tap closes it.
    /// Art in Resources/UI/Settings (round_green, round_red, ic_*, slash) and UI/Buttons/rect_quit_* is PLACEHOLDER
    /// (recoloured kit art + drawn icons) until Spencer's icons land - same file names, drop-in.
    /// </summary>
    public static class SettingsV2
    {
        public static bool ENABLED = true;
        public static bool FLYOUT_OUTSIDE_TAP_CLOSES = true;
        public static bool SHOW_VERSION = true;
        /// <summary>Not set up yet (11-17 Spencer: "these are all not built yet so just keep them as dead links for now").
        /// SHOW_DEAD_LINKS = true: the buttons are shown and a tap on an empty one does nothing. false = empty ones are not built.</summary>
        public static string SUPPORT_EMAIL = "", TERMS_URL = "", PRIVACY_URL = "";
        public static bool SHOW_DEAD_LINKS = true;
        private static void OpenLink(string what, string url)
        {
            if (string.IsNullOrEmpty(url)) { WDLog.Log($"[SettingsV2] {what}: no link set yet - nothing opened"); return; }
            Application.OpenURL(url);
        }

        private const int SORT_FLYOUT = 205, SORT_CARD = 210, SORT_SUB = 215;
        private const float REF_W = 1179f, REF_H = 2556f;
        private const float CW = 1000f;                       // card width in mock px (1179-wide screen), like the level card
        public static float FLY_DIA = 150f, FLY_STEP = 180f;  // flyout button size / spacing, canvas units
        private static readonly Color FLY_DIM = new Color32(10, 4, 28, 232);   // 11-17: 204 read far lighter in-game than in the mock (linear blending)
        private static readonly Color LINK_INK = new Color32(40, 34, 150, 255);

        private static GameObject s_flyout, s_card, s_sub;
        // a quit on its way home; self-clears after 8 s so a lost coroutine can never leave the gear dead
        private static bool s_quitFlag; private static float s_quitAt;
        private static bool s_quitting
        {
            get => s_quitFlag && Time.unscaledTime - s_quitAt < 8f;
            set { s_quitFlag = value; s_quitAt = Time.unscaledTime; }
        }
        public static bool IsOpen => s_flyout != null || s_card != null || s_sub != null;

        // ── the four switches ───────────────────────────────────────────────────────────────────
        private static bool MusicOn
        {
            get => GameAudio.Instance == null || !GameAudio.Instance.MusicMuted;
            set
            {
                var a = GameAudio.Instance; if (a == null) return;
                a.MusicMuted = !value;
                if (value && a.MusicVolume < 0.02f) a.MusicVolume = GameAudio.FIXED_MUSIC;   // an old slider left at 0% would make "on" silent
            }
        }
        private static bool SoundOn
        {
            get => GameAudio.Instance == null || !GameAudio.Instance.Muted;
            set
            {
                var a = GameAudio.Instance; if (a == null) return;
                a.Muted = !value; a.UIMuted = !value;
                if (value && a.Volume < 0.02f) a.Volume = 0.8f;
            }
        }
        private static bool VibeOn { get => HapticsManager.Enabled; set => HapticsManager.Enabled = value; }
        private static bool HintsOn { get => HintManager.IsEnabled(); set => HintManager.SetEnabled(value); }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  IN-LEVEL FLYOUT
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Opens the flyout above the gear. False = could not (no gear / art missing) - caller falls back to the old modal.</summary>
        public static bool ShowFlyout()
        {
            if (s_quitting) return true;                 // swallow taps while a quit is on its way home
            if (s_flyout != null) { CloseFlyout(); return true; }
            var hud = BoosterHUDSlot.Instance; var gear = hud != null ? hud.SettingsSlotRT : null;
            if (gear == null || Art("round_green") == null || Art("round_red") == null) return false;

            SurvivalManager.Instance?.SetOverlayPaused(true);   // board + boosters dead while it is up
            s_flyout = NewCanvas("SettingsFlyout", SORT_FLYOUT);
            var root = (RectTransform)s_flyout.transform;

            var dim = NewImage("Dim", root, null, new Color(FLY_DIM.r, FLY_DIM.g, FLY_DIM.b, 0f)); Stretch(dim.rectTransform);
            dim.raycastTarget = true;
            dim.DOFade(FLY_DIM.a, 0.15f).SetUpdate(true);
            var db = dim.gameObject.AddComponent<Button>(); db.transition = Selectable.Transition.None;
            db.onClick.AddListener(() => { if (FLYOUT_OUTSIDE_TAP_CLOSES) CloseFlyout(); });

            // The gear's centre in THIS canvas. Both canvases scale by height against 1179x2556, so the factor is known without
            // waiting a frame for the CanvasScaler (reading a fresh canvas's size is the ChapterEndCard 11-06 bug).
            var gc = gear.GetComponentInParent<Canvas>();
            Camera cam = gc != null && gc.renderMode != RenderMode.ScreenSpaceOverlay ? gc.worldCamera : null;
            var wc = new Vector3[4]; gear.GetWorldCorners(wc);
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, (wc[0] + wc[2]) * 0.5f);
            float sf = Screen.height / REF_H;
            Vector2 centre = (sp - new Vector2(Screen.width, Screen.height) * 0.5f) / sf;

            // a lit copy of the gear on top of the dim; tapping it closes
            // (cloned under an INACTIVE holder so none of the slot's scripts ever wake up on the copy; only its pictures are kept)
            var holder = new GameObject("Holder", typeof(RectTransform)); holder.transform.SetParent(root, false); holder.SetActive(false);
            var copy = Object.Instantiate(gear.gameObject, holder.transform);
            copy.name = "GearLit";
            foreach (var mb in copy.GetComponentsInChildren<MonoBehaviour>(true)) if (mb != null && !(mb is Graphic)) Object.DestroyImmediate(mb);
            foreach (var g in copy.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            copy.transform.SetParent(root, false); copy.SetActive(true); Object.Destroy(holder);
            var crt = (RectTransform)copy.transform;
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.sizeDelta = gear.rect.size; crt.localScale = Vector3.one; crt.localRotation = Quaternion.identity;
            crt.anchoredPosition = centre + Vector2.Scale(crt.pivot - new Vector2(0.5f, 0.5f), gear.rect.size);
            var hit = NewImage("GearHit", root, null, new Color(1f, 1f, 1f, 0f)); hit.raycastTarget = true;
            PlaceCentre(hit.rectTransform, centre, new Vector2(FLY_DIA, FLY_DIA));
            var hb = hit.gameObject.AddComponent<Button>(); hb.transition = Selectable.Transition.None;
            hb.onClick.AddListener(() => { GameAudio.Instance?.PlaySettingsRelease(); CloseFlyout(); });

            // bottom to top: vibration, sound, music, exit
            float x = Mathf.Min(centre.x, Screen.width * 0.5f / sf - FLY_DIA * 0.55f);   // never off the right edge
            MakeToggle(root, "Vibration", "ic_vibe", new Vector2(x, centre.y + FLY_STEP * 1f), FLY_DIA, () => VibeOn, v => VibeOn = v, 0);
            MakeToggle(root, "Sound", "ic_sound", new Vector2(x, centre.y + FLY_STEP * 2f), FLY_DIA, () => SoundOn, v => SoundOn = v, 1);
            MakeToggle(root, "Music", "ic_music", new Vector2(x, centre.y + FLY_STEP * 3f), FLY_DIA, () => MusicOn, v => MusicOn = v, 2);
            // 11-20 (tester: "some kind of thing that explains what each piece does ... how boosters work"): a ? button -> How to Play
            int top = 4;
            if (HELP_BUTTON)
            {
                var help = RoundButton(root, "HelpBtn", "ic_hint", false, FLY_DIA, out _);
                var hic = help.transform.Find("Icon"); if (hic != null) hic.gameObject.SetActive(false);
                var q = Text(help.transform, "?", FLY_DIA * 0.50f, Vector2.zero, Vector2.zero);
                PlaceCentre(q.rectTransform, new Vector2(0f, FLY_DIA * 0.024f), new Vector2(FLY_DIA, FLY_DIA));
                HUDManager.ApplyRecipeCount(q, HELP_Q_LINE, 0.19f, -0.9f);
                PlaceCentre((RectTransform)help.transform, new Vector2(x, centre.y + FLY_STEP * 4f), RoundSize(FLY_DIA));
                PopStagger(help.transform, 3);
                help.onClick.AddListener(ShowHelpCard);
                top = 5;
            }
            var exit = RoundButton(root, "ExitBtn", "ic_exit", true, FLY_DIA, out _);
            PlaceCentre((RectTransform)exit.transform, new Vector2(x, centre.y + FLY_STEP * top), RoundSize(FLY_DIA));
            PopStagger(exit.transform, top - 1);
            exit.onClick.AddListener(OnExitTapped);
            return true;
        }

        public static void CloseFlyout()
        {
            if (s_flyout == null) return;
            var go = s_flyout; s_flyout = null;
            DOTween.Kill(go.transform, false);
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) DOTween.Kill(t);
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) DOTween.Kill(g);
            Object.Destroy(go);
            if (!s_quitting) SurvivalManager.Instance?.SetOverlayPaused(false);
        }

        private static void OnExitTapped()
        {
            if (s_quitting || s_sub != null) return;
            bool played = StageStats.MovesMade > 0;
            if (!SettingsModal.QUIT_LIKE_RM || !played)
            {   // leaving before the first move costs nothing (RM) - no question
                WDLog.Log("[SettingsV2] Quit level - no move made yet (or RM quit off), nothing lost");
                DoQuit(); return;
            }
            ShowQuitCard();
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  QUIT LEVEL? CARD
        // ═════════════════════════════════════════════════════════════════════════════════════════
        private static void ShowQuitCard()
        {
            if (s_flyout != null) s_flyout.SetActive(false);   // our card is wider than RK's - the flyout would sit half under it
            const float CH = 834f;
            var card = BuildCard("QuitLevelCard", SORT_SUB, "Quit Level?", LevelIntroModal.V2_TITLE_CAP, CH, true, out s_sub, () =>
            {   // X = cancel: back to the level
                CloseSub(() => { CloseFlyout(); if (!s_quitting) SurvivalManager.Instance?.SetOverlayPaused(false); });
            });
            var box = CreamBox(card, 210f, 300f);
            var tex = Resources.Load<Texture2D>("Hearts/heart_broken");
            if (tex != null)
            {
                var h = NewImage("BrokenHeart", box, Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f), Color.white);
                h.preserveAspect = true; Place(h.rectTransform, (CW - 140f) / 2f, 112f, 160f, 160f);
            }
            var body = Text(box, "You will lose a life!", LevelIntroModal.V2_BODY_CAP, new Vector2((CW - 140f) / 2f, 236f), new Vector2(CW - 180f, 90f));
            HUDManager.ApplyRecipeFlat(body); body.color = LevelIntroModal.V2_LABEL_INK;

            var quit = ArtButton.Create(card, "rect_quit", "Quit", lilita: false, titleMedium: false);
            if (quit == null) quit = ArtButton.Create(card, "rect_play", "Quit", lilita: false, titleMedium: false);
            if (quit != null)
            {
                SizeButton(quit, CW / 2f, 672f, 204f, 0f);
                quit.ScaleLabel(ArtButton.SMALL_BUTTON_LABEL);   // 11-18: 5.2% -> 6.8% of the card (his pick B)
                quit.Button.onClick.AddListener(() =>
                {
                    if (s_quitting) return;
                    HeartsManager.Consume();
                    int streak = WinStreak.Count; WinStreak.Reset();
                    try { AnalyticsManager.Log("level_quit", "stage", SurvivalManager.Instance != null ? SurvivalManager.Instance.CurrentStageIndex : -1, "moves_used", StageStats.MovesMade, "lives_left", HeartsManager.Current, "streak_lost", streak); }
                    catch (System.Exception ex) { Debug.LogError($"[SettingsV2] Analytics threw: {ex.Message}"); }
                    WDLog.Log($"[SettingsV2] Quit level confirmed - life taken ({HeartsManager.Current} left), streak {streak} -> 0");
                    DoQuit();
                });
            }
        }

        /// <summary>Home screen, SAME level (SettingsModal.DoQuitLevel's route, minus the old card). The pause stays on until the
        /// level is rebuilt under the home screen - RetryCurrentLevel hands it over.</summary>
        private static void DoQuit()
        {
            if (s_quitting) return;
            s_quitting = true;
            CloseSub(null); CloseFlyout();
            var sm = SurvivalManager.Instance;
            if (SettingsModal.QUIT_TO_HOME && sm != null && SurvivalManager.IsSurvivalMode && LevelMapPanel.MapFlowEnabled) { sm.StartCoroutine(QuitToHome(sm)); return; }
            s_quitting = false;
            if (GameManager.Instance != null) GameManager.Instance.TransitionTo(GameState.Menu);
        }
        private static System.Collections.IEnumerator QuitToHome(SurvivalManager sm)
        {
            float until = Time.unscaledTime + 4f;   // let a move that is still resolving finish first
            while (Time.unscaledTime < until && MatchController.Instance != null && MatchController.Instance.IsProcessing) yield return null;
            s_quitting = false;
            if (sm != null) sm.RetryCurrentLevel(openCard: false);
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  HOME SETTINGS CARD
        // ═════════════════════════════════════════════════════════════════════════════════════════
        public static void ShowHome()
        {
            if (s_card != null) return;
            bool support = SHOW_DEAD_LINKS || !string.IsNullOrEmpty(SUPPORT_EMAIL);
            int rows = (support ? 1 : 0) + 1 + (HELP_IN_HOME ? 1 : 0);
            const float ROW0 = 685f, ROW_STEP = 225f, BTN_H = 190f;
            float lastY = ROW0 + (rows - 1) * ROW_STEP;
            float CH = lastY + BTN_H / 2f + (SHOW_VERSION ? 130f : 70f);

            var card = BuildCard("SettingsCard", SORT_CARD, "Settings", LevelIntroModal.V2_TITLE_CAP, CH, false, out s_card, CloseHome);

            // recessed pocket: dark top edge, light catch edge below (raised = tappable, recessed = container)
            var sp = MenuUI.GetRoundedRectSprite(42);
            var lightE = NewImage("WellLight", card, sp, new Color32(112, 78, 210, 255)); lightE.type = Image.Type.Sliced; Place(lightE.rectTransform, CW / 2f, 375f, 860f, 340f);
            var darkE = NewImage("WellDark", card, sp, new Color32(24, 8, 70, 255)); darkE.type = Image.Type.Sliced; Place(darkE.rectTransform, CW / 2f, 365f, 860f, 340f);
            var fill = NewImage("Well", card, sp, new Color32(44, 20, 118, 255)); fill.type = Image.Type.Sliced; Place(fill.rectTransform, CW / 2f, 368f, 850f, 330f);

            float[] xs = { 178.25f, 392.75f, 607.25f, 821.75f };
            string[] names = { "Music", "Sound", "Vibration", "Hints" };
            string[] icons = { "ic_music", "ic_sound", "ic_vibe", "ic_hint" };
            System.Func<bool>[] gets = { () => MusicOn, () => SoundOn, () => VibeOn, () => HintsOn };
            System.Action<bool>[] sets = { v => MusicOn = v, v => SoundOn = v, v => VibeOn = v, v => HintsOn = v };
            for (int i = 0; i < 4; i++)
            {
                var lab = Text(card, names[i], 38f, new Vector2(xs[i], 248f), new Vector2(230f, 70f));
                HUDManager.ApplyRecipeCount(lab, LevelIntroModal.V2_TITLE_LINE, 0.19f, -0.9f);
                var b = MakeToggle(card, names[i], icons[i], Vector2.zero, 170f, gets[i], sets[i], -1);
                Place((RectTransform)b.transform, xs[i], 396f, RoundSize(170f).x, RoundSize(170f).y);
            }

            float y = ROW0;
            if (HELP_IN_HOME)
            {   // 11-23 Spencer: How to Play was only behind the gear inside a level - it belongs in the Settings screen too
                var h = ArtButton.Create(card, "rect_map", "How to Play", lilita: false, titleMedium: false);
                if (h != null) { SizeButton(h, CW / 2f, y, BTN_H, 373f); h.Button.onClick.AddListener(ShowHelpCard); }
                y += ROW_STEP;
            }
            if (support)
            {
                var s = ArtButton.Create(card, "rect_map", "Support", lilita: false, titleMedium: false);
                if (s != null) { SizeButton(s, CW / 2f, y, BTN_H, 373f); s.Button.onClick.AddListener(OpenSupport); }
                y += ROW_STEP;
            }
            var tp = ArtButton.Create(card, "rect_map", "Terms & Privacy", lilita: false, titleMedium: false);
            if (tp != null) { SizeButton(tp, CW / 2f, y, BTN_H, 373f); tp.Button.onClick.AddListener(ShowTermsCard); }

            if (SHOW_VERSION)
            {
                // 11-18: a number after the version = how many times ClockGuard had to un-freeze the game clock on this device
                var v = Text(card, "Version " + Application.version + (ClockGuard.Fired > 0 || ClockGuard.Errors > 0 ? "  \u00b7 c" + ClockGuard.Fired + " e" + ClockGuard.Errors : ""), 26f, new Vector2(CW / 2f, lastY + BTN_H / 2f + 62f), new Vector2(700f, 60f));
                HUDManager.ApplyRecipeFlat(v); v.color = new Color32(176, 150, 240, 255);
            }
        }

        private static void CloseHome()
        {
            if (s_card == null) return;
            var go = s_card; s_card = null;
            SettingsModal.RaiseClosed();   // the nav bar moves its dome back off the settings tab
            if (!go.activeInHierarchy) { Object.Destroy(go); return; }
            FadeOutAndDestroy(go);
        }

        private static void OpenSupport()
        {
            if (string.IsNullOrEmpty(SUPPORT_EMAIL)) { OpenLink("Support", null); return; }
            string subject = System.Uri.EscapeDataString($"WordDrop feedback (v{Application.version})");
            Application.OpenURL($"mailto:{SUPPORT_EMAIL}?subject={subject}");
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  TERMS & PRIVACY CARD  +  RESET PROGRESS
        // ═════════════════════════════════════════════════════════════════════════════════════════
        private static void ShowTermsCard()
        {
            if (s_sub != null) return;
            bool terms = SHOW_DEAD_LINKS || !string.IsNullOrEmpty(TERMS_URL), priv = SHOW_DEAD_LINKS || !string.IsNullOrEmpty(PRIVACY_URL);
            int n = (terms ? 1 : 0) + (priv ? 1 : 0);
            const float BTN_H = 190f, STEP = 225f;   // same size as the Settings card's buttons
            float boxH = (n > 0 ? 50f + n * STEP : 40f) + 110f;
            float CH = 210f + boxH + 70f;
            if (s_card != null) s_card.SetActive(false);   // one card at a time (11-17 Spencer: the stacked pair "looks off")
            var card = BuildCard("TermsCard", SORT_SUB, "Terms & Privacy", LONG_TITLE_CAP, CH, true, out s_sub, () => CloseSub(null));
            var box = CreamBox(card, 210f, boxH);
            float bw = CW - 140f, y = 50f + BTN_H / 2f;
            if (terms)
            {
                var b = ArtButton.Create(box, "rect_map", "Terms of Service", lilita: false, titleMedium: false);
                if (b != null) { SizeButton(b, bw / 2f, y, BTN_H, 373f); b.Button.onClick.AddListener(() => OpenLink("Terms of Service", TERMS_URL)); }
                y += STEP;
            }
            if (priv)
            {
                var b = ArtButton.Create(box, "rect_map", "Privacy Policy", lilita: false, titleMedium: false);
                if (b != null) { SizeButton(b, bw / 2f, y, BTN_H, 373f); b.Button.onClick.AddListener(() => OpenLink("Privacy Policy", PRIVACY_URL)); }
            }
            var link = Text(box, "<u>Reset Progress</u>", 36f, new Vector2(bw / 2f, boxH - 62f), new Vector2(520f, 90f));
            HUDManager.ApplyRecipeFlat(link); link.color = LINK_INK; link.richText = true;
            link.raycastTarget = true;
            var lb = link.gameObject.AddComponent<Button>(); lb.transition = Selectable.Transition.None;
            lb.onClick.AddListener(() => { GameAudio.Instance?.PlaySettingsRelease(); CloseSub(ShowResetCard); });
        }

        private static void ShowResetCard()
        {
            if (s_sub != null) return;
            const float CH = 834f;
            if (s_card != null) s_card.SetActive(false);
            var card = BuildCard("ResetCard", SORT_SUB, "Reset Progress?", LONG_TITLE_CAP, CH, true, out s_sub, () => CloseSub(null));
            var box = CreamBox(card, 210f, 300f);
            // 11-18 Spencer: "text in the reset progress is too big" at the card body size (53) - two lines, the longer 25 letters
            // against the Quit card's 21: 44 keeps the same margins either side.
            var body = Text(box, "Start again from Level 1.\nThis can't be undone.", RESET_BODY_CAP, new Vector2((CW - 140f) / 2f, 150f), new Vector2(CW - 180f, 220f));
            HUDManager.ApplyRecipeFlat(body); body.color = LevelIntroModal.V2_LABEL_INK; body.lineSpacing = 8f;
            var go = ArtButton.Create(card, "rect_quit", "Reset", lilita: false, titleMedium: false);
            if (go == null) go = ArtButton.Create(card, "rect_play", "Reset", lilita: false, titleMedium: false);
            if (go != null)
            {
                SizeButton(go, CW / 2f, 672f, 204f, 0f);
                go.ScaleLabel(ArtButton.SMALL_BUTTON_LABEL);     // 11-18: same button as Quit
                go.Button.onClick.AddListener(() => CloseSub(() =>
                {
                    // 11-17: monkey run 534391 pressed Reset 5 times in 10 minutes and never got past level 5. A real player has to
                    // mean it (Settings > Terms & Privacy > Reset Progress > Reset); the monkey just mashes. While it runs, Reset closes.
#if UNITY_EDITOR || DEVELOPMENT_BUILD   // the monkey only exists in the editor / dev builds (a release build failed on this line, 11-17)
                    if (MonkeyTester.Instance != null && MonkeyTester.Instance.Running) { WDLog.Log("[SettingsV2] Reset pressed by the monkey - ignored"); return; }
#endif
                    CloseHome(); DoResetProgress();
                }));
            }
        }

        /// <summary>Same clean slate as the FX Test Menu's "Tutorial - RESET (as if never played)", then the level under the home
        /// screen is rebuilt at the start. Coins and lives are left alone.</summary>
        private static void DoResetProgress()
        {
            SurvivalManager.TutorialDone = false;
            TutorialManager.ResetTutorial();
            TutorialBeats.ResetAll();
            SurvivalManager.ClearSavedProgress();
            BoosterHUDSlot.ResetSlotsSeen();
            PlayerPrefs.DeleteKey("wild_unlock_taught");
            WinStreak.Reset();
            PlayerPrefs.Save();
            WDLog.Log("[SettingsV2] progress reset from Settings - back to the first level");
            SurvivalManager.Instance?.RestartFromSavedProgress();
        }

        private static void CloseSub(System.Action then)
        {
            if (s_card != null && !s_card.activeSelf) s_card.SetActive(true);   // back to the Settings card underneath
            if (s_sub == null) { then?.Invoke(); return; }
            var go = s_sub; s_sub = null;
            FadeOutAndDestroy(go);
            then?.Invoke();
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  HOW TO PLAY CARD (11-20). Mock approved by Spencer ("that looks great"): three tabs - Power-ups / Boosters / Pieces -
        //  one row per thing: picture, name, one or two short lines. Rows the player has not met yet are hidden.
        // ═════════════════════════════════════════════════════════════════════════════════════════
        public static bool HELP_BUTTON = true;       // the ? in the gear's fly-out
        public static bool HELP_IN_HOME = true;      // 11-23: a "How to Play" button on the home Settings card as well
        public static bool HELP_HIDE_UNMET = true;   // false = every row from level 1
        public static float HELP_BODY_CAP = 30f, HELP_TAB_CAP = 28f;   // pass 2: were 34 / 41 ("Power-ups" was wider than its tab); 4 tabs: 34 -> 28
        private static readonly Color HELP_Q_LINE = new Color32(22, 78, 10, 255);
        private static readonly Color HELP_INK2 = new Color32(96, 70, 150, 255), HELP_RULE = new Color32(226, 208, 170, 255);
        private static readonly Color HELP_TAB_ON = new Color32(244, 232, 203, 255), HELP_TAB_ON_EDGE = new Color32(250, 190, 60, 255);
        private static readonly Color HELP_TAB_OFF = new Color32(44, 20, 118, 255), HELP_TAB_OFF_EDGE = new Color32(112, 78, 210, 255);
        private sealed class HelpRow
        {
            public string Art, Name, A, B; public int From; public string BoosterId;
            public HelpRow(string art, string name, string a, string b, int from, string boosterId = null) { Art = art; Name = name; A = a; B = b; From = from; BoosterId = boosterId; }
        }
        // From = the level the thing is first taught / appears on (LevelRules table). Boosters: the slot's unlock level.
        private static readonly HelpRow[] HELP_POWER =
        {
            new HelpRow("Specials/special_rocket",   "Rocket",      "Spell a 5-letter word.",      "Clears a whole row or column.",    3),
            new HelpRow("Specials/special_disco",    "Royal Orb",   "Spell 6 letters or more.",    "Clears every tile of one letter.", 5),
            new HelpRow("Specials/special_bomb",     "Bomb",        "Cross two words.",            "Blows up the tiles around it.",    4),
            new HelpRow("Specials/special_airplane", "Flying Tome", "Make two words in one move.", "Flies to a goal and hits it.",     10),
        };
        private static readonly HelpRow[] HELP_BOOST =
        {
            new HelpRow(null, "Glove",     "Swap any two tiles.",   null, 0, BoosterManager.ID_FREE_SWAP),
            new HelpRow(null, "Hammer",    "Smash one tile.",       null, 1, BoosterManager.ID_MALLET),
            new HelpRow(null, "Lightning", "Clear a whole row.",    null, 2, BoosterManager.ID_BRAMBLE_SWEEP),
            new HelpRow(null, "Axe",       "Clear a whole column.", null, 3, BoosterManager.ID_AXE),
            new HelpRow(null, "Spyglass",  "Shows the best word.",  null, 4, BoosterManager.ID_SPYGLASS),
        };
        private static readonly HelpRow[] HELP_PIECE =
        {
            new HelpRow("Tiles/gem_teal",     "Gem",       "Use its letter in a word.",          null, 1),
            new HelpRow("Blockers/crate",     "Crate",     "Make a word next to it.",            null, 6),
            new HelpRow("Tiles/ice_overlay",  "Ice",       "Use the frozen letter in a word.",   null, 9),
            new HelpRow("Blockers/bookshelf", "Bookshelf", "Make words next to it.",             null, 13),
            new HelpRow("Blockers/safe",      "Safe",      "Make words next to it to open it.",  null, 18),
        };
        private static Sprite ResSprite(string path)
        {
            string key = "R/" + path; if (s_sprites.TryGetValue(key, out var sp) && sp != null) return sp;
            sp = Resources.Load<Sprite>(path);
            if (sp == null) { var tex = Resources.Load<Texture2D>(path); if (tex != null) sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f); }
            s_sprites[key] = sp; return sp;
        }
        private static TextMeshProUGUI HelpText(Transform parent, string s, float cap, float x, float y, float w, bool left, bool body)
        {
            var t = Text(parent, s, cap, new Vector2(left ? x + w / 2f : x, y), new Vector2(w, cap * 1.7f));
            HUDManager.ApplyRecipeFlat(t);
            if (body)
            {   // the lighter second voice: the button face font, like the lesson bubbles
                var bf = Resources.Load<TMP_FontAsset>("WordSlop Button SDF");
                if (bf != null) { t.font = bf; t.fontSharedMaterial = bf.material; }
                t.color = HELP_INK2;
            }
            else t.color = LevelIntroModal.V2_LABEL_INK;
            if (left) t.alignment = TextAlignmentOptions.MidlineLeft;
            return t;
        }
        /// <summary>Three stacked blocks: a baked picture (2 source px = 1 mock px at 720; 0.828 card px per source px) + one line.</summary>
        private static void BuildBasicsPage(Transform page, float bw)
        {
            const float K = 0.828f;
            (string art, float w, float h, float cy, string a, string b, float ty)[] blocks =
            {
                ("UI/Help/basics_slide", 512f, 512f, 180f, "Slide a letter to move it.", null, 326f),
                HandManager.CLAIM_MODE   // 11-25 swipe-to-claim: a word does nothing until it is swiped, so the card says so
                    ? ("UI/Help/basics_word", 1024f, 256f, 492f, "Spell 4 or more letters,", "then swipe the word.", 624f)
                    : ("UI/Help/basics_word", 1024f, 256f, 492f, "Line up 4 or more letters", "to spell a word.", 624f),
                ("UI/Help/basics_goal", 1024f, 256f, 876f, "Finish the goal before", "your moves run out.", 1018f),
            };
            for (int i = 0; i < blocks.Length; i++)
            {
                var bl = blocks[i];
                var sp = ResSprite(bl.art);
                if (sp != null) { var im = NewImage("Pic", page, sp, Color.white); Place(im.rectTransform, bw / 2f, bl.cy, bl.w * K, bl.h * K); }
                else Debug.LogWarning($"[SettingsV2] missing art {bl.art}");
                HelpText(page, bl.a, 44f, bw / 2f, bl.ty, bw - 60f, false, false);
                if (bl.b != null) HelpText(page, bl.b, 44f, bw / 2f, bl.ty + 57f, bw - 60f, false, false);
                if (i > 0) { var rule = NewImage("Rule", page, null, HELP_RULE); Place(rule.rectTransform, bw / 2f, i == 1 ? 376f : 750f, bw - 80f, 4f); }
            }
        }
        private static void ShowHelpCard()
        {
            if (s_sub != null) return;
            if (s_flyout != null) s_flyout.SetActive(false);   // same as the Quit card: the card covers the fly-out's column
            bool fromHome = s_flyout == null && s_card != null;   // 11-23: opened from the home Settings card, not from a level
            if (fromHome) s_card.SetActive(false);                // one card at a time, like Terms & Privacy
            const float CH = 1590f, BOX_Y = 306f, BOX_H = 1159f, BW = CW - 140f;
            var card = BuildCard("HowToPlayCard", SORT_SUB, "How to Play", LevelIntroModal.V2_TITLE_CAP, CH, true, out s_sub, () =>
            {
                if (fromHome) { CloseSub(null); return; }           // back to the Settings card; no level to un-pause
                CloseSub(() => { CloseFlyout(); if (!s_quitting) SurvivalManager.Instance?.SetOverlayPaused(false); });
            });
            var box = CreamBox(card, BOX_Y, BOX_H);

            var sm = SurvivalManager.Instance;
            int lvl = sm != null ? LevelMapPanel.RunLevel(sm.CurrentStageIndex) : 1;
            int slots = TutorialLocks.BoosterSlotsOpen();
            var sets = new[] { HELP_POWER, HELP_BOOST, HELP_PIECE };
            // 11-20 BASICS TAB (the level-1 card was one gem row and blank; the tester who was lost needed the three rules, not the
            // pieces). Tab 0, always there, opens first until the first power-up level. Pictures: UI/Help/basics_*.png (baked).
            string[] names = { "Basics", "Power-ups", "Boosters", "Pieces" };
            string[] foot = { null, "Boosters cost no moves.", "Power-ups clear all of these too." };
            string[] none = { "Power-ups arrive at level 3.", $"Boosters unlock at level {TutorialLocks.BOOSTER_SLOT_UNLOCKS[0]}.", "" };
            const int NT = 4;
            var pages = new GameObject[NT]; var tabOn = new GameObject[NT]; var tabOff = new GameObject[NT];
            for (int pg = 0; pg < NT; pg++)
            {
                var page = new GameObject("Page" + names[pg], typeof(RectTransform)); page.transform.SetParent(box, false);
                Stretch((RectTransform)page.transform); pages[pg] = page;
                if (pg == 0) { BuildBasicsPage(page.transform, BW); continue; }
                int p = pg - 1;
                bool two = p == 0; float step = two ? 212f : 185f, y = two ? 127f : 124f; int shown = 0;
                foreach (var r in sets[p])
                {
                    bool met = !HELP_HIDE_UNMET || (r.BoosterId != null ? r.From < slots : lvl >= r.From);
                    if (!met) continue;
                    if (shown > 0) { var rule = NewImage("Rule", page.transform, null, HELP_RULE); Place(rule.rectTransform, BW / 2f, y - step / 2f, BW - 80f, 4f); }
                    var sp = r.BoosterId != null ? BoosterHUDSlot.BoosterIcon(r.BoosterId) : ResSprite(r.Art);
                    if (sp != null) { var ic = NewImage("Pic", page.transform, sp, Color.white); ic.preserveAspect = true; Place(ic.rectTransform, 116f, y, 138f, 138f); }
                    // 11-20 pass 2 (his screenshots): body 34 ran past the box ("Clears every tile of one letter." 390 px of 371
                    // on a 720 screen) and sat tight under the name -> body 30, name lifted, lines spread.
                    HelpText(page.transform, r.Name, 47f, 212f, y - (two ? 52f : 26f), 620f, true, false);
                    HelpText(page.transform, r.A, HELP_BODY_CAP, 212f, y + (two ? 6f : 30f), 620f, true, true);
                    if (r.B != null) HelpText(page.transform, r.B, HELP_BODY_CAP, 212f, y + 48f, 620f, true, true);
                    y += step; shown++;
                }
                if (shown == 0) HelpText(page.transform, none[p], 41f, BW / 2f, BOX_H / 2f, BW - 80f, false, false);
                else
                {
                    if (p == 0)
                    {
                        var rule = NewImage("Rule", page.transform, null, HELP_RULE); Place(rule.rectTransform, BW / 2f, 869f, BW - 80f, 4f);
                        HelpText(page.transform, "To set one off:", 41f, BW / 2f, 932f, BW - 80f, false, false);
                        HelpText(page.transform, "slide it into a word, or double-tap it.", HELP_BODY_CAP, BW / 2f, 992f, BW - 80f, false, true);
                        HelpText(page.transform, "A double-tap costs one move.", HELP_BODY_CAP, BW / 2f, 1040f, BW - 80f, false, true);
                    }
                    else if (p != 2 || !HELP_HIDE_UNMET || lvl >= HELP_POWER[0].From)   // "Power-ups clear all of these too." only once a power-up has been met
                        HelpText(page.transform, foot[p], 38f, BW / 2f, 1068f, BW - 80f, false, false);
                }
            }
            int first = lvl < HELP_POWER[0].From ? 0 : 1;

            System.Action<int> select = null;
            for (int p = 0; p < NT; p++)
            {
                int pi = p; float tx = 161f + 226f * p;
                var hit = NewImage("Tab" + names[p], card, null, new Color(1f, 1f, 1f, 0f)); hit.raycastTarget = true;
                Place(hit.rectTransform, tx, 207f, 216f, 110f);
                for (int k = 0; k < 2; k++)
                {
                    bool on = k == 0;
                    var edge = NewImage(on ? "On" : "Off", hit.transform, MenuUI.GetRoundedRectSprite(36), on ? HELP_TAB_ON_EDGE : HELP_TAB_OFF_EDGE); edge.type = Image.Type.Sliced;
                    Stretch(edge.rectTransform);
                    var fill = NewImage("Fill", edge.transform, MenuUI.GetRoundedRectSprite(31), on ? HELP_TAB_ON : HELP_TAB_OFF); fill.type = Image.Type.Sliced;
                    Stretch(fill.rectTransform); fill.rectTransform.offsetMin = new Vector2(5f, 5f); fill.rectTransform.offsetMax = new Vector2(-5f, -5f);
                    var lab = Text(edge.transform, names[p], HELP_TAB_CAP, Vector2.zero, Vector2.zero);
                    PlaceCentre(lab.rectTransform, new Vector2(0f, 2f), new Vector2(216f, 110f));
                    if (on) { HUDManager.ApplyRecipeFlat(lab); lab.color = LevelIntroModal.V2_LABEL_INK; }
                    else HUDManager.ApplyRecipeCount(lab, LevelIntroModal.V2_TITLE_LINE, 0.19f, -0.9f);
                    if (on) tabOn[p] = edge.gameObject; else tabOff[p] = edge.gameObject;
                }
                var tb = hit.gameObject.AddComponent<Button>(); tb.transition = Selectable.Transition.None;
                tb.onClick.AddListener(() => { GameAudio.Instance?.PlaySettingsRelease(); select(pi); });
            }
            select = sel =>
            {
                for (int p = 0; p < NT; p++) { pages[p].SetActive(p == sel); tabOn[p].SetActive(p == sel); tabOff[p].SetActive(p != sel); }
            };
            select(first);
            try { AnalyticsManager.Log("help_open", "level", lvl); } catch (System.Exception) { }
            WDLog.Log($"[SettingsV2] How to Play opened at level {lvl} (booster slots open {slots}, first tab {names[first]})");
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  BUILDERS
        // ═════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Dim + a level-card-kit card (gold-framed purple panel, arched header + title, red X on the corner), laid out in
        /// mock px inside a root scaled to the screen every frame. Returns the card; (0,0) is its top-left, y down.</summary>
        private static RectTransform BuildCard(string name, int sort, string title, float titleCap, float ch, bool instantDim, out GameObject root, System.Action onClose)
        {
            root = NewCanvas(name, sort);
            var rootRT = (RectTransform)root.transform;
            var dim = NewImage("Dim", rootRT, null, new Color(UISeparation.MODAL_DIM.r, UISeparation.MODAL_DIM.g, UISeparation.MODAL_DIM.b, 0f));
            Stretch(dim.rectTransform); dim.raycastTarget = true;   // swallows outside taps; does NOT close (locked rule)
            // a card that REPLACES another one (or the flyout) starts fully dim - a fade from clear would flash the screen bright
            if (instantDim) dim.color = UISeparation.MODAL_DIM; else dim.DOFade(UISeparation.MODAL_DIM.a, 0.2f).SetUpdate(true);

            var scaleRT = new GameObject("Scale", typeof(RectTransform)).GetComponent<RectTransform>(); scaleRT.SetParent(rootRT, false);
            scaleRT.anchorMin = scaleRT.anchorMax = scaleRT.pivot = new Vector2(0.5f, 0.5f);
            scaleRT.gameObject.AddComponent<FitScale>().canvasRT = rootRT;
            scaleRT.localScale = Vector3.one * (Screen.width / (Screen.height / REF_H) / REF_W);   // first frame, before LateUpdate
            var card = new GameObject("Card", typeof(RectTransform)).GetComponent<RectTransform>(); card.SetParent(scaleRT, false);
            card.anchorMin = card.anchorMax = card.pivot = new Vector2(0.5f, 0.5f);
            card.sizeDelta = new Vector2(CW, ch);
            card.anchoredPosition = new Vector2(0f, -40f);

            var frame = NewImage("Frame", card, PanelSprite("panel", new Vector4(205, 225, 205, 205)), Color.white); frame.type = Image.Type.Sliced; frame.raycastTarget = true;
            var frt = frame.rectTransform; frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = new Vector2(-90f, -110f); frt.offsetMax = new Vector2(90f, 90f);
            var header = NewImage("Header", card, PanelSprite("header"), Color.white);
            Place(header.rectTransform, CW / 2f, 11f, 820f, 358f);
            // 11-17 MEASURED (Spencer: "does settings seem centered to you?"): at y = 10 the capitals sat 11 px (720 screen) below the
            // plate's centre - 50 px above them, 28 below - exactly like the Try Again? card. Lifted 8 px (13 card px): the plate's top
            // is arched (highest in the middle), so a full 11 would look high at the ends of a long title.
            var t = Text(card, title, titleCap, new Vector2(CW / 2f, 10f - LevelIntroModal.V2_TITLE_LIFT), new Vector2(720f, 150f));
            HUDManager.ApplyRecipeCount(t, LevelIntroModal.V2_TITLE_LINE, 0.19f, -0.9f, Resources.Load<TMP_FontAsset>("WordSlop Title Medium SDF"));
            t.characterSpacing = 0f;

            var close = NewImage("CloseBtn", card, PanelSprite("close"), Color.white); close.raycastTarget = true;   // "CloseBtn": the monkey's way out
            Place(close.rectTransform, CW - 0.62f * 168f + 84f, -0.62f * 168f + 84f + 6f, 168f, 180f);
            var cb = close.gameObject.AddComponent<Button>(); cb.transition = Selectable.Transition.None;
            var trig = close.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => GameAudio.Instance?.PlayMultiPopPress());
            trig.triggers.Add(down);
            cb.onClick.AddListener(() => { GameAudio.Instance?.PlayMultiPopRelease(); onClose?.Invoke(); });

            UIAnimations.PopIn(card.transform);
            return card;
        }

        private static RectTransform CreamBox(RectTransform card, float y0, float h)
        {
            var box = NewImage("Box", card, PanelSprite("cream", new Vector4(52, 52, 52, 52)), Color.white); box.type = Image.Type.Sliced;
            Place(box.rectTransform, CW / 2f, y0 + h / 2f, CW - 140f, h);
            return box.rectTransform;
        }

        /// <summary>Root size <paramref name="h"/> tall at the art's aspect, 9-slice widened by <paramref name="extra"/> art px first.</summary>
        private static void SizeButton(ArtButton b, float cx, float cy, float h, float extra)
        {
            // 11-17 MEASURED (recording, 720 px): the red button's capitals sat 3.5 px under the face centre (blue ones 0-2 px) -
            // lift its label 9 art px. The blue rect_map labels are left alone.
            if (b.name == "rect_quit" || b.name == "rect_play") { var lab = b.Face != null ? b.Face.Find("Label") as RectTransform : null; if (lab != null) lab.anchoredPosition += new Vector2(0f, QUIT_LABEL_LIFT); }
            if (extra > 0f) b.Widen(extra, 130f);
            var fs = b.FrameSize;
            Place((RectTransform)b.transform, cx, cy, h * fs.x / fs.y, h); b.Refit();
        }

        public static float QUIT_LABEL_LIFT = 9f;
        private static Vector2 RoundSize(float dia) => new Vector2(dia, dia * 308f / 294f);   // the art is the disc + its bottom lip

        /// <summary>Round kit button with an icon. <paramref name="slash"/> = the red "off" bar (hidden).</summary>
        private static Button RoundButton(Transform parent, string name, string icon, bool red, float dia, out Image slash)
        {
            var img = NewImage(name, parent, Art(red ? "round_red" : "round_green"), Color.white); img.preserveAspect = true; img.raycastTarget = true;
            img.rectTransform.sizeDelta = RoundSize(dia);
            Vector2 faceC = new Vector2(0f, dia * 0.024f);   // the face sits above the art's centre (lip below)
            var ic = NewImage("Icon", img.transform, Art(icon), Color.white); ic.preserveAspect = true;
            PlaceCentre(ic.rectTransform, faceC, Vector2.one * dia * 0.56f);
            slash = NewImage("Off", img.transform, Art("slash"), Color.white); slash.preserveAspect = true;
            PlaceCentre(slash.rectTransform, faceC, Vector2.one * dia * 0.78f);
            slash.gameObject.SetActive(false);

            var b = img.gameObject.AddComponent<Button>(); b.transition = Selectable.Transition.None;
            var tr = img.transform;
            var trig = img.gameObject.AddComponent<EventTrigger>();
            var down = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            down.callback.AddListener(_ => { GameAudio.Instance?.PlaySettingsPress(); HapticsManager.ButtonTap(); DOTween.Kill(tr); tr.DOScale(0.9f, 0.06f).SetUpdate(true).SetTarget(tr); });
            var up = new EventTrigger.Entry { eventID = EventTriggerType.PointerUp };
            up.callback.AddListener(_ => { DOTween.Kill(tr); tr.DOScale(1f, 0.16f).SetEase(Ease.OutBack).SetUpdate(true).SetTarget(tr); });
            trig.triggers.Add(down); trig.triggers.Add(up);
            b.onClick.AddListener(() => GameAudio.Instance?.PlaySettingsRelease());
            return b;
        }

        /// <summary>A green on/off button. <paramref name="stagger"/> >= 0 pops it in after that many steps (flyout).</summary>
        private static Button MakeToggle(Transform parent, string name, string icon, Vector2 centre, float dia,
                                         System.Func<bool> get, System.Action<bool> set, int stagger)
        {
            var b = RoundButton(parent, name + "Toggle", icon, false, dia, out var slash);
            PlaceCentre((RectTransform)b.transform, centre, RoundSize(dia));
            slash.gameObject.SetActive(!get());
            b.onClick.AddListener(() =>
            {
                bool on = !get(); set(on);
                slash.gameObject.SetActive(!on);
                WDLog.Log($"[SettingsV2] {name} -> {(on ? "on" : "off")}");
            });
            if (stagger >= 0) PopStagger(b.transform, stagger);
            return b;
        }

        private static void PopStagger(Transform t, int i)
        {
            t.localScale = Vector3.zero;
            t.DOScale(1f, 0.22f).SetEase(Ease.OutBack).SetDelay(0.03f + 0.045f * i).SetUpdate(true).SetTarget(t);
        }

        private static void FadeOutAndDestroy(GameObject go)
        {
            if (go == null) return;
            foreach (var g in go.GetComponentsInChildren<Graphic>(true)) { g.raycastTarget = false; DOTween.Kill(g); }
            var card = go.transform.Find("Scale/Card");
            var dim = go.transform.Find("Dim") != null ? go.transform.Find("Dim").GetComponent<Image>() : null;
            if (dim != null) dim.DOFade(0f, 0.18f).SetUpdate(true);
            if (card != null) UIAnimations.PopOut(card, () => { if (go != null) Object.Destroy(go); });
            else Object.Destroy(go);
        }

        private static GameObject NewCanvas(string name, int sort)
        {
            var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var c = go.GetComponent<Canvas>(); c.renderMode = RenderMode.ScreenSpaceOverlay; c.sortingOrder = sort;
            var s = go.GetComponent<CanvasScaler>(); s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = new Vector2(REF_W, REF_H); s.matchWidthOrHeight = 1f;   // by height, like the booster row's canvas
            return go;
        }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_sprites = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite PanelSprite(string name, Vector4 border = default)
        {
            string key = name + border; if (s_sprites.TryGetValue(key, out var sp) && sp != null) return sp;
            var tex = Resources.Load<Texture2D>("UI/Panel/" + name);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border) : null;
            s_sprites[key] = sp; return sp;
        }
        private static Sprite Art(string name)
        {
            string key = "S/" + name; if (s_sprites.TryGetValue(key, out var sp) && sp != null) return sp;
            sp = Resources.Load<Sprite>("UI/Settings/" + name);
            if (sp == null)
            {   // imported as a plain texture -> still usable
                var tex = Resources.Load<Texture2D>("UI/Settings/" + name);
                if (tex != null) sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            if (sp == null) Debug.LogWarning($"[SettingsV2] missing art UI/Settings/{name}");
            s_sprites[key] = sp; return sp;
        }

        /// <summary>Centre (x, y) in mock px from the parent's TOP-LEFT (y down).</summary>
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }
        /// <summary>Centre-anchored, position from the parent's centre (y up).</summary>
        public static float RESET_BODY_CAP = 44f;
        // 11-18 (Spencer's screenshot: "terms & privacy looks like its too big for the header plate"): MEASURED - the 15-letter title
        // ran 96% of the plate's width at cap 72. 60 leaves the same side margins as "Settings" (about 80% of the plate).
        public static float LONG_TITLE_CAP = 60f;
        private static void PlaceCentre(RectTransform rt, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
        }
        private static TextMeshProUGUI Text(Transform parent, string s, float capPx, Vector2 centre, Vector2 size)
        {
            var go = new GameObject("Text", typeof(RectTransform)); go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>(); t.text = s; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap; t.fontSize = capPx / 0.594f / HUDManager.RECIPE_MAX_MUL;
            Place(t.rectTransform, centre.x, centre.y, size.x, size.y); return t;
        }
        private static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>(); im.sprite = sprite; im.color = color; im.raycastTarget = false; return im;
        }
        private static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }

        private class FitScale : MonoBehaviour
        {   // mock px -> canvas units every frame (the canvas is only sized after the scaler runs)
            public RectTransform canvasRT;
            private void LateUpdate()
            {
                if (canvasRT == null || canvasRT.rect.width <= 1f) return;
                float k = canvasRT.rect.width / REF_W; transform.localScale = new Vector3(k, k, 1f);
            }
        }
    }
}
