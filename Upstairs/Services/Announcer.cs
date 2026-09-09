using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Upstairs.Configuration;

namespace Upstairs.Services
{
    // Tells the host about the new floors through the game's own moon-phase popup, a beat
    // after the level finishes loading so the animation plays in full.
    internal sealed class Announcer : MonoBehaviour
    {
        internal static Announcer? Instance { get; private set; }

        private bool pending;
        private float delay = -1f;

        private void Awake() => Instance = this;

        public void Trigger()
        {
            if (!PluginConfig.Announce.Value)
                return;
            pending = true;
            delay = -1f;
        }

        private void Update()
        {
            if (!pending)
                return;
            LevelGenerator lg = LevelGenerator.Instance;
            if (!SemiFunc.RunIsLevel() || lg == null || !lg.Generated)
            {
                delay = -1f;
                return;
            }
            if (delay < 0f)
                delay = 2f;
            delay -= Time.deltaTime;
            if (delay > 0f)
                return;
            if (Show())
                pending = false;
        }

        private static bool Show()
        {
            MoonUI mui = MoonUI.instance;
            RunManager rm = RunManager.instance;
            if (mui == null || rm == null || mui.textTitle == null)
                return false;

            int ml = Mathf.Clamp(rm.moonLevel, 0, rm.moons.Count);
            SetGraphic(mui.moonGraphicPreviousest, rm.MoonGetIcon(ml - 2));
            SetGraphic(mui.moonGraphicPrevious, rm.MoonGetIcon(ml - 1));
            SetGraphic(mui.moonGraphicCurrent, rm.MoonGetIcon(ml));
            SetGraphic(mui.moonGraphicNext, rm.MoonGetIcon(ml + 1));

            var lines = new List<Moon.MoonAttribute>();
            if (rm.moonLevelChanged && ml > 0)
            {
                lines.Add(new Moon.MoonAttribute { text = rm.MoonGetName(ml) });
                lines.AddRange(rm.MoonGetAttributes(ml));
            }
            bool up = Session.Up != null;
            bool down = Session.Down != null;
            lines.Add(new Moon.MoonAttribute { text = up && down ? "This place has a floor above and a floor below" : up ? "This place has another floor above" : "This place has a basement" });
            lines.Add(new Moon.MoonAttribute { text = "The lift is " + Session.Hint });
            lines.Add(new Moon.MoonAttribute { text = up && down ? "Stand on it to go up, crouch on it to go down" : up ? "Stand on it and up you go" : "Crouch on it and down you go" });

            mui.textTitle.text = up && down ? "MORE FLOORS" : up ? "UPSTAIRS" : "BASEMENT";
            mui.attributes = lines;
            mui.SetState(MoonUI.State.Start);
            rm.moonLevelChanged = false;
            Plugin.LogInfo("[Announce] floors popup shown");
            return true;
        }

        private static void SetGraphic(RawImage img, Texture tex)
        {
            if (img == null)
                return;
            img.texture = tex;
            img.color = tex != null ? Color.white : Color.clear;
        }
    }
}
