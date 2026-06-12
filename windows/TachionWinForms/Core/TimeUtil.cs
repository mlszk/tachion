using System.IO;
namespace Tachion.Core;

public static class TimeUtil
{
    private static readonly long UnixEpochTicks = DateTime.UnixEpoch.Ticks;

    public static long FileMtimeNs(string path)
    {
        var utc = File.GetLastWriteTimeUtc(path);
        return Math.Max(0, (utc.Ticks - UnixEpochTicks) * 100L);
    }

    public static DateTime NsToUtc(long ns)
    {
        return new DateTime(UnixEpochTicks + ns / 100L, DateTimeKind.Utc);
    }

    public static void SetMtimeNs(string path, long ns)
    {
        File.SetLastWriteTimeUtc(path, NsToUtc(ns));
    }
}
