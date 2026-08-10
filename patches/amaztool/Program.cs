namespace AmazTool;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var pauseOnError = args.Length == 0 ||
            !string.Equals(args[0], "validateupdate", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (args.Length == 0)
            {
                ShowHelp();
                return;
            }

            foreach (var arg in args)
            {
                Console.WriteLine(arg);
            }

            switch (args[0].ToLowerInvariant())
            {
                case "rebootas":
                    HandleReboot();
                    break;

                case "help":
                case "--help":
                case "-h":
                case "/?":
                    ShowHelp();
                    break;

                case "validateupdate":
                    if (args.Length != 2)
                    {
                        throw new ArgumentException("validateupdate requires exactly one ZIP path.");
                    }
                    UpgradeApp.ValidateArchive(Uri.UnescapeDataString(args[1]));
                    break;

                default:
                    var upgradeData = Uri.UnescapeDataString(string.Join(" ", args));
                    HandleUpgrade(upgradeData);
                    break;
            }

            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            Console.Error.WriteLine($"Upgrade failed with exit code {Environment.ExitCode}.");
            Console.Error.WriteLine(ex);
            if (pauseOnError)
            {
                Console.WriteLine("Press any key to close this window...");
                if (!Console.IsInputRedirected)
                {
                    Console.ReadKey(true);
                }
            }
        }
    }

    private static void ShowHelp()
    {
        Console.WriteLine(Resx.Resource.Guidelines);
        Console.WriteLine("Available commands:");
        Console.WriteLine("  rebootas             - Restart the application");
        Console.WriteLine("  validateupdate ZIP   - Validate and display update path mappings");
        Console.WriteLine("  help                 - Display this help information");
        Thread.Sleep(5000);
    }

    private static void HandleReboot()
    {
        Console.WriteLine("Restarting application...");
        Thread.Sleep(1000);
        Utils.StartV2RayN();
    }

    private static void HandleUpgrade(string upgradeData)
    {
        Console.WriteLine("Upgrading application...");
        UpgradeApp.Upgrade(upgradeData);
    }
}
