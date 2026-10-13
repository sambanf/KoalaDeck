namespace KoalaDeck.Models;

/// <summary>A COM port present on the system, enriched with USB VID/PID when Windows exposes them.</summary>
public sealed record SerialPortInfo(string PortName, string Description, string? VendorId, string? ProductId, bool IsBluetooth)
{
    /// <summary>How likely this port is an ESP32 board. Higher is better, 0 = unknown, negative = never auto-pick.</summary>
    public int Esp32Score => IsBluetooth
        ? -1
        : VendorId?.ToUpperInvariant() switch
        {
            "303A" => 100, // Espressif native USB (ESP32-S2/S3/C3/C6 USB-CDC or USB-Serial-JTAG)
            "10C4" => 80,  // Silicon Labs CP210x
            "1A86" => 80,  // WCH CH340 / CH343 / CH9102
            "0403" => 50,  // FTDI
            _ => 0,
        };

    public bool IsLikelyEsp32 => Esp32Score > 0;

    public bool IsEspressifNativeUsb => string.Equals(VendorId, "303A", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? PortName : $"{PortName} · {Description}";

    public override string ToString() => DisplayName;
}
