using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace FlatBlack.Tests;

internal static class GameplaySmoke
{
    private enum ScreenKind { Work, Health, Research, Inventory, Menu }
    private const int FramesPerScreen = 20;
    private static readonly ScreenKind[] Screens = Enum.GetValues<ScreenKind>();
    private static Pawn? colonist;
    private static int frames;

    internal static void Install()
    {
        Prefs.VolumeMaster = 0;
        new Harmony("FlatBlack.Tests.Gameplay").Patch(AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI)),
            postfix: new HarmonyMethod(typeof(GameplaySmoke), nameof(DrawFrame)));
    }

    private static void DrawFrame()
    {
        if (Event.current.type != EventType.Repaint || Current.ProgramState != ProgramState.Playing || Find.CurrentMap is null)
            return;
        try
        {
            frames++;
            if (frames == 1)
                PrepareColony();
            foreach (Window log in Find.WindowStack.Windows.OfType<EditWindow_Log>().ToArray())
                Find.WindowStack.TryRemove(log, false);

            int screenIndex = (frames - 1) / FramesPerScreen;
            if (screenIndex >= Screens.Length)
            {
                Log.Message("[Flat Black smoke] PASS rendered gameplay screens");
                Application.Quit();
                return;
            }
            ScreenKind screen = Screens[screenIndex];
            if ((frames - 1) % FramesPerScreen == 0)
                OpenScreen(screen);
            if (frames % FramesPerScreen == 15)
                SmokeMod.Capture($"gameplay-{screen.ToString().ToLowerInvariant()}.png");
        }
        catch (Exception exception)
        {
            Log.Error($"[Flat Black smoke] FAIL gameplay: {exception}");
            Application.Quit();
        }
    }

    private static void PrepareColony()
    {
        SmokeMod.VerifyFonts();
        LoadedModManager.GetMod<FlatBlackMod>().Settings.options.FontPreset = ThemeFontPreset.OxaniumExo2;
        LoadedModManager.GetMod<FlatBlackMod>().ApplyOptions();
        Screen.SetResolution(1280, 900, false);
        Find.TickManager.Pause();
        colonist = Find.CurrentMap.mapPawns.FreeColonistsSpawned.First();
        Hediff injury = HediffMaker.MakeHediff(HediffDefOf.Cut, colonist, colonist.RaceProps.body.corePart);
        injury.Severity = 3f;
        colonist.health.AddHediff(injury);
        SmokeMod.Check(Find.CurrentMap.mapPawns.FreeColonistsSpawned.Count >= 3,
            "isolated quick-test colony has colonists for dense UI review");
    }

    private static void OpenScreen(ScreenKind screen)
    {
        Pawn pawn = colonist ?? throw new InvalidOperationException("The review colony has not been prepared.");
        Find.MainTabsRoot.EscapeCurrentTab(false);
        Find.Selector.ClearSelection();
        Find.Selector.Select(pawn, false, true);
        switch (screen)
        {
            case ScreenKind.Work:
                Find.MainTabsRoot.SetCurrentTab(DefDatabase<MainButtonDef>.GetNamed("Work"), false);
                break;
            case ScreenKind.Health:
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);
                InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Health));
                break;
            case ScreenKind.Research:
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Research, false);
                break;
            case ScreenKind.Inventory:
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Inspect, false);
                InspectPaneUtility.OpenTab(typeof(ITab_Pawn_Gear));
                break;
            case ScreenKind.Menu:
                Thing meal = GenSpawn.Spawn(ThingDefOf.MealSimple, pawn.Position + IntVec3.North, pawn.Map);
                List<FloatMenuOption> options = FloatMenuMakerMap.GetOptions([pawn], meal.DrawPos, out _);
                SmokeMod.Check(options.Count > 0, "native right-click menu generates actions");
                options.Add(new FloatMenuOption("Disabled action", null));
                var menu = new FloatMenu(options) { vanishIfMouseDistant = false };
                Find.WindowStack.Add(menu);
                menu.windowRect.position = new Vector2(520, 180);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(screen));
        }
    }
}
