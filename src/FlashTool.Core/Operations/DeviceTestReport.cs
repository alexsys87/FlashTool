using System.Globalization;
using System.Text;

namespace FlashTool.Core.Operations;

/// <summary>
/// The result of a device test as CSV: a commented summary, then one table per kind of data
/// (chart points, passes, random access, bad areas), each with its own header row. Speeds are in
/// bytes per second and times in milliseconds, numbers in invariant format.
/// </summary>
public static class DeviceTestReport
{
    public static string ToCsv(DeviceTestResult result, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(result);
        var csv = new StringBuilder();
        Line(csv, "# FlashTool device test");
        Line(csv, "# device", deviceName);
        Line(csv, "# mode", result.Mode.ToString());
        Line(csv, "# size_bytes", Number(result.DeviceLength));
        Line(csv, "# verdict", result.Verdict.ToString());
        Line(csv, "# real_capacity_bytes", result.RealCapacity is { } real ? Number(real) : string.Empty);
        Line(csv, "# addresses_wrap", result.AddressesWrap ? "true" : "false");
        Line(csv, "# bad_areas", Number(result.BadAreaCount));
        Line(csv, "# bad_bytes", Number(result.BadBytes));
        Line(csv, "# elapsed_seconds", Number(result.Elapsed.TotalSeconds));

        if (result.Samples.Count > 0)
        {
            csv.Append('\n');
            Line(csv, "phase", "offset_bytes", "length_bytes", "bytes_per_second", "max_latency_ms");
            foreach (var s in result.Samples)
            {
                Line(csv, Phase(s.Phase), Number(s.Offset), Number(s.Length), Number(s.BytesPerSecond), Number(s.MaxLatencyMs));
            }
        }

        if (result.Passes.Count > 0)
        {
            csv.Append('\n');
            Line(csv, "pass", "bytes", "average_bytes_per_second", "min_bytes_per_second", "max_bytes_per_second", "median_latency_ms", "max_latency_ms");
            foreach (var p in result.Passes)
            {
                Line(csv, Phase(p.Phase), Number(p.Bytes), Number(p.AverageBytesPerSecond), Number(p.MinBytesPerSecond), Number(p.MaxBytesPerSecond), Number(p.MedianLatencyMs), Number(p.MaxLatencyMs));
            }
        }

        if (result.RandomAccess.Count > 0)
        {
            csv.Append('\n');
            Line(csv, "random_4k", "operations", "iops", "bytes_per_second", "average_latency_ms", "p99_latency_ms", "errors");
            foreach (var r in result.RandomAccess)
            {
                Line(csv, Phase(r.Phase), Number(r.Operations), Number(r.Iops), Number(r.BytesPerSecond), Number(r.AverageLatencyMs), Number(r.P99LatencyMs), Number(r.Errors));
            }
        }

        if (result.SlowAreas.Count > 0)
        {
            csv.Append('\n');
            Line(csv, "slow_area", "offset_bytes", "length_bytes");
            foreach (var a in result.SlowAreas)
            {
                Line(csv, Phase(a.Phase), Number(a.Offset), Number(a.Length));
            }
        }

        if (result.BadAreas.Count > 0)
        {
            csv.Append('\n');
            Line(csv, "bad_area", "offset_bytes", "length_bytes");
            foreach (var a in result.BadAreas)
            {
                Line(csv, a.Kind switch
                {
                    DeviceBadAreaKind.ReadError => "read_error",
                    DeviceBadAreaKind.WriteError => "write_error",
                    _ => "wrong_data",
                }, Number(a.Offset), Number(a.Length));
            }
        }

        return csv.ToString();
    }

    private static string Phase(DeviceTestPhase phase) => phase == DeviceTestPhase.Read ? "read" : "write";

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void Line(StringBuilder csv, params string[] fields)
    {
        csv.AppendJoin(',', fields.Select(Escape));
        csv.Append('\n');
    }

    private static string Escape(string field)
        => field.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
}
