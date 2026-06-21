using System.Text;

namespace KioskTerm;

internal static class Program
{
    [STAThread]
    private static int Main(string[] rawArgs)
    {
        // Usage:
        //   kioskterm.exe [--header "Text to show up top"] -- <command> [args...]
        //
        // Everything after the "--" separator is the command line that gets run
        // inside the pseudo-console. If there is no "--", the first non-flag token
        // and everything after it is treated as the command.
        string? header = null;
        string? logoPath = null;
        bool minimizeOthers = false;
        bool keepAwake = true;   // default: keep the machine awake + display on while provisioning
        bool allowInput = false; // default: display-only, no user input
        bool testMode = false;   // safety harness: titled window, taskbar shown, no key blocking
        bool showOnOutputOnly = false; // stay hidden until the command produces real output
        bool hidden = false;           // run the command fully hidden — never show anything
        var command = new List<string>();

        int i = 0;
        bool inCommand = false;
        while (i < rawArgs.Length)
        {
            string a = rawArgs[i];
            if (!inCommand && a == "--")
            {
                inCommand = true;
                i++;
                continue;
            }
            if (!inCommand && (a == "--minimize-others" || a == "--minimize-all" || a == "-m"))
            {
                minimizeOthers = true;
                i++; continue;
            }
            if (!inCommand && a == "--allow-sleep")
            {
                keepAwake = false;
                i++; continue;
            }
            if (!inCommand && (a == "--allow-input" || a == "--input"))
            {
                allowInput = true;
                i++; continue;
            }
            if (!inCommand && a == "--test")
            {
                testMode = true;
                i++; continue;
            }
            if (!inCommand && a == "--show-on-output-only")
            {
                showOnOutputOnly = true;
                i++; continue;
            }
            if (!inCommand && a == "--hidden")
            {
                hidden = true;
                i++; continue;
            }
            if (!inCommand && (a == "--header" || a == "-h"))
            {
                if (i + 1 < rawArgs.Length) { header = rawArgs[i + 1]; i += 2; continue; }
                i++; continue;
            }
            if (!inCommand && a.StartsWith("--header="))
            {
                header = a.Substring("--header=".Length);
                i++; continue;
            }
            if (!inCommand && (a == "--logo" || a == "-l"))
            {
                if (i + 1 < rawArgs.Length) { logoPath = rawArgs[i + 1]; i += 2; continue; }
                i++; continue;
            }
            if (!inCommand && a.StartsWith("--logo="))
            {
                logoPath = a.Substring("--logo=".Length);
                i++; continue;
            }
            // first bare token starts the command
            inCommand = true;
            command.Add(a);
            i++;
        }

        if (command.Count == 0)
        {
            System.Windows.Forms.MessageBox.Show(
                "KioskTerm — fullscreen, rear-most, input-locked terminal overlay.\n\n" +
                "Usage:\n  kioskterm.exe [--header \"Caption text\"] [--minimize-others] -- <command> [args...]\n\n" +
                "Options:\n" +
                "  --header, -h <text>     Caption shown in the reserved header area.\n" +
                "  --logo, -l <path>       Image shown top-right, sized to the header height.\n" +
                "  --minimize-others, -m   Minimize all other windows on launch (handy for testing).\n" +
                "  --allow-sleep           Allow normal sleep/display timeout (default: kept awake).\n" +
                "  --allow-input           Accept keyboard input (focusable terminal); blocks Ctrl combos.\n" +
                "  --test                  Safe testing: titled window, taskbar shown, no key blocking.\n" +
                "  --show-on-output-only   Stay hidden until the command emits real output; a\n" +
                "                          do-nothing run exits without ever showing the overlay.\n" +
                "  --hidden                Run the command fully hidden: no window, no taskbar/key\n" +
                "                          changes. For silent startup tasks (no console flash).\n\n" +
                "Example:\n  kioskterm.exe --header \"Configuring Windows for first use...\" -- " +
                "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\setup.ps1",
                "KioskTerm", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Information);
            return 1;
        }

        string commandLine = BuildCommandLine(command);

        if (minimizeOthers)
        {
            // Done before the overlay window exists so it isn't minimized too;
            // give the shell a moment to process it before we show the overlay.
            Native.MinimizeAllWindows();
            System.Threading.Thread.Sleep(300);
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        using var form = new MainForm(header, commandLine, keepAwake, logoPath, allowInput, testMode, showOnOutputOnly, hidden);
        System.Windows.Forms.Application.Run(form);
        return form.ExitCode;
    }

    // Reconstruct a single command-line string from the already-split tokens,
    // re-quoting any token that contains whitespace or quotes (Windows rules).
    private static string BuildCommandLine(List<string> tokens)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(QuoteArg(tokens[i]));
        }
        return sb.ToString();
    }

    private static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return arg;

        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }
}
