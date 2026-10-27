namespace CommBluetooth.Windows;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, @"Local\CommBluetooth", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("Comm Bluetooth est déjà ouvert (regardez près de l'horloge).", "Comm Bluetooth",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startHidden: args.Contains(StartupRegistration.MinimizedArgument)));
    }
}
