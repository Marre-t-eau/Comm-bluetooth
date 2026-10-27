using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace CommBluetooth.Phone;

/// <summary>
/// Service de premier plan : garde la réception et la lecture actives écran éteint ou application
/// en arrière-plan, avec une notification permanente (exigée par Android) qui permet d'arrêter.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class StreamingService : Service
{
    private const string ChannelId = "streaming";
    private const int NotificationId = 1;
    private const string ActionStop = "io.github.marreteau.commbluetooth.STOP";
    private const string ExtraTargetFrames = "targetFrames";

    public static void Start(Context context, int targetFrames)
    {
        var intent = new Intent(context, typeof(StreamingService)).PutExtra(ExtraTargetFrames, targetFrames);
        ContextCompat.StartForegroundService(context, intent);
    }

    public static void Stop(Context context)
    {
        context.StartService(new Intent(context, typeof(StreamingService)).SetAction(ActionStop));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            PlaybackEngine.Instance.Stop();
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Android exige l'appel à StartForeground dans les 5 secondes suivant le démarrage.
        var notification = BuildNotification();
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        else
            StartForeground(NotificationId, notification);

        int targetFrames = intent?.GetIntExtra(ExtraTargetFrames, 5) ?? 5;
        try
        {
            PlaybackEngine.Instance.Start(this, DeviceName.Get(this), targetFrames);
        }
        catch (Exception)
        {
            // L'erreur est exposée par PlaybackEngine.Error et affichée par l'interface.
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // Si Android tue le service faute de mémoire, il le relancera sans intent : on reprend avec la latence par défaut.
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        PlaybackEngine.Instance.Stop();
        base.OnDestroy();
    }

    private Notification BuildNotification()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && manager.GetNotificationChannel(ChannelId) is null)
        {
            var channel = new NotificationChannel(ChannelId, "Réception du son", NotificationImportance.Low)
            {
                Description = "Affichée pendant que le téléphone reçoit le son du PC.",
            };
            manager.CreateNotificationChannel(channel);
        }

        var openApp = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop),
            PendingIntentFlags.Immutable);
        var stop = PendingIntent.GetService(this, 1,
            new Intent(this, typeof(StreamingService)).SetAction(ActionStop),
            PendingIntentFlags.Immutable);

        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Comm Bluetooth")!
            .SetContentText("Prêt à recevoir le son du PC")!
            .SetSmallIcon(Resource.Mipmap.appicon)!
            .SetOngoing(true)!
            .SetContentIntent(openApp)!
            .AddAction(0, "Arrêter", stop)!
            .SetCategory(NotificationCompat.CategoryService)!
            .Build()!;
    }
}
