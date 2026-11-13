using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakSplitScreen
{
    /// <summary>
    /// Adds a "play split-screen" button to the title menu, right under the game's own
    /// "play solo" button. Solo starts a one-player offline game; the new button starts the
    /// same offline game with player 2 switched on.
    /// </summary>
    internal static class MenuOption
    {
        private const string Label = "Play Split-Screen";
        public static Button SplitButton;

        public static void Apply(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(MainMenu), "Start"),
                postfix: new HarmonyMethod(typeof(MenuOption).GetMethod(nameof(MenuStartPostfix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }

        private static void MenuStartPostfix(MainMenu __instance)
        {
            try { Build(__instance); }
            catch (System.Exception e) { Plugin.Log.LogError("Menu button: " + e); }
        }

        private static void Build(MainMenu menu)
        {
            if (!Plugin.Enabled.Value) return;
            Button solo = menu.playSoloButton;
            if (solo == null || SplitButton != null) return;

            // The original solo button now always means one player.
            solo.onClick.AddListener(() => Plugin.TwoPlayers.Value = false);

            Transform parent = solo.transform.parent;
            Button split = Object.Instantiate(solo, parent);
            split.name = "PlaySplitScreenButton";
            split.transform.SetSiblingIndex(solo.transform.GetSiblingIndex() + 1);

            // Replace the localized label with ours, keeping the game's font and casing.
            foreach (LocalizedText lt in split.GetComponentsInChildren<LocalizedText>(true))
                Object.DestroyImmediate(lt);
            TMP_Text soloText = solo.GetComponentInChildren<TMP_Text>(true);
            TMP_Text text = split.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                string src = soloText != null ? soloText.text : "";
                bool upper = src.Length > 0 && src == src.ToUpperInvariant();
                bool lower = src.Length > 0 && src == src.ToLowerInvariant();
                text.text = upper ? Label.ToUpperInvariant() : lower ? Label.ToLowerInvariant() : Label;
            }

            split.onClick.RemoveAllListeners();
            split.onClick.AddListener(() =>
            {
                Plugin.TwoPlayers.Value = true;
                menu.PlaySoloClicked();
            });

            // Slot the new button into controller / keyboard navigation between solo and
            // whatever used to come after it.
            Navigation soloNav = solo.navigation;
            if (soloNav.mode == Navigation.Mode.Explicit)
            {
                Selectable below = soloNav.selectOnDown;
                Navigation nav = soloNav;
                nav.selectOnUp = solo;
                nav.selectOnDown = below;
                split.navigation = nav;
                soloNav.selectOnDown = split;
                solo.navigation = soloNav;
                if (below != null)
                {
                    Navigation b = below.navigation;
                    if (b.selectOnUp == solo) { b.selectOnUp = split; below.navigation = b; }
                }
            }

            // If the menu does not lay its buttons out automatically, make room by hand.
            if (parent.GetComponent<LayoutGroup>() == null)
            {
                RectTransform sr = (RectTransform)solo.transform;
                RectTransform nr = (RectTransform)split.transform;
                float step = 0f;
                int idx = solo.transform.GetSiblingIndex();
                for (int i = 0; i < parent.childCount; i++)
                {
                    if (i == idx || i == idx + 1) continue;
                    RectTransform o = parent.GetChild(i) as RectTransform;
                    if (o == null || o.GetComponent<Button>() == null) continue;
                    float d = Mathf.Abs(o.anchoredPosition.y - sr.anchoredPosition.y);
                    if (d > 1f && (step == 0f || d < step)) step = d;
                }
                if (step == 0f) step = sr.rect.height * 1.2f;
                // Open the gap partly upward and partly downward so the bottom sign stays
                // on screen, then hang the new sign in the middle of it.
                float up = step * 0.4f, down = step * 0.55f;
                float soloY = sr.anchoredPosition.y;
                for (int i = 0; i < parent.childCount; i++)
                {
                    RectTransform o = parent.GetChild(i) as RectTransform;
                    if (o == null || o == nr || o.GetComponent<Button>() == null) continue;
                    if (o.anchoredPosition.y < soloY - 1f) o.anchoredPosition -= new Vector2(0f, down);
                    else o.anchoredPosition += new Vector2(0f, up);
                }
                float mid = (soloY + up + (soloY - step - down)) * 0.5f;
                nr.anchoredPosition = new Vector2(sr.anchoredPosition.x - 12f, mid);
                nr.localRotation = Quaternion.Euler(0f, 0f, 1.6f);
            }

            // Two hikers on the sign instead of one.
            Transform icons = split.transform.Find("Hinge/Icons");
            if (icons != null && icons.childCount > 0)
            {
                int shown = 0;
                Transform first = null;
                for (int i = 0; i < icons.childCount; i++)
                {
                    Transform c = icons.GetChild(i);
                    if (!c.gameObject.activeSelf) continue;
                    shown++;
                    if (first == null) first = c;
                }
                if (first == null) first = icons.GetChild(0);
                for (int i = 0; i < icons.childCount && shown < 2; i++)
                {
                    Transform c = icons.GetChild(i);
                    if (c.gameObject.activeSelf) continue;
                    c.gameObject.SetActive(true);
                    shown++;
                }
                while (shown < 2)
                {
                    Transform c = Object.Instantiate(first, icons);
                    c.gameObject.SetActive(true);
                    shown++;
                }
                // Make room for the second hiker: a slightly longer sign.
                RectTransform br = (RectTransform)split.transform;
                br.sizeDelta += new Vector2(50f, 0f);
                if (text != null) text.rectTransform.offsetMax -= new Vector2(42f, 0f);
                LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)icons);
            }

            SplitButton = split;
            Plugin.Log.LogInfo($"Menu: added split-screen button under '{solo.name}' " +
                $"(layout={(parent.GetComponent<LayoutGroup>() != null ? parent.GetComponent<LayoutGroup>().GetType().Name : "none")}, " +
                $"nav={soloNav.mode}, label='{(text != null ? text.text : "?")}')");
        }
    }
}
