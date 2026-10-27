using System.Net.NetworkInformation;
using System.Net.Sockets;
using Android.Content;
using Android.OS;
using Android.Provider;

namespace CommBluetooth.Phone;

internal static class DeviceName
{
    /// <summary>Nom affiché sur le PC : celui choisi dans les paramètres du téléphone, sinon le modèle.</summary>
    public static string Get(Context context)
    {
        var name = Settings.Global.GetString(context.ContentResolver, "device_name");
        if (string.IsNullOrWhiteSpace(name))
            name = $"{Build.Manufacturer} {Build.Model}";
        return name.Trim();
    }

    /// <summary>Adresse IPv4 du téléphone sur le Wi-Fi, à saisir sur le PC si la détection automatique échoue.</summary>
    public static string? WifiAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.Name.StartsWith("wlan", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
