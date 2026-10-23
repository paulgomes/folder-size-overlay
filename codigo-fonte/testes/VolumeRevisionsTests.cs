using System;
using System.Threading.Tasks;
using PastaGBIcons;

internal static class VolumeRevisionsTests
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
            const uint driveE = 1u << 4;
            const uint driveF = 1u << 5;
            var volumes = new VolumeRevisions();
            long originalE = volumes.Read(@"E:\Fotos");
            long originalF = volumes.Read(@"F:\Fotos");
            Check(originalE == 0 && originalF == 0, "Drive revisions start at zero");
            Check(volumes.Matches(@"E:\Fotos", originalE), "An unchanged volume accepts its own result revision");

            volumes.Changed(driveE);
            Check(volumes.Read(@"E:\Fotos") == 1, "Removing or replacing E increments its revision");
            Check(!volumes.Matches(@"E:\Fotos", originalE), "An old E result is rejected even when the replacement has the same path");
            Check(volumes.Matches(@"F:\Fotos", originalF), "Changing E leaves cached F results valid");
            Check(volumes.Read(@"E:\") == volumes.Read(@"E:\Fotos\2026"), "Revision covers every folder on the affected drive");
            Check(volumes.Read(@"e:\Fotos") == volumes.Read(@"E:\Fotos"), "Drive letters are case insensitive");
            Check(volumes.Read("e:/Fotos") == volumes.Read(@"E:\Fotos"), "Accepted slash notation selects the same drive");
            long removalE = volumes.Read(@"E:\Fotos");

            volumes.Changed(driveE);
            Check(volumes.Read(@"E:\Fotos") == 2, "A second device notification advances the same drive again");
            Check(!volumes.Matches(@"E:\Fotos", removalE), "A scan begun before a later arrival cannot be published for the new media");
            Check(volumes.Matches(@"E:\Fotos", 2), "A scan tied to the latest media revision is accepted");
            Check(volumes.Matches(@"F:\Fotos", originalF), "Repeated E notifications still leave F intact");

            volumes.Changed(driveE | driveF);
            Check(volumes.Read(@"E:\Fotos") == 3 && volumes.Read(@"F:\Fotos") == 1,
                "A broadcast mask independently updates every affected letter");
            Check(!volumes.Matches(@"F:\Fotos", originalF), "F's old result is rejected only after F changes");
            volumes.Changed(0);
            volumes.Changed(0xFC000000u);
            Check(volumes.Read(@"E:\Fotos") == 3 && volumes.Read(@"F:\Fotos") == 1,
                "Empty masks and bits outside A-Z do not alter valid drive revisions");

            Check(VolumeRevisions.DriveIndex(@"A:\") == 0, "A is the first bit in a volume mask");
            Check(VolumeRevisions.DriveIndex(@"z:\Archive") == 25, "Z is the last valid bit, including lower case input");
            Check(VolumeRevisions.DriveIndex(null) == -1 && VolumeRevisions.DriveIndex(String.Empty) == -1,
                "Missing paths do not select a drive");
            Check(VolumeRevisions.DriveIndex("E:") == -1 && VolumeRevisions.DriveIndex("E:relative") == -1,
                "Drive-relative paths are not treated as absolute volume identities");
            Check(VolumeRevisions.DriveIndex(@"1:\folder") == -1, "Only alphabetic Windows drive letters are accepted");
            Check(VolumeRevisions.DriveIndex(@"\\server\share\folder") == -1,
                "A UNC path is not confused with a local drive-letter revision");
            Check(VolumeRevisions.DriveIndex(@"\\?\Volume{example}\folder") == -1,
                "A volume namespace string is not guessed as a drive letter");

            // These revisions are read by background scans while the UI receives
            // device notifications. Concurrent increments must not be lost.
            long before = volumes.Read(@"E:\Fotos");
            long unaffected = volumes.Read(@"F:\Fotos");
            Parallel.For(0, 2000, delegate(int index) { volumes.Changed(driveE); });
            Check(volumes.Read(@"E:\Fotos") == before + 2000, "Concurrent device revision updates are not lost");
            Check(volumes.Matches(@"F:\Fotos", unaffected), "Concurrent updates on E still do not invalidate F");

            Console.WriteLine("PASS: " + checks + " checks against production VolumeRevisions.cs (pure logic; no device events executed).");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL after " + checks + " checks: " + exception.Message);
            return 1;
        }
    }
}
