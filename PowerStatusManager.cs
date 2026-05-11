using Meadow.Devices;
using System;
using System.Globalization;
using System.Reflection;

namespace dog_feeder_reminder;

public readonly struct PowerStatusSnapshot
{
    public string BatteryVoltageText { get; }
    public string ChargeStateText { get; }
    public string SourceText { get; }
    public string Notes { get; }

    public PowerStatusSnapshot(string batteryVoltageText, string chargeStateText, string sourceText, string notes)
    {
        BatteryVoltageText = batteryVoltageText;
        ChargeStateText = chargeStateText;
        SourceText = sourceText;
        Notes = notes;
    }
}

public class PowerStatusManager
{
    const string Tag = "PowerStatusManager";
    readonly F7FeatherV2 device;

    public PowerStatusManager(F7FeatherV2 device)
    {
        this.device = device;
    }

    public PowerStatusSnapshot GetSnapshot()
    {
        try
        {
            object batteryInfo = TryInvokeMethod(device, "GetBatteryInfo");
            if (batteryInfo == null)
            {
                return new PowerStatusSnapshot(
                    batteryVoltageText: "Unavailable",
                    chargeStateText: "Unavailable",
                    sourceText: "Unknown",
                    notes: "Battery info API not available on this OS/device build.");
            }

            var voltage = TryGetBatteryVoltage(batteryInfo);
            var chargeState = TryGetChargeStateText(batteryInfo);
            var source = InferSourceText(chargeState);

            var voltageText = voltage.HasValue ? $"{voltage.Value:N2} V" : "Unknown";
            var chargeText = string.IsNullOrWhiteSpace(chargeState) ? "Unknown" : chargeState;

            return new PowerStatusSnapshot(
                batteryVoltageText: voltageText,
                chargeStateText: chargeText,
                sourceText: source,
                notes: "Source is inferred from charge state, not direct USB-vs-battery telemetry.");
        }
        catch (Exception ex)
        {
            Logger.Warn(Tag, $"Unable to read power telemetry: {ex.Message}");
            return new PowerStatusSnapshot(
                batteryVoltageText: "Error",
                chargeStateText: "Error",
                sourceText: "Unknown",
                notes: "Power telemetry read failed.");
        }
    }

    static object TryInvokeMethod(object target, string methodName)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        if (method == null)
        {
            return null;
        }

        return method.Invoke(target, null);
    }

    static double? TryGetBatteryVoltage(object batteryInfo)
    {
        var voltageObj = GetPropertyValue(batteryInfo, "Voltage");
        if (voltageObj == null)
        {
            return null;
        }

        var voltsObj = GetPropertyValue(voltageObj, "Volts");
        if (TryConvertToDouble(voltsObj, out var volts))
        {
            return volts;
        }

        if (TryConvertToDouble(voltageObj, out var directVoltage))
        {
            return directVoltage;
        }

        return null;
    }

    static string TryGetChargeStateText(object batteryInfo)
    {
        var stateObj = GetPropertyValue(batteryInfo, "State")
            ?? GetPropertyValue(batteryInfo, "ChargeState");

        if (stateObj != null)
        {
            return stateObj.ToString();
        }

        var isChargingObj = GetPropertyValue(batteryInfo, "IsCharging");
        if (isChargingObj is bool isCharging)
        {
            return isCharging ? "Charging" : "NotCharging";
        }

        return string.Empty;
    }

    static string InferSourceText(string chargeState)
    {
        if (string.IsNullOrWhiteSpace(chargeState))
        {
            return "Unknown";
        }

        var normalized = chargeState.Trim();
        if (normalized.IndexOf("charging", StringComparison.OrdinalIgnoreCase) >= 0 &&
            normalized.IndexOf("not", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return "External power (inferred)";
        }

        if (normalized.IndexOf("discharg", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("notcharging", StringComparison.OrdinalIgnoreCase) >= 0 ||
            normalized.IndexOf("not charging", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Battery (inferred)";
        }

        return "Unknown";
    }

    static object GetPropertyValue(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(target);
    }

    static bool TryConvertToDouble(object value, out double result)
    {
        if (value == null)
        {
            result = 0;
            return false;
        }

        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            default:
                return double.TryParse(
                    value.ToString(),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out result);
        }
    }
}