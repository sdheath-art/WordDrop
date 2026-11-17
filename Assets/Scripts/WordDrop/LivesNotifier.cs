using System;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using Unity.Notifications.iOS;
#endif

namespace WordDrop
{
    /// <summary>
    /// 11-16 launch item (D7 return reason): one local notification, "lives are full", scheduled for the moment the last missing
    /// heart regenerates and cancelled when lives are refilled. iOS only, via com.unity.mobile.notifications; the permission prompt
    /// is asked the FIRST time a life is lost (not at launch, when it would be refused). Compiled out of the editor and Android.
    /// </summary>
    public static class LivesNotifier
    {
        public static bool ENABLED = true;
        private const string ID = "wd_lives_full";

#if UNITY_IOS && !UNITY_EDITOR
        private static AuthorizationRequest s_auth;
#endif

        public static void Reschedule()
        {
            if (!ENABLED) return;
            try
            {
#if UNITY_IOS && !UNITY_EDITOR
                iOSNotificationCenter.RemoveScheduledNotification(ID);
                TimeSpan wait = HeartsManager.TimeUntilFull;
                if (wait <= TimeSpan.Zero) return;
                if (s_auth == null)
                    s_auth = new AuthorizationRequest(AuthorizationOption.Alert | AuthorizationOption.Badge | AuthorizationOption.Sound, false);
                iOSNotificationCenter.ScheduleNotification(new iOSNotification
                {
                    Identifier = ID,
                    Title = "Your lives are full!",
                    Body = "All 5 lives are ready. Come spell some words!",
                    ShowInForeground = false,
                    Trigger = new iOSNotificationTimeIntervalTrigger { TimeInterval = wait, Repeats = false },
                });
                WDLog.Log($"[LivesNotifier] lives-full notification in {wait.TotalMinutes:0} min");
#endif
            }
            catch (Exception e) { Debug.LogWarning($"[LivesNotifier] {e.Message}"); }
        }
    }
}
