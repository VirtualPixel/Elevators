using HarmonyLib;
using TMPro;
using UnityEngine;
using Elevators.Services;

namespace Elevators.Patches
{
    // With the map open, the scroll wheel steps through the floors. The game draws whatever
    // layer PlayerLayer says, so the viewed floor is written there each frame before the map
    // lays itself out; its own tracker only writes that field every tenth of a second. After
    // the layout, every layer that belongs to another floor is dropped out of the camera's
    // reach, so a tall ground room never shows through the floor above it, and the floor's name
    // is written on the map screen in the map's own lettering.
    [HarmonyPatch(typeof(Map), "Update")]
    internal static class MapFloorsPatch
    {
        private const float Hidden = -5f;
        private const float LabelDepth = 0.6f;
        private const float LabelMargin = 0.2f;
        private const string LabelName = "Elevators floor";

        private static int viewing = -1;
        private static int playerFloor = -1;
        private static TMP_Text? label;
        private static bool playerHidden;

        internal static string ViewingName { get; private set; } = "";
        internal static int Viewing { get => viewing; set => viewing = value; }

        private static void Prefix(Map __instance)
        {
            if (!__instance.Active)
            {
                viewing = -1;
                playerFloor = -1;
                ViewingName = "";
                ShowLabel(false);
                ShowPlayer(__instance, true);
                foreach (MapModule module in __instance.MapModules)
                {
                    if (module != null)
                        Show(module.gameObject, true);
                }
                if (__instance.OverLayerParent != null)
                {
                    foreach (Transform mark in __instance.OverLayerParent)
                    {
                        if (mark.GetComponent<MapValuable>() != null || mark.GetComponent<MapCustomEntity>() != null)
                            Show(mark.gameObject, true);
                    }
                }
                return;
            }
            LiftService? lift = LiftService.Instance;
            if (lift == null || lift.FloorCount < 2)
                return;
            // Opens on the floor you stand on, and follows you if you change floors while it is up.
            PlayerAvatar me = PlayerAvatar.instance;
            int mine = me != null ? lift.FloorIndexOfHeight(me.transform.position.y) : lift.FloorIndexOfLayer(__instance.PlayerLayer);
            if (viewing < 0 || mine != playerFloor)
                viewing = mine;
            playerFloor = mine;

            float scroll = Input.mouseScrollDelta.y;
            if (scroll > 0.01f || scroll < -0.01f)
            {
                int next = Mathf.Clamp(viewing + (scroll > 0f ? 1 : -1), 0, lift.FloorCount - 1);
                if (next != viewing)
                {
                    viewing = next;
                    if (MenuManager.instance != null && MenuManager.instance.soundTick != null && Camera.main != null)
                        MenuManager.instance.soundTick.Play(Camera.main.transform.position);
                }
            }
            __instance.PlayerLayer = lift.FloorLayer(viewing);
            ViewingName = lift.MapFloorName(viewing);
        }

        private static void Postfix(Map __instance)
        {
            if (!__instance.Active)
                return;
            LiftService? lift = LiftService.Instance;
            if (lift == null || lift.FloorCount < 2 || viewing < 0)
            {
                ShowLabel(false);
                return;
            }
            foreach (MapLayer layer in __instance.Layers)
            {
                if (lift.FloorIndexOfLayer(layer.layer) == viewing)
                    continue;
                Vector3 at = layer.transform.localPosition;
                layer.transform.localPosition = new Vector3(at.x, Hidden, at.z);
            }
            // Room markers and outlines hang off the module's own map object, wherever the game
            // parented it, so those follow the floor of the room they stand for.
            // Renderers only: switching the objects off would kill a room's "discovered" animation
            // or a marker's follow routine mid-way and leave a question mark for good.
            foreach (MapModule module in __instance.MapModules)
            {
                if (module == null || module.module == null)
                    continue;
                Show(module.gameObject, lift.FloorIndexOfHeight(module.module.transform.position.y) == viewing);
            }
            // Valuable dots and custom markers sit on the over layer at one height for the whole
            // level; each one follows the floor of the thing it marks.
            if (__instance.OverLayerParent != null)
            {
                foreach (Transform mark in __instance.OverLayerParent)
                {
                    float? y = null;
                    MapValuable valuable = mark.GetComponent<MapValuable>();
                    if (valuable != null && valuable.target != null)
                        y = valuable.target.transform.position.y;
                    MapCustomEntity custom = mark.GetComponent<MapCustomEntity>();
                    if (custom != null && custom.Parent != null)
                        y = custom.Parent.position.y;
                    if (y == null)
                        continue;
                    Show(mark.gameObject, lift.FloorIndexOfHeight(y.Value) == viewing);
                }
            }
            bool onViewedFloor = playerFloor < 0 || playerFloor == viewing;
            ShowPlayer(__instance, onViewedFloor);
            if (label == null)
                label = BuildLabel(__instance);
            if (label != null)
            {
                if (label.text != ViewingName)
                {
                    label.text = ViewingName;
                    // A text outside a canvas only rebuilds on its own schedule; the map camera
                    // renders by hand, so build the mesh now.
                    label.ForceMeshUpdate();
                }
                ShowLabel(true);
            }
        }

        private static void Show(GameObject go, bool show)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r.enabled != show)
                    r.enabled = show;
            }
        }

        private static void ShowPlayer(Map map, bool show)
        {
            if (map.PlayerGraphicTransform == null)
                return;
            if (show && playerHidden)
                map.PlayerGraphicTransform.gameObject.SetActive(true);
            else if (!show && !playerHidden)
                map.PlayerGraphicTransform.gameObject.SetActive(false);
            playerHidden = !show;
        }

        private static void ShowLabel(bool show)
        {
            if (label != null && label.gameObject.activeSelf != show)
                label.gameObject.SetActive(show);
        }

        // A copy of the map's own "disabled" lettering, parked in the top left corner of the map
        // camera's view, tinted like the room outlines so it reads as part of the screen.
        private static TMP_Text? BuildLabel(Map map)
        {
            if (map.DisabledTransform == null || map.ActiveParent == null)
                return null;
            TMP_Text? source = map.DisabledTransform.GetComponentInChildren<TMP_Text>(true);
            Camera? cam = map.ActiveParent.GetComponentInChildren<Camera>(true);
            if (source == null || cam == null)
                return null;
            GameObject go = Object.Instantiate(source.gameObject, cam.transform);
            go.name = LabelName;
            go.SetActive(true);
            TMP_Text text = go.GetComponent<TMP_Text>();
            text.alignment = TextAlignmentOptions.TopLeft;
            text.enableWordWrapping = false;
            // Big, the map screen is only two hundred pixels across.
            text.fontSize = source.fontSize * 1.8f;
            // The map draws in one green; the player dot carries it as a plain colour.
            SpriteRenderer? dot = map.PlayerGraphicTransform != null ? map.PlayerGraphicTransform.GetComponentInChildren<SpriteRenderer>(true) : null;
            Color tint = dot != null ? dot.color : new Color(0.55f, 1f, 0.6f);
            tint.a = 1f;
            text.color = tint;
            // The disabled sign uses the lit surface shader, black under the map camera's ambient.
            // The font's own material is the plain unlit one.
            Material? plain = text.font != null ? GameCall.Read<Material>(text.font, typeof(TMP_FontAsset), "material") : null;
            Material face = plain != null ? new Material(plain) : text.fontMaterial;
            if (face != null && face.HasProperty("_FaceColor"))
                face.SetColor("_FaceColor", tint);
            if (face != null)
                text.fontMaterial = face;
            RectTransform rect = go.GetComponent<RectTransform>();
            float halfH = cam.orthographicSize;
            float halfW = halfH * cam.aspect;
            Vector3 scale = rect.localScale;
            rect.localRotation = Quaternion.identity;
            rect.localPosition = new Vector3(-halfW + LabelMargin + rect.sizeDelta.x * 0.5f * scale.x, halfH - LabelMargin - rect.sizeDelta.y * 0.5f * scale.y, LabelDepth);
            return text;
        }
    }
}
