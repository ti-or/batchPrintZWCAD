using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZwcadBatchPlot;

/**
 * @file PaperOutput.cs
 * @description 纸质打印（直接输出到系统打印机/绘图仪）的公共定义与工具。
 *
 * 主要功能：
 * - FormatName：批量打印「输出格式」中的纸质项，IsPaperFormat/IsSystemPrinterDevice 判定
 * - GetSystemPrinters：合并 CAD 设备列表与 Windows 已安装打印机，供界面选择
 * - NormalizeCopies：份数规范化
 *
 * 核心概念：
 * - 纸质打印不生成任何输出文件，由 CAD 打印引擎按设备纸型直接送到打印机
 * - 系统打印机没有 .pc3/.pc5 绘图仪配置，纸张来自打印机驱动
 *
 * 注意：插件自有 LA_pdf/LA_dwf 绘图仪的自定义纸张（PMP）机制不适用于系统打印机，
 *       纸质打印按打印机现有纸型精确匹配，匹配不到时按用户选择报错或改用最接近纸张。
 */
public static class PaperOutput
{
    /// <summary>批量打印「输出格式」中的纸质项文本。</summary>
    public const string FormatName = "纸质";

    /// <summary>允许的最小份数。</summary>
    public const int MinCopies = 1;

    /// <summary>允许的最大份数。</summary>
    public const int MaxCopies = 99;

    /// <summary>输出格是否为纸质打印（兼容旧名「打印机」）。</summary>
    public static bool IsPaperFormat(string? format)
    {
        var value = (format ?? "").Trim();
        return string.Equals(value, FormatName, StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "打印机", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>输出格式是否为需要生成文件的电子格式。</summary>
    public static bool IsFileFormat(string? format)
        => !string.IsNullOrWhiteSpace(format) && !IsPaperFormat(format);

    /// <summary>设备名是否为 CAD 绘图仪配置文件（PC3/PC5/PMP/CTB/STB），这类都不是系统打印机。</summary>
    public static bool IsPlotterConfigFile(string? deviceName)
    {
        var value = (deviceName ?? "").Trim();
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var extension in new[] { ".pc3", ".pc5", ".pmp", ".ctb", ".stb" })
        {
            if (value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>设备名是否为 Windows 系统打印机/绘图仪（不是插件 PC3/PC5，也不是 None）。</summary>
    public static bool IsSystemPrinterDevice(string? deviceName)
    {
        var value = (deviceName ?? "").Trim();
        if (value.Length == 0 || IsPlotterConfigFile(value))
        {
            return false;
        }

        return !string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(value, "无", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 枚举可选打印机：优先取 CAD 设备列表中的系统打印机（PC3/PC5 除外），
    /// 保证列出的设备在当前 CAD 会话里可直接绑定；CAD 没枚举到任何系统打印机时，
    /// 才回退到 Windows 已安装打印机列表。去重后按名称排序。
    /// </summary>
    /// <param name="cadPlotDevices">PlotSettingsValidator.GetPlotDeviceList() 的结果；可为 null。</param>
    public static List<string> GetSystemPrinters(IEnumerable<string>? cadPlotDevices)
    {
        var printers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            var value = (name ?? "").Trim();
            if (value.Length == 0 || !IsSystemPrinterDevice(value) || !seen.Add(value))
            {
                return;
            }

            printers.Add(value);
        }

        if (cadPlotDevices != null)
        {
            foreach (var device in cadPlotDevices)
            {
                Add(device);
            }
        }

        if (printers.Count == 0)
        {
            try
            {
                foreach (string printer in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
                {
                    Add(printer);
                }
            }
            catch
            {
                // 打印后台服务不可用（如被禁用）时忽略，界面会提示先安装打印机。
            }
        }

        printers.Sort((left, right) => string.Compare(left, right, StringComparison.OrdinalIgnoreCase));
        return printers;
    }

    /// <summary>把份数规范到合法范围。</summary>
    public static int NormalizeCopies(int copies)
    {
        if (copies < MinCopies)
        {
            return MinCopies;
        }

        return copies > MaxCopies ? MaxCopies : copies;
    }

    /// <summary>纸质打印完成提示中使用的目标描述，如「HP DesignJet（2 份）」。</summary>
    public static string DescribeTarget(string? printerName, int copies)
    {
        var name = (printerName ?? "").Trim();
        if (name.Length == 0)
        {
            name = "打印机";
        }

        var count = NormalizeCopies(copies);
        return count > 1 ? $"{name}（{count} 份）" : name;
    }

    /// <summary>
    /// 打印机缺少任务图幅时给用户的提示：列出最近的可选纸张，并说明如何自动改纸。
    /// </summary>
    /// <param name="deviceName">打印机名称。</param>
    /// <param name="widthMm">任务纸张宽（毫米）。</param>
    /// <param name="heightMm">任务纸张高（毫米）。</param>
    /// <param name="availableMedia">打印机可用纸张描述，如「A1 594×841」。</param>
    public static string BuildPaperMissingMessage(
        string? deviceName,
        double widthMm,
        double heightMm,
        IEnumerable<string> availableMedia)
    {
        var builder = new StringBuilder();
        builder.Append($"打印机「{deviceName}」没有 {widthMm:0.##} × {heightMm:0.##} mm 的纸张，已停止打印。");
        builder.Append("请先在打印机中配置该幅面，或勾选「纸张不匹配时改用最接近的纸张」。");
        var media = availableMedia?.Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).ToList();
        if (media is { Count: > 0 })
        {
            builder.Append(" 打印机可用纸张: ");
            builder.Append(string.Join("、", media));
        }

        return builder.ToString();
    }
}
