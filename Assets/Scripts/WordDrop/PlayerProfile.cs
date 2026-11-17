using System;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// THE PLAYER'S PICTURE, one source for every place it shows (2026-10-29, Spencer: "make sure the profile here matches the
    /// other one and they should be linked in code so their same profile appears"). The home-screen HUD bar (HudBar) and the
    /// in-level HUD (HUDManager art HUD) both draw <see cref="Avatar"/> through <see cref="Bind"/>, so changing it once
    /// (a future profile screen calls <see cref="SetAvatar"/>) updates both. Default = Resources/UI/HUD/avatar_default (the
    /// soft white bust on blue, already circular with the recess shading).
    /// </summary>
    public static class PlayerProfile
    {
        public static event Action Changed;
        private static Sprite s_avatar, s_default;

        public static Sprite DefaultAvatar => s_default != null ? s_default : (s_default = Resources.Load<Sprite>("UI/HUD/avatar_default"));
        public static Sprite Avatar => s_avatar != null ? s_avatar : DefaultAvatar;

        /// <summary>Set the player's picture (null = back to the default). Every bound image updates.</summary>
        public static void SetAvatar(Sprite s) { s_avatar = s; Changed?.Invoke(); }

        /// <summary>Show the profile picture in <paramref name="img"/> and keep it in sync. The image should be the circular
        /// photo area; the default art is already a disc, a custom picture is masked to the image's own circle by the caller.</summary>
        public static void Bind(Image img)
        {
            if (img == null) return;
            img.sprite = Avatar; img.color = Color.white; img.preserveAspect = false;
            var b = img.GetComponent<ProfileBinding>(); if (b == null) b = img.gameObject.AddComponent<ProfileBinding>();
        }
    }

    /// <summary>Keeps one Image showing <see cref="PlayerProfile.Avatar"/>; unsubscribes when destroyed.</summary>
    public class ProfileBinding : MonoBehaviour
    {
        private Image _img;
        private void Awake() { _img = GetComponent<Image>(); PlayerProfile.Changed += Apply; }
        private void OnDestroy() { PlayerProfile.Changed -= Apply; }
        private void Apply() { if (_img != null) _img.sprite = PlayerProfile.Avatar; }
    }
}
