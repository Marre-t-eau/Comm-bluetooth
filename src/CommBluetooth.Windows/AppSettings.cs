using System.Text.Json;
using CommBluetooth.Core.Protocol;

namespace CommBluetooth.Windows;

/// <summary>Réglages mémorisés entre deux lancements (%AppData%\CommBluetooth\settings.json).</summary>
internal sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommBluetooth", "settings.json");

    /// <summary>Identifiant WASAPI de la sortie capturée ; null = sortie par défaut de Windows.</summary>
    public string? DeviceId { get; set; }

    public int Bitrate { get; set; } = StreamFormat.DefaultBitrate;

    /// <summary>Dernière adresse saisie à la main (« 192.168.1.42 » ou « 192.168.1.42:50506 »).</summary>
    public string? ManualAddress { get; set; }

    /// <summary>Diffuser dès que le dernier téléphone utilisé apparaît sur le réseau.</summary>
    public bool AutoStream { get; set; } = true;

    /// <summary>Nom du dernier téléphone utilisé, pour le resélectionner automatiquement.</summary>
    public string? LastReceiverName { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fichier illisible : on repart des réglages par défaut.
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Les réglages sont un confort : une erreur d'écriture ne doit pas gêner l'utilisateur.
        }
    }
}
