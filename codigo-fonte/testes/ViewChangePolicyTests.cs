using System;
using PastaGBIcons;

internal static class ViewChangePolicyTests
{
    private static int checks;

    private static void Expect(string scenario, ViewChangeKind expected,
        uint eventType, int objectId, int childId, ViewEventRole role)
    {
        ViewChangeKind actual = ViewChangePolicy.Classify(eventType, objectId, childId, role);
        if (actual != expected)
            throw new Exception(scenario + ": expected " + expected + ", got " + actual);
        checks++;
    }

    private static void Assert(bool condition, string scenario)
    {
        if (!condition) throw new Exception(scenario);
        checks++;
    }

    public static int Main()
    {
        try
        {
            Assert(ViewChangeKind.None < ViewChangeKind.Refresh &&
                ViewChangeKind.Refresh < ViewChangeKind.Geometry &&
                ViewChangeKind.Geometry < ViewChangeKind.Structure,
                "Severity permits a caller to retain Refresh frames and invalidate Geometry/Structure");

            uint[] normalSelection = { 0x8005, 0x8006, 0x8007, 0x8008, 0x8009, 0x800A, 0x8014 };
            foreach (uint eventType in normalSelection)
                Expect("Normal selection/focus/state never invalidates placement", ViewChangeKind.Refresh,
                    eventType, -4, 7, ViewEventRole.View);

            uint[] caretEvents = { 0x8000, 0x8001, 0x8002, 0x8003, 0x800B };
            foreach (uint eventType in caretEvents)
            {
                Expect("Caret blinking cannot hide the view", ViewChangeKind.None,
                    eventType, -8, 0, ViewEventRole.ContentChild);
                Expect("Cursor changes cannot hide the view", ViewChangeKind.None,
                    eventType, -9, 0, ViewEventRole.Root);
            }

            Expect("Address edit typing is unrelated chrome", ViewChangeKind.None,
                0x800E, -4, 0, ViewEventRole.Other);
            Expect("Progress bar NAME cannot become folder navigation", ViewChangeKind.None,
                0x800C, -4, 0, ViewEventRole.Other);
            Expect("Progress bar VALUE cannot become scrolling", ViewChangeKind.None,
                0x800E, -4, 0, ViewEventRole.Other);
            Expect("Navigation tree selection is unrelated to current item placement", ViewChangeKind.None,
                0x8006, -4, 15, ViewEventRole.Other);
            Expect("A root client child's status name is not a new folder", ViewChangeKind.Refresh,
                0x800C, -4, 3, ViewEventRole.Root);
            Expect("A root client name is not the actual window title", ViewChangeKind.Refresh,
                0x800C, -4, 0, ViewEventRole.Root);
            Expect("Ancestor progress names do not invalidate content", ViewChangeKind.None,
                0x800C, -4, 1, ViewEventRole.ViewAncestor);

            Expect("Actual root title signals possible navigation", ViewChangeKind.Structure,
                0x800C, 0, 0, ViewEventRole.Root);
            Expect("Accessible title bar name signals possible navigation", ViewChangeKind.Structure,
                0x800C, -2, 0, ViewEventRole.Root);
            Expect("Folder item rename invalidates name mapping", ViewChangeKind.Structure,
                0x800C, -4, 4, ViewEventRole.View);
            Expect("Rename editor name invalidates mapping", ViewChangeKind.Structure,
                0x800C, -4, 0, ViewEventRole.ContentChild);
            Expect("Typing in an existing rename editor requests a read", ViewChangeKind.Refresh,
                0x800E, -4, 0, ViewEventRole.ContentChild);

            Expect("Scrolling the content invalidates placement", ViewChangeKind.Geometry,
                0x8015, -4, 0, ViewEventRole.View);
            Expect("Content native scrollbar value invalidates placement", ViewChangeKind.Geometry,
                0x800E, -5, 0, ViewEventRole.View);
            Expect("Horizontal scrollbar position invalidates placement", ViewChangeKind.Geometry,
                0x800E, -6, 0, ViewEventRole.ContentChild);
            Expect("Scrolling the navigation tree is unrelated", ViewChangeKind.None,
                0x0012, -5, 0, ViewEventRole.Other);
            Expect("Content scroll gesture invalidates placement", ViewChangeKind.Geometry,
                0x0012, -5, 0, ViewEventRole.View);
            Expect("Content scroll end requests current placement", ViewChangeKind.Geometry,
                0x0013, -6, 0, ViewEventRole.View);
            Expect("A root value update is not scrolling", ViewChangeKind.None,
                0x800E, -4, 0, ViewEventRole.Root);

            Expect("Window movement invalidates physical coordinates", ViewChangeKind.Geometry,
                0x800B, 0, 0, ViewEventRole.Root);
            Expect("Pane movement invalidates physical coordinates", ViewChangeKind.Geometry,
                0x800B, 0, 0, ViewEventRole.ViewAncestor);
            Expect("One item moves within the content", ViewChangeKind.Geometry,
                0x800B, -4, 8, ViewEventRole.View);
            Expect("A root client's unrelated child moves", ViewChangeKind.None,
                0x800B, -4, 8, ViewEventRole.Root);
            Expect("Resize starts", ViewChangeKind.Geometry,
                0x000A, 0, 0, ViewEventRole.Root);
            Expect("Resize ends", ViewChangeKind.Geometry,
                0x000B, 0, 0, ViewEventRole.Root);
            Expect("Minimization invalidates visibility", ViewChangeKind.Geometry,
                0x0016, 0, 0, ViewEventRole.Root);
            Expect("Restoration needs current geometry", ViewChangeKind.Geometry,
                0x0017, 0, 0, ViewEventRole.Root);

            uint[] structural = { 0x8000, 0x8001, 0x8004, 0x800F };
            foreach (uint eventType in structural)
                Expect("Content lifecycle invalidates an incremental name index", ViewChangeKind.Structure,
                    eventType, -4, 2, ViewEventRole.View);
            Expect("Root HWND destruction invalidates identity", ViewChangeKind.Structure,
                0x8001, 0, 0, ViewEventRole.Root);
            Expect("An unrelated root client object is destroyed", ViewChangeKind.None,
                0x8001, -4, 9, ViewEventRole.Root);
            Expect("Native child HWND Z-order does not invalidate placement", ViewChangeKind.Refresh,
                0x8004, 0, 0, ViewEventRole.ViewAncestor);
            Expect("An overlay HWND reordered under the view cannot create a hide/show loop", ViewChangeKind.Refresh,
                0x8004, 0, 0, ViewEventRole.View);
            Expect("Root child HWND Z-order does not invalidate file identities", ViewChangeKind.Refresh,
                0x8004, 0, 0, ViewEventRole.Root);
            Expect("Reordering real accessible file items still invalidates the index", ViewChangeKind.Structure,
                0x8004, -4, 0, ViewEventRole.View);
            Expect("Hiding the view removes its placement", ViewChangeKind.Geometry,
                0x8003, 0, 0, ViewEventRole.View);
            Expect("Showing rename input removes previous placement", ViewChangeKind.Geometry,
                0x8002, -4, 0, ViewEventRole.ContentChild);

            Expect("Switching foreground alone preserves geometry", ViewChangeKind.Refresh,
                0x0003, 0, 0, ViewEventRole.Root);
            Expect("Description updates do not hide content", ViewChangeKind.None,
                0x800D, -4, 1, ViewEventRole.View);
            Expect("Opening a menu does not invalidate the item list", ViewChangeKind.None,
                0x8002, -3, 0, ViewEventRole.Root);
            Expect("Unknown event IDs are not assumed to be navigation", ViewChangeKind.None,
                0x9999, -4, 0, ViewEventRole.View);
            Expect("An unrecognized role is not trusted as content", ViewChangeKind.None,
                0x800C, -4, 0, (ViewEventRole)99);

            Console.WriteLine("PASS: " + checks + " checks against production ViewChangePolicy.cs (pure logic; no Windows UI execution).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after " + checks + " checks: " + exception.Message);
            return 1;
        }
    }
}
