// Récepteur de test : se comporte comme le téléphone (annonce + réception + décodage Opus)
// et enregistre le son reçu dans un fichier WAV. Permet de tester le programme Windows
// sans l'application Android, depuis un autre PC ou le même.
//
// Utilisation : CommBluetooth.TestReceiver [--name "Mon récepteur"] [--port 50506] [--out recu.wav]

using Concentus;
using CommBluetooth.Core.Network;
using CommBluetooth.Core.Protocol;
using NAudio.Wave;

string name = $"Récepteur de test ({Environment.MachineName})";
int port = StreamFormat.DefaultAudioPort;
string output = "recu.wav";

for (int i = 0; i + 1 < args.Length; i += 2)
{
    switch (args[i])
    {
        case "--name": name = args[i + 1]; break;
        case "--port": port = int.Parse(args[i + 1]); break;
        case "--out": output = args[i + 1]; break;
    }
}

var decoder = OpusCodecFactory.CreateDecoder(StreamFormat.SampleRate, StreamFormat.Channels);
var pcm = new float[StreamFormat.FrameInterleavedSamples * 6];
var writer = new WaveFileWriter(output, WaveFormat.CreateIeeeFloatWaveFormat(StreamFormat.SampleRate, StreamFormat.Channels));
var gate = new Lock();

uint? session = null;
uint expected = 0;
long received = 0, lost = 0, bytes = 0;

using var receiver = new AudioReceiver(port);
receiver.PacketReceived += (header, payload, from) =>
{
    lock (gate)
    {
        if (header.Type == PacketType.Stop)
        {
            Console.WriteLine($"Fin de session annoncée par {from.Address}.");
            session = null;
            return;
        }

        if (session != header.SessionId)
        {
            Console.WriteLine($"Nouvelle session {header.SessionId:X8} depuis {from.Address}.");
            session = header.SessionId;
            expected = header.Sequence;
            decoder.ResetState();
        }

        if (header.Type != PacketType.Audio)
            return;

        int gap = (int)(header.Sequence - expected);
        if (gap < 0)
            return; // paquet en retard : déjà masqué, on l'ignore

        // Trames perdues : masquage par Opus (au-delà de 5, on repart simplement de zéro).
        if (gap is > 0 and <= 5)
        {
            for (int i = 0; i < gap; i++)
            {
                int concealed = decoder.Decode([], pcm, StreamFormat.FrameSamplesPerChannel, false);
                writer.WriteSamples(pcm, 0, concealed * StreamFormat.Channels);
            }
        }
        lost += gap;

        int samples = decoder.Decode(payload.Span, pcm, pcm.Length / StreamFormat.Channels, false);
        writer.WriteSamples(pcm, 0, samples * StreamFormat.Channels);
        expected = header.Sequence + 1;
        received++;
        bytes += payload.Length + PacketHeader.Size;
    }
};
receiver.Start();

using var announcer = new ReceiverAnnouncer(name, receiver.Port);
Console.WriteLine($"« {name} » attend le son sur le port {receiver.Port}. Écriture dans {Path.GetFullPath(output)}.");
Console.WriteLine("Ctrl+C pour arrêter.");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };

long lastBytes = 0;
while (!stop.Task.IsCompleted)
{
    await Task.WhenAny(stop.Task, Task.Delay(1000));
    lock (gate)
    {
        if (received > 0)
            Console.Write($"\rReçus : {received}  perdus : {lost}  débit : {(bytes - lastBytes) * 8 / 1000} kb/s    ");
        lastBytes = bytes;
    }
}

lock (gate)
    writer.Dispose();
Console.WriteLine();
Console.WriteLine($"Terminé : {received} trames reçues, {lost} perdues.");
