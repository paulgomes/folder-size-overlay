using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PastaGB;

namespace PastaGBIcons
{
    // Pure result conversion shared by the coordinator and regression harness.
    internal static class SizeResultPolicy
    {
        internal static SizeRecord Create(string path, long[] volumeStamp, ScanResult result,
            DateTime now, bool stale, SizeRecord previous)
        {
            bool wrongRoot = result != null && !String.Equals(PathRelations.Normalize(result.RootPath),
                PathRelations.Normalize(path), StringComparison.OrdinalIgnoreCase);
            bool failed = result == null || wrongRoot || result.TotalBytes < 0 || result.Cancelled ||
                !String.IsNullOrEmpty(result.FatalError);
            var paths = new List<string> { path };
            if (!wrongRoot && result != null && result.DependencyPaths != null)
                paths.AddRange(result.DependencyPaths.Where(x => !String.IsNullOrEmpty(x)).Take(64));
            var record = new SizeRecord { Path = path, VolumeStamp = volumeStamp,
                Dependencies = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), FinishedUtc = now,
                Bytes = failed ? 0 : result.TotalBytes,
                LastResultPartial = !failed && result.IsPartial };
            if (failed)
            {
                record.Phase = SizePhase.Unavailable;
                record.FailureCount = Math.Min(20, previous == null ? 1 : previous.FailureCount + 1);
                record.NextAttemptUtc = now.AddSeconds(SizeRefreshPolicy.RetrySeconds(record.FailureCount));
                record.Detail = (result == null ? "Leitura encerrada sem resultado" : wrongRoot ?
                    "Resultado rejeitado: a pasta de origem mudou" : result.Cancelled ?
                    "Leitura interrompida" : result.TotalBytes < 0 ? "Resultado inválido" : result.FatalError) +
                    "; nova tentativa após " + record.NextAttemptUtc.ToLocalTime().ToString("HH:mm:ss");
            }
            else
            {
                record.Phase = stale ? SizePhase.Stale : result.IsPartial ? SizePhase.Partial : SizePhase.Ready;
                record.NextAttemptUtc = stale ? now.AddSeconds(1) : DateTime.MinValue;
                record.Detail = result.TotalBytes.ToString(CultureInfo.InvariantCulture) + " bytes; " +
                    result.FileCount + " arquivos; " + result.ErrorCount + " falhas; " +
                    result.SkippedLinks + " vínculos ignorados; concluído " + now.ToLocalTime().ToString("HH:mm:ss");
                if (stale) record.Detail += "; alterações pedem nova conferência";
                if (result.Issues != null && result.Issues.Count > 0 && result.Issues[0] != null)
                    record.Detail += "; " + result.Issues[0].Message;
            }
            return record;
        }

        internal static SizeRecord Subtotal(SizeRecord parent, FolderSummary child)
        {
            if (parent == null || !parent.HasValue || parent.NeedsRead || parent.IsReading || child == null ||
                String.IsNullOrEmpty(child.Path) || child.Bytes < 0 ||
                String.Equals(PathRelations.Normalize(parent.Path), PathRelations.Normalize(child.Path),
                    StringComparison.OrdinalIgnoreCase) || !PathRelations.Contains(parent.Path, child.Path)) return null;
            return new SizeRecord { Path = child.Path, Bytes = child.Bytes,
                Phase = child.IsPartial ? SizePhase.Partial : SizePhase.Ready,
                LastResultPartial = child.IsPartial, FinishedUtc = parent.FinishedUtc,
                VolumeStamp = parent.VolumeStamp,
                Dependencies = parent.Dependencies.Length > 1 ? parent.Dependencies : new[] { child.Path },
                Detail = child.Bytes.ToString(CultureInfo.InvariantCulture) + " bytes; " +
                    child.FileCount + " arquivos; subtotal concluído durante leitura de " + parent.Path };
        }
    }
}
