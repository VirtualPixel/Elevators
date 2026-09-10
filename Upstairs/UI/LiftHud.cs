using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Upstairs.Configuration;
using Upstairs.Services;

namespace Upstairs.UI
{
    // One line in the game's own HUD font, bottom centre, pointing the host at the nearest lift pad.
    // The pad is a spot with no marker of its own, so without this the host has nothing to walk toward.
    internal sealed class LiftHud : MonoBehaviour
    {
        private const float FontRetrySeconds = 1f;
        private const float HideWithin = 2.2f;
        private const float SameFloor = 8f;

        private RectTransform root = null!;
        private TextMeshProUGUI? text;
        private float nextFontTry;

        private void Awake()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32740;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            root = new GameObject("line", typeof(RectTransform)).GetComponent<RectTransform>();
            root.SetParent(transform, false);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.anchoredPosition = new Vector2(0f, 150f);
            root.sizeDelta = new Vector2(900f, 40f);
        }

        private void Update()
        {
            string line = CurrentLine();
            if (line.Length == 0)
            {
                if (text != null)
                    text.enabled = false;
                return;
            }
            if (!BuildText())
                return;
            text!.enabled = true;
            text.text = line;
        }

        private static string CurrentLine()
        {
            if (!PluginConfig.ShowLiftHint.Value || !SemiFunc.RunIsLevel())
                return string.Empty;
            if (GameDirector.instance == null || GameDirector.instance.currentState != GameDirector.gameState.Main)
                return string.Empty;
            PlayerAvatar me = PlayerAvatar.instance;
            LiftService? lift = LiftService.Instance;
            Camera cam = Camera.main;
            if (me == null || lift == null || cam == null || me.isDisabled || me.deadSet)
                return string.Empty;

            Vector3 from = me.transform.position;
            if (!lift.NearestPad(from, SameFloor, out Vector3 pad, out string name))
                return string.Empty;

            Vector3 flat = pad - from;
            float rise = flat.y;
            flat.y = 0f;
            float distance = flat.magnitude;
            string look = Elevator.LookingAt();
            if (look.Length > 0)
                return look == "call" ? "ELEVATOR  grab to call it" : look.EndsWith("hatch") ? $"ELEVATOR  grab to {look}" : $"ELEVATOR  grab: {look}";
            switch (Elevator.LocalStatus())
            {
                case "riding":
                    return string.Empty;
                case "moving":
                    return "ELEVATOR  on its way";
                case "inside":
                    return "ELEVATOR  buttons on the wall by the door";
            }
            if (distance < HideWithin && Mathf.Abs(rise) < 2f)
                return Elevator.Built ? "ELEVATOR  call it with the button" : "LIFT  stand still";

            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            float angle = forward.sqrMagnitude < 0.001f ? 0f : Vector3.SignedAngle(forward, flat, Vector3.up);
            return Describe(name, distance, angle, rise);
        }

        internal static string Describe(string pad, float distance, float angle, float rise)
        {
            string way;
            float a = Mathf.Abs(angle);
            if (a <= 30f)
                way = "ahead";
            else if (a >= 150f)
                way = "behind you";
            else
            {
                string side = angle > 0f ? "right" : "left";
                way = a < 100f ? "to your " + side : "behind you to the " + side;
            }
            if (rise > 6f)
                way += ", above";
            else if (rise < -6f)
                way += ", below";
            string label = Elevator.Built ? "ELEVATOR" : pad.StartsWith("ground") ? "LIFT" : pad.StartsWith("upstairs") ? "LIFT DOWN" : "LIFT UP";
            return $"{label}  {Mathf.RoundToInt(distance)} m, {way}";
        }

        private bool BuildText()
        {
            if (text != null)
                return true;
            if (Time.unscaledTime < nextFontTry)
                return false;
            if (!NativeText.TryCaptureFont())
            {
                nextFontTry = Time.unscaledTime + FontRetrySeconds;
                return false;
            }
            text = NativeText.Create(root, "text", 24f, TextAlignmentOptions.Center);
            RectTransform rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(900f, 40f);
            return true;
        }
    }
}
