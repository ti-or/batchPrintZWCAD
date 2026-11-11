using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.PlottingServices;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;

/**
 * @file PlotterService.Media.cs（ZWCAD）
 * @description 介质名选择、缓存与纸张尺寸校验。
 *
 * 主要功能：
 * - SelectMedia / SelectMediaFromNames：按尺寸与名称选介质
 * - FindRasterMediaByAspectRatio：栅格设备按窗口长宽比兜底
 * - GetMediaNames 缓存：避免重复 RefreshLists
 *
 * 核心代码：
 * - FindByPhysicalSize / EnsureExactMediaSize：毫米容差匹配与写回校验
 * - IsRasterPlotDevice：PNG/JPG 等栅格驱动分支入口
 *
 * 注意：单介质无刷新快捷路径（TrySelectExactSingleMediaWithoutRefresh）仅用于性能优化。
 */

namespace ZwcadBatchPlot;

public static partial class PlotterService
{
    /** 纸质打印按打印机实际纸型匹配的容差（毫米）；驱动上报的纸型尺寸可能有小数差。 */
    private const double PrinterMediaToleranceMm = 3d;

    /** 纸质打印改纸告警去重：设备|图幅，避免同一批每张都读设置文件写日志。 */
    private static readonly object PrinterPaperFallbackGate = new();
    private static readonly HashSet<string> PrinterPaperFallbackWarned = new(StringComparer.OrdinalIgnoreCase);

    /** SelectMedia：获取介质列表并选择匹配项（含栅格长宽比兜底）。 */
    private static MediaSelection? SelectMedia(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        bool modelType,
        Extents2d rasterWindow)
    {
        // 纸质打印：纸张来自打印机驱动，必须按驱动上报的物理尺寸选纸，不能只看介质名。
        if (job.PrintToDevice)
        {
            return SelectPrinterMedia(validator, plotSettings, job, settings, deviceName);
        }

        var media = GetMediaNames(validator, plotSettings, deviceName, modelType);
        var windowWidth = Math.Abs(rasterWindow.MaxPoint.X - rasterWindow.MinPoint.X);
        var windowHeight = Math.Abs(rasterWindow.MaxPoint.Y - rasterWindow.MinPoint.Y);
        return SelectMediaFromNames(media, job, settings, deviceName, windowWidth, windowHeight);
    }

    /** SelectMediaFromNames：在给定介质名列表中按尺寸/名称选择。 */
    private static MediaSelection? SelectMediaFromNames(
        IReadOnlyList<string> media,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        double rasterWindowWidth = 0d,
        double rasterWindowHeight = 0d)
    {
        if (media.Count == 0)
        {
            return null;
        }

        if (IsRasterPlotDevice(deviceName))
        {
            RasterPlotOrientation.GetDcsOrientedPaperSize(
                job, rasterWindowWidth, rasterWindowHeight, out var rasterWidth, out var rasterHeight);
            return FindRasterMediaByAspectRatio(media, rasterWidth, rasterHeight);
        }

        var tolerance = job.RequireExactPaperSize
            ? ExactMediaToleranceMm
            : settings.PaperMatchToleranceMm;
        // 扩大纸张留白模式：按有效尺寸（含留白）选纸
        var searchWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var searchHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var exact = FindByPhysicalSize(media, searchWidth, searchHeight, tolerance);
        if (exact != null)
        {
            return exact;
        }

        if (job.RequireExactPaperSize)
            return null;

        // 名称兜底是标准纸张的固定兼容策略，不再作为用户设置；精确任意纸张已在上方直接返回，仍禁止兜底。
        var paperName = job.PaperName ?? "";
        var plusIndex = paperName.IndexOf('+');
        var basePaper = plusIndex > 0 ? paperName.Substring(0, plusIndex) : paperName;
        if (plusIndex > 0)
        {
            var longNamed = media.FirstOrDefault(x => x.IndexOf(paperName, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0
                    && x.IndexOf("加长", StringComparison.OrdinalIgnoreCase) >= 0);
            if (longNamed != null)
            {
                return new MediaSelection { Name = longNamed, NeedsRotation = false };
            }
        }
        else
        {
            var named = media.FirstOrDefault(x => x.IndexOf(basePaper, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? media.FirstOrDefault(x => x.IndexOf(basePaper.Replace("A", "ISO_A"), StringComparison.OrdinalIgnoreCase) >= 0);
            if (named != null)
            {
                return new MediaSelection { Name = named, NeedsRotation = false };
            }
        }

        return null;
    }

    /** PrinterMedia：打印机纸型：名称与毫米尺寸。 */
    private sealed class PrinterMedia
    {
        public string Name { get; set; } = "";
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
    }

    /**
     * SelectPrinterMedia：纸质打印按打印机上报的纸型尺寸选纸。
     * 逐个介质读 PlotPaperSize（统一换算为毫米），图幅精确命中直接用；
     * 命中不了时按「纸张不匹配时改用最接近的纸张」开关决定报错停止或改纸。
     */
    private static MediaSelection? SelectPrinterMedia(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName)
    {
        var names = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        if (names.Count == 0)
        {
            throw new InvalidOperationException($"打印机没有可用纸张: {deviceName}");
        }

        var targetWidth = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm
            : job.PaperWidthMm > 0 ? job.PaperWidthMm : Math.Abs(job.MaxX - job.MinX);
        var targetHeight = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm
            : job.PaperHeightMm > 0 ? job.PaperHeightMm : Math.Abs(job.MaxY - job.MinY);
        if (targetWidth <= 0d || targetHeight <= 0d)
        {
            // 没有图幅信息时退回按介质名匹配，纸张由驱动的默认纸型决定。
            return SelectMediaFromNames(names, job, settings, deviceName);
        }

        var measured = new List<PrinterMedia>();
        foreach (var name in names)
        {
            var size = TryMeasurePrinterMedia(validator, plotSettings, name);
            if (size != null)
            {
                measured.Add(new PrinterMedia { Name = name, WidthMm = size.Value.WidthMm, HeightMm = size.Value.HeightMm });
            }
        }

        if (measured.Count == 0)
        {
            return SelectMediaFromNames(names, job, settings, deviceName);
        }

        var tolerance = Math.Max(settings.PaperMatchToleranceMm, PrinterMediaToleranceMm);
        PrinterMedia? best = null;
        var bestError = double.MaxValue;
        var bestNeedsRotation = false;
        foreach (var item in measured)
        {
            var direct = DirectSizeError(item.WidthMm, item.HeightMm, targetWidth, targetHeight);
            var rotated = DirectSizeError(item.WidthMm, item.HeightMm, targetHeight, targetWidth);
            var error = Math.Min(direct, rotated);
            if (error >= bestError)
            {
                continue;
            }

            best = item;
            bestError = error;
            bestNeedsRotation = rotated < direct;
        }

        if (best != null && bestError <= tolerance)
        {
            return new MediaSelection { Name = best.Name, NeedsRotation = bestNeedsRotation };
        }

        var available = measured
            .OrderBy(item => Math.Min(
                DirectSizeError(item.WidthMm, item.HeightMm, targetWidth, targetHeight),
                DirectSizeError(item.WidthMm, item.HeightMm, targetHeight, targetWidth)))
            .Take(8)
            .Select(item => $"{item.Name}({item.WidthMm:0.##}×{item.HeightMm:0.##}mm)")
            .ToList();

        if (!job.AllowClosestPaper)
        {
            throw new InvalidOperationException(
                PaperOutput.BuildPaperMissingMessage(deviceName, targetWidth, targetHeight, available));
        }

        var fallback = PickPrinterFallbackMedia(measured, targetWidth, targetHeight)
            ?? throw new InvalidOperationException(
                PaperOutput.BuildPaperMissingMessage(deviceName, targetWidth, targetHeight, available));
        WarnPrinterPaperFallback(deviceName, targetWidth, targetHeight, fallback);
        return new MediaSelection { Name = fallback.Name, NeedsRotation = fallback.NeedsRotation };
    }

    /** TryMeasurePrinterMedia：读取单个介质在打印机上的物理尺寸并换算成毫米。 */
    private static (double WidthMm, double HeightMm)? TryMeasurePrinterMedia(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        string mediaName)
    {
        try
        {
            validator.SetCanonicalMediaName(plotSettings, mediaName);
            var size = plotSettings.PlotPaperSize;
            if (size.X <= 0d || size.Y <= 0d)
            {
                return null;
            }

            // 打印纸张单位在部分宿主上不可读；英寸驱动的纸型数值都很小（A4 约 8.27×11.69），
            // 两个方向都小于 60 时按英寸换算，其余按毫米处理。
            var factor = size.X < 60d && size.Y < 60d ? 25.4d : 1d;
            return (size.X * factor, size.Y * factor);
        }
        catch
        {
            return null;
        }
    }

    /**
     * PickPrinterFallbackMedia：按「长宽比偏差 + 尺寸偏差」选最接近图幅的纸张。
     * 纸质打印默认按纸张布满（ScaleToFit），所以不能挑「能放下但大很多」的纸，
     * 否则一张 A3 会被放大铺满 A0 或整卷纸。
     */
    private static (string Name, bool NeedsRotation)? PickPrinterFallbackMedia(
        IReadOnlyList<PrinterMedia> measured,
        double targetWidth,
        double targetHeight)
    {
        if (measured.Count == 0 || targetWidth <= 0d || targetHeight <= 0d)
        {
            return null;
        }

        var chosen = measured
            .OrderBy(item => MediaShapeScore(item.WidthMm, item.HeightMm, targetWidth, targetHeight))
            .First();
        var needsRotation = (chosen.WidthMm >= chosen.HeightMm) != (targetWidth >= targetHeight);
        return (chosen.Name, needsRotation);
    }

    /** MediaShapeScore：长宽比偏差与对角线尺寸偏差（取对数，无量纲），越小越接近目标图幅。 */
    private static double MediaShapeScore(double width, double height, double targetWidth, double targetHeight)
    {
        var mediaAspect = Math.Max(width, height) / Math.Min(width, height);
        var targetAspect = Math.Max(targetWidth, targetHeight) / Math.Min(targetWidth, targetHeight);
        var aspectScore = Math.Abs(Math.Log(mediaAspect / targetAspect));
        var mediaDiagonal = Math.Sqrt(width * width + height * height);
        var targetDiagonal = Math.Sqrt(targetWidth * targetWidth + targetHeight * targetHeight);
        var sizeScore = Math.Abs(Math.Log(mediaDiagonal / targetDiagonal));
        return aspectScore + sizeScore;
    }

    /** WarnPrinterPaperFallback：同一设备同一图幅只记一次改纸告警，避免逐张读设置文件。 */
    private static void WarnPrinterPaperFallback(
        string deviceName,
        double targetWidth,
        double targetHeight,
        (string Name, bool NeedsRotation)? fallback)
    {
        if (fallback == null)
        {
            return;
        }

        var key = $"{deviceName}|{targetWidth:0.##}x{targetHeight:0.##}";
        lock (PrinterPaperFallbackGate)
        {
            if (!PrinterPaperFallbackWarned.Add(key))
            {
                return;
            }
        }

        BatchPlotLogger.AddPending(
            "WARN",
            $"打印机 {deviceName} 缺少 {targetWidth:0.##}×{targetHeight:0.##}mm 纸张，"
            + $"已改用 {fallback.Value.Name} 并按纸张布满打印。");
    }

    /** FindRasterMediaByAspectRatio：栅格设备按窗口长宽比选介质。 */
    private static MediaSelection? FindRasterMediaByAspectRatio(
        IEnumerable<string> mediaNames,
        double targetWidth,
        double targetHeight)
    {
        if (targetWidth <= 0d || targetHeight <= 0d)
        {
            return null;
        }

        var targetAspect = Math.Max(targetWidth, targetHeight) / Math.Min(targetWidth, targetHeight);
        // 原生 PNG/JPG 绘图器的介质通常以像素命名，并不等于 A 系列毫米纸张。
        // 按长宽比选择最接近且分辨率最大的介质，再由 ScaleToFit 保证图框完整输出。
        return mediaNames
            .Select(name => new { Name = name, Size = TryParseMediaSize(name) })
            .Where(item => item.Size != null
                           && item.Size.Value.Width > 0d
                           && item.Size.Value.Height > 0d)
            .Select(item => new
            {
                item.Name,
                Width = item.Size!.Value.Width,
                Height = item.Size.Value.Height,
                AspectError = Math.Abs(Math.Log(
                    (Math.Max(item.Size.Value.Width, item.Size.Value.Height)
                     / Math.Min(item.Size.Value.Width, item.Size.Value.Height)) / targetAspect))
            })
            .OrderBy(item => item.AspectError)
            // 同比例介质优先选择与 DCS 窗口相同的方向；只有设备缺少该方向时才旋转。
            .ThenBy(item => (item.Width >= item.Height) == (targetWidth >= targetHeight) ? 0 : 1)
            .ThenByDescending(item => item.Width * item.Height)
            .Select(item => new MediaSelection
            {
                Name = item.Name,
                NeedsRotation = (item.Width >= item.Height) != (targetWidth >= targetHeight)
            })
            .FirstOrDefault();
    }

    /** IsRasterPlotDevice：是否为栅格出图设备（PNG/JPG 等）。 */
    private static bool IsRasterPlotDevice(string deviceName)
    {
        return deviceName.IndexOf("PNG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPG", StringComparison.OrdinalIgnoreCase) >= 0
               || deviceName.IndexOf("JPEG", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /** TrySelectExactSingleMediaWithoutRefresh：仅一名介质时跳过 RefreshLists 的快捷路径。 */
    private static MediaSelection? TrySelectExactSingleMediaWithoutRefresh(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        PlotJob job,
        AppSettings settings,
        string deviceName,
        bool modelType)
    {
        // 先只重绑设备并读取当前列表；只有新 PMP 尚未可见时，调用方才回退到完整 RefreshLists。
        if (job.CustomPaperWasAdded)
            validator.SetPlotConfigurationName(plotSettings, "None", null);

        validator.SetPlotConfigurationName(plotSettings, deviceName, null);
        TrySetPlotPaperUnits(validator, plotSettings, PlotPaperUnit.Millimeters);
        var names = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        var media = SelectMediaFromNames(names, job, settings, deviceName);
        if (media != null)
            SetCachedMediaNames(deviceName, modelType, names);

        return media;
    }

    /** HasCachedMediaNames：是否已有该设备的介质名缓存。 */
    private static bool HasCachedMediaNames(string deviceName, bool modelType)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            return MediaNameCache.ContainsKey(cacheKey);
        }
    }

    /** SetCachedMediaNames：写入介质名缓存。 */
    private static void SetCachedMediaNames(string deviceName, bool modelType, IReadOnlyList<string> media)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            MediaNameCache[cacheKey] = media;
        }
    }

    /** GetMediaNames：读取介质名列表（带缓存）。 */
    private static IReadOnlyList<string> GetMediaNames(
        PlotSettingsValidator validator,
        PlotSettings plotSettings,
        string deviceName,
        bool modelType)
    {
        var cacheKey = BuildMediaNameCacheKey(deviceName, modelType);
        lock (MediaNameCacheLock)
        {
            if (MediaNameCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var media = validator.GetCanonicalMediaNameList(plotSettings).Cast<string>().ToList();
        lock (MediaNameCacheLock)
        {
            // 仅缓存纸张名称，不缓存任何与当前事务绑定的 ZWCAD 对象。
            MediaNameCache[cacheKey] = media;
        }

        return media;
    }

    /** BuildMediaNameCacheKey：介质名缓存键（设备+模型/布局+指纹）。 */
    private static string BuildMediaNameCacheKey(string deviceName, bool modelType)
    {
        var plottersDirectory = AcadPlotterInstaller.GetPlottersDirectory();
        var devicePath = string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(plottersDirectory, deviceName);
        var pmpPath = string.IsNullOrWhiteSpace(plottersDirectory)
            ? ""
            : Path.Combine(
                plottersDirectory,
                "PMP Files",
                Path.GetFileNameWithoutExtension(deviceName) + ".pmp");
        return string.Join("|", deviceName, modelType ? "M" : "P", GetFileFingerprint(devicePath), GetFileFingerprint(pmpPath));
    }

    /** GetFileFingerprint：PC3 等文件指纹。 */
    private static string GetFileFingerprint(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch
        {
            return "unavailable";
        }
    }

    /** TrySetPlotPaperUnits：尝试设置纸张单位，失败返回 false。 */
    private static bool TrySetPlotPaperUnits(PlotSettingsValidator validator, PlotSettings plotSettings, PlotPaperUnit units)
    {
        try
        {
            validator.SetPlotPaperUnits(plotSettings, units);
            return true;
        }
        catch (ZwSoft.ZwCAD.Runtime.Exception ex) when (ex.ErrorStatus == ZwSoft.ZwCAD.Runtime.ErrorStatus.InvalidInput)
        {
            return false;
        }
    }

    /** FindByPhysicalSize：按毫米宽高与容差匹配介质名。 */
    private static MediaSelection? FindByPhysicalSize(IEnumerable<string> mediaNames, double widthMm, double heightMm, double toleranceMm)
    {
        if (widthMm <= 0 || heightMm <= 0)
        {
            return null;
        }

        var parsed = mediaNames
            .Select(name => new { Name = name, Size = TryParseMediaSize(name) })
            .Where(x => x.Size != null)
            .Select(x => new
            {
                x.Name,
                DirectError = DirectSizeError(x.Size!.Value.Width, x.Size.Value.Height, widthMm, heightMm),
                RotatedError = DirectSizeError(x.Size.Value.Width, x.Size.Value.Height, heightMm, widthMm)
            })
            .ToList();

        var direct = parsed
            .Where(x => x.DirectError <= toleranceMm)
            .OrderBy(x => x.DirectError)
            .Select(x => new MediaSelection { Name = x.Name, NeedsRotation = false })
            .FirstOrDefault();
        if (direct != null)
        {
            return direct;
        }

        return parsed
            .Where(x => x.RotatedError <= toleranceMm)
            .OrderBy(x => x.RotatedError)
            .Select(x => new MediaSelection { Name = x.Name, NeedsRotation = true })
            .FirstOrDefault();
    }

    /** static：从介质名解析宽高。 */
    private static (double Width, double Height)? TryParseMediaSize(string mediaName)
    {
        var match = Regex.Match(mediaName, @"(?<w>\d+(?:\.\d+)?)\s*[xX]\s*(?<h>\d+(?:\.\d+)?)\s*(?<unit>MM|毫米|IN|英寸)?", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var width = double.Parse(match.Groups["w"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var height = double.Parse(match.Groups["h"].Value, System.Globalization.CultureInfo.InvariantCulture);
        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        if (unit is "IN" or "英寸")
        {
            width *= 25.4;
            height *= 25.4;
        }

        return (width, height);
    }

    /** DirectSizeError：尺寸误差（宽高差之和）。 */
    private static double DirectSizeError(double mediaWidth, double mediaHeight, double targetWidth, double targetHeight)
    {
        return Math.Max(Math.Abs(mediaWidth - targetWidth), Math.Abs(mediaHeight - targetHeight));
    }

    /** EnsureExactMediaSize：要求精确纸张时校验当前介质尺寸。 */
    private static void EnsureExactMediaSize(PlotSettings plotSettings, PlotJob job)
    {
        if (!job.RequireExactPaperSize)
            return;

        var size = plotSettings.PlotPaperSize;
        // 扩大纸张留白模式：校验实际加载尺寸应等于有效尺寸（原始+留白×2），而非原始尺寸。
        var expectedW = job.EffectivePaperWidthMm > 0 ? job.EffectivePaperWidthMm : job.PaperWidthMm;
        var expectedH = job.EffectivePaperHeightMm > 0 ? job.EffectivePaperHeightMm : job.PaperHeightMm;
        var direct = DirectSizeError(size.X, size.Y, expectedW, expectedH);
        var rotated = DirectSizeError(size.X, size.Y, expectedH, expectedW);
        if (Math.Min(direct, rotated) <= ExactMediaToleranceMm)
            return;

        throw new InvalidOperationException(
            $"中望 CAD 实际加载纸张 {size.X:0.######} x {size.Y:0.######} mm，"
            + $"与任意纸张 {expectedW:0.######} x {expectedH:0.######} mm 不一致；"
            + "已停止打印，禁止生成错误页幅。");
    }
}
