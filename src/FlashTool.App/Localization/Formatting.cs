using System.IO;
using System.Net.Http;
using FlashTool.Core;
using FlashTool.Core.Containers;
using FlashTool.Core.Devices;
using FlashTool.Core.Imaging;
using FlashTool.Core.Operations;

namespace FlashTool.App.Localization;

/// <summary>Turns values and errors into localized display text.</summary>
public static class Formatting
{
    private static readonly string[] Units = ["Unit_B", "Unit_KB", "Unit_MB", "Unit_GB", "Unit_TB"];

    public static string Size(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "—";
        }

        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        var format = unit == 0 ? "0" : size < 10 ? "0.##" : size < 100 ? "0.#" : "0";
        return $"{size.ToString(format, Loc.Instance.Culture)} {Loc.Get(Units[unit])}";
    }

    public static string Duration(TimeSpan? time)
    {
        if (time is not { } t || t < TimeSpan.Zero || t.TotalDays > 7)
        {
            return "—";
        }

        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Loc.Instance.Culture) : t.ToString(@"m\:ss", Loc.Instance.Culture);
    }

    public static string Phase(OperationPhase phase) => Loc.Get($"Phase_{phase}");

    public static string DiskKind(DiskKind kind) => Loc.Get($"Devices_Kind_{kind}");

    public static string ImageKind(ImageKind kind) => Loc.Get($"Image_Kind_{kind}");

    public static string ContainerFormat(ContainerFormat format) => Loc.Get($"Format_{format}");

    public static string Compression(CompressionPreset preset) => Loc.Get($"Compression_{preset}");

    public static string Progress(OperationProgress p)
    {
        var speed = Size((long)p.BytesPerSecond);
        return p.BytesTotal is > 0
            ? Loc.Format("Progress_Details", Size(p.BytesProcessed), Size(p.BytesTotal), speed, Duration(p.Remaining))
            : Loc.Format("Progress_DetailsNoTotal", Size(p.BytesProcessed), speed);
    }

    /// <summary>Localized, user-facing description of an exception.</summary>
    public static string Error(Exception ex)
    {
        switch (ex)
        {
            case VerificationFailedException v:
                return Loc.Format("Error_VerificationFailed", $"{v.Offset:N0} ({Size(v.Offset)})");

            case FlashToolException { Code: FlashErrorCode.DeviceEjectFailed, Arguments: [EjectFailure reason, var detail, ..] }:
                return Loc.Format("Error_DeviceEjectFailed", Loc.Format($"Eject_{reason}", detail));

            case FlashToolException f:
                var key = $"Error_{f.Code}";
                var text = Loc.Get(key);
                if (text.StartsWith('['))
                {
                    return Loc.Format("Error_Unknown", f.Message);
                }

                var args = f.Code == FlashErrorCode.DeviceIoError ? [f.Message] : f.Arguments.ToArray();
                return args.Length > 0 ? string.Format(Loc.Instance.Culture, text, args) : text;

            case OperationCanceledException:
                return Loc.Get("Cancelled");

            case UnauthorizedAccessException:
                return Loc.Get("Error_DeviceAccessDenied");

            case FileNotFoundException fnf:
                return Loc.Format("Error_FileNotFound", fnf.FileName ?? fnf.Message);

            case HttpRequestException http:
                return Loc.Format("Error_Network", http.Message);

            case IOException io:
                return Loc.Format("Error_IoError", io.Message);

            case AggregateException { InnerExceptions.Count: 1 } agg:
                return Error(agg.InnerExceptions[0]);

            default:
                return Loc.Format("Error_Unknown", ex.Message);
        }
    }
}
