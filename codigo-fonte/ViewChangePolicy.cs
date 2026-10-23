namespace PastaGBIcons
{
    // Ordered by how much previously captured information becomes unusable.
    // Refresh requests another read without hiding a valid frame. Geometry
    // invalidates placement; Structure also invalidates the Shell item index.
    internal enum ViewChangeKind
    {
        None = 0,
        Refresh = 1,
        Geometry = 2,
        Structure = 3
    }

    internal enum ViewEventRole
    {
        Other = 0,
        Root = 1,
        View = 2,
        ContentChild = 3,
        ViewAncestor = 4
    }

    // Pure policy: native HWND ancestry and known chrome must be classified by
    // the caller. In particular, address/search edits, toolbars and progress
    // bars are Other, not ContentChild merely because they share an Explorer
    // root. No accessibility call or filesystem operation belongs here.
    internal static class ViewChangePolicy
    {
        private const int ObjectWindow = 0;
        private const int ObjectSystemMenu = -1;
        private const int ObjectTitleBar = -2;
        private const int ObjectMenu = -3;
        private const int ObjectClient = -4;
        private const int ObjectVerticalScroll = -5;
        private const int ObjectHorizontalScroll = -6;
        private const int ObjectCaret = -8;
        private const int ObjectCursor = -9;
        private const int ObjectAlert = -10;
        private const int ObjectSound = -11;

        internal static ViewChangeKind Classify(uint eventType, int objectId,
            int childId, ViewEventRole role)
        {
            bool root = role == ViewEventRole.Root;
            bool content = role == ViewEventRole.View || role == ViewEventRole.ContentChild;
            bool ancestor = role == ViewEventRole.ViewAncestor;
            if ((!root && !content && !ancestor) ||
                objectId == ObjectCaret || objectId == ObjectCursor ||
                objectId == ObjectSystemMenu || objectId == ObjectMenu ||
                objectId == ObjectAlert || objectId == ObjectSound)
                return ViewChangeKind.None;

            bool windowSelf = objectId == ObjectWindow && childId == 0;
            bool clientSelf = objectId == ObjectClient && childId == 0;
            bool scrollbar = objectId == ObjectVerticalScroll || objectId == ObjectHorizontalScroll;

            switch (eventType)
            {
                case 0x0003: // EVENT_SYSTEM_FOREGROUND
                    return root ? ViewChangeKind.Refresh : ViewChangeKind.None;

                case 0x000A: // EVENT_SYSTEM_MOVESIZESTART
                case 0x000B: // EVENT_SYSTEM_MOVESIZEEND
                case 0x000E: // EVENT_SYSTEM_DRAGDROPSTART
                case 0x000F: // EVENT_SYSTEM_DRAGDROPEND
                case 0x0016: // EVENT_SYSTEM_MINIMIZESTART
                case 0x0017: // EVENT_SYSTEM_MINIMIZEEND
                    return ViewChangeKind.Geometry;

                case 0x0012: // EVENT_SYSTEM_SCROLLINGSTART
                case 0x0013: // EVENT_SYSTEM_SCROLLINGEND
                case 0x8015: // EVENT_OBJECT_CONTENTSCROLLED
                    return content || scrollbar ? ViewChangeKind.Geometry : ViewChangeKind.None;

                case 0x8000: // EVENT_OBJECT_CREATE
                case 0x8001: // EVENT_OBJECT_DESTROY
                case 0x800F: // EVENT_OBJECT_PARENTCHANGE
                    return content || windowSelf ? ViewChangeKind.Structure : ViewChangeKind.None;

                case 0x8002: // EVENT_OBJECT_SHOW
                case 0x8003: // EVENT_OBJECT_HIDE
                case 0x8017: // EVENT_OBJECT_CLOAKED
                case 0x8018: // EVENT_OBJECT_UNCLOAKED
                    return content || windowSelf ? ViewChangeKind.Geometry : ViewChangeKind.None;

                case 0x8004: // EVENT_OBJECT_REORDER
                    // Windows also sends REORDER for native child HWND Z-order.
                    // A caption HWND can cause that notification in its parent;
                    // it does not imply that the Shell's file items changed.
                    if (windowSelf) return ViewChangeKind.Refresh;
                    if (content) return ViewChangeKind.Structure;
                    return clientSelf ? ViewChangeKind.Geometry : ViewChangeKind.None;

                case 0x8005: // EVENT_OBJECT_FOCUS
                case 0x8006: // EVENT_OBJECT_SELECTION
                case 0x8007: // EVENT_OBJECT_SELECTIONADD
                case 0x8008: // EVENT_OBJECT_SELECTIONREMOVE
                case 0x8009: // EVENT_OBJECT_SELECTIONWITHIN
                case 0x800A: // EVENT_OBJECT_STATECHANGE
                case 0x8014: // EVENT_OBJECT_TEXTSELECTIONCHANGED
                    return ViewChangeKind.Refresh;

                case 0x800B: // EVENT_OBJECT_LOCATIONCHANGE
                    return content || windowSelf || clientSelf || scrollbar ?
                        ViewChangeKind.Geometry : ViewChangeKind.None;

                case 0x800C: // EVENT_OBJECT_NAMECHANGE
                    if (content) return ViewChangeKind.Structure;
                    // A root title is a navigation hint. Names of accessible
                    // root-client children may be status/progress text instead.
                    if (root && childId == 0 &&
                        (objectId == ObjectWindow || objectId == ObjectTitleBar))
                        return ViewChangeKind.Structure;
                    return root ? ViewChangeKind.Refresh : ViewChangeKind.None;

                case 0x800E: // EVENT_OBJECT_VALUECHANGE
                    // ValueChange is also emitted by edits and progress bars.
                    // Only an explicitly identified scrollbar proves movement.
                    if (scrollbar) return ViewChangeKind.Geometry;
                    return content ? ViewChangeKind.Refresh : ViewChangeKind.None;

                default:
                    return ViewChangeKind.None;
            }
        }
    }
}
