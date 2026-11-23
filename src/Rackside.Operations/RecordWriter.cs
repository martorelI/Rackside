using System.Globalization;
using Rackside.Core;
using Rackside.Core.Localization;
using Rackside.Records;

namespace Rackside.Operations;

/// <summary>Writes the HTML record of an operation; on failure, the reduced one (always a printable page).</summary>
internal static class RecordWriter
{
    public static string Duration(DateTime start, DateTime end)
    {
        var d = end - start;
        return Ci.F("{0:00}:{1:00}:{2:00}", (int)Math.Floor(d.TotalHours), d.Minutes, d.Seconds);
    }

    public static string Stamp(DateTime d) => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Report_X_host_yyyy-MM-dd_HHmm.html without characters Windows does not allow.</summary>
    public static string FileName(string kind, string host, DateTime start) =>
        Ci.Replace(Ci.F("Report_{0}_{1}_{2}.html", kind, host, start.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)), "[\\\\/:*?\"<>|]", "_");

    /// <summary>Tries the full record; returns the path or empty if it could not be written.</summary>
    public static string Write(string path, Func<string> build, OperationLog log, Translator t, string okMessage = "Record: {0}", string failMessage = "Could not generate the record: {0}")
    {
        try
        {
            HtmlRecords.Write(path, build());
            log.Ok(Ci.F(t.T(okMessage), path));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or ArgumentException)
        {
            log.Error(Ci.F(t.T(failMessage), ex.Message));
            return string.Empty;
        }
    }
}
