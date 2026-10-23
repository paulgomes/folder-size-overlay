using System;
using PastaGBIcons;

internal static class FolderResolutionStateTests
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        checks++;
    }

    public static int Main()
    {
        try
        {
            DateTime start = new DateTime(2026, 10, 19, 17, 0, 0, DateTimeKind.Utc);
            var temporary = new FolderResolutionState();
            Check(temporary.TryBegin(start), "A newly visible folder can be resolved immediately");
            // Simulate a failed Shell path query: no successful result is accepted.
            Check(!temporary.Resolved && temporary.Path == null,
                "A temporary path failure is not permanently cached as a non-folder");
            Check(!temporary.TryBegin(start.AddMilliseconds(300)),
                "The next visual capture does not immediately hammer the unavailable item");
            Check(!temporary.TryBegin(start.AddMilliseconds(999)),
                "A failed item waits the full one-second interval");

            var healthy = new FolderResolutionState();
            Check(healthy.TryBegin(start.AddMilliseconds(300)),
                "An unavailable item does not prevent another folder from resolving");
            healthy.AcceptFolder(@"E:\Fotos");
            Check(healthy.Resolved && healthy.Path == @"E:\Fotos",
                "A healthy folder is available while the other item is waiting");

            Check(temporary.TryBegin(start.AddSeconds(1)),
                "The unavailable folder is retried at one second without rebuilding the index");
            temporary.AcceptFolder(@"E:\Projetos");
            Check(temporary.Resolved && temporary.Path == @"E:\Projetos",
                "A failure followed by a successful path query recovers the folder");
            Check(!temporary.TryBegin(start.AddSeconds(30)),
                "A confirmed path is reused for the lifetime of this index");

            var confirmedFile = new FolderResolutionState();
            Check(confirmedFile.TryBegin(start), "A candidate starts unclassified");
            // Production calls this only after a successful attributes query.
            confirmedFile.ExcludeNonFolder();
            Check(confirmedFile.Resolved && confirmedFile.Path == null &&
                !confirmedFile.TryBegin(start.AddSeconds(2)),
                "Proven non-folder attributes can suppress further work on that item");

            var repeatedFailure = new FolderResolutionState();
            Check(repeatedFailure.TryBegin(start) && repeatedFailure.TryBegin(start.AddSeconds(1)),
                "A second failed query remains retryable");
            Check(!repeatedFailure.TryBegin(start.AddMilliseconds(1500)) &&
                repeatedFailure.TryBegin(start.AddSeconds(2)),
                "Each retry preserves the minimum interval without permanent negative caching");
            Check(!repeatedFailure.Resolved && repeatedFailure.Path == null,
                "Repeated failures do not invent a path or a confirmed exclusion");

            Console.WriteLine("PASS: " + checks + " checks against production FolderResolutionState.cs (pure logic; no Shell calls executed).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after " + checks + " checks: " + exception.Message);
            return 1;
        }
    }
}
