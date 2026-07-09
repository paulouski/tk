namespace Tk.Commands;

public sealed class SwitchCommand : ICommand
{
    private const string MainService = "Claude Code-credentials";
    private const string Slot1Service = "Claude Code-credentials-1";
    private const string Slot2Service = "Claude Code-credentials-2";

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "accounts", "active");

    public string Name => "switch";

    public async Task<int> RunAsync(CommandContext ctx)
    {
        if (ctx.Args.Contains("--help") || ctx.Args.Contains("-h"))
        {
            ctx.Out.WriteLine("Usage: tk switch");
            ctx.Out.WriteLine("  First run:  saves account 1, logs out, opens Claude Code to log in as account 2");
            ctx.Out.WriteLine("  Setup done: saves account 2, restores account 1, opens Claude Code");
            ctx.Out.WriteLine("  After setup: toggles between account 1 and 2, opens Claude Code");
            return 0;
        }

        var state = ReadState();

        return state switch
        {
            "setup" => await FinishSetupAsync(ctx),
            "1" => await ToggleAsync(ctx, from: "1", fromSlot: Slot1Service, toSlot: Slot2Service, toName: "2"),
            "2" => await ToggleAsync(ctx, from: "2", fromSlot: Slot2Service, toSlot: Slot1Service, toName: "1"),
            _ => await StartSetupAsync(ctx),
        };
    }

    // First ever switch: save current creds as slot 1, log out.
    private async Task<int> StartSetupAsync(CommandContext ctx)
    {
        var (exitCode, creds, _) = await ReadKeychainAsync(ctx, MainService);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(creds))
        {
            ctx.Err.WriteLine("tk switch: no active Claude Code session found in Keychain");
            return 1;
        }

        if (await SaveSlotAsync(ctx, Slot1Service, creds.Trim()) != 0) return 1;

        await ctx.Process.RunAsync(["security", "delete-generic-password",
            "-s", MainService, "-a", Environment.UserName]);

        WriteState("setup");
        ctx.Out.WriteLine("Account 1 saved. Opening Claude Code to log in as account 2.");
        return await RunClaudeAsync(ctx);
    }

    // Second switch: user just logged in as account 2 — save it, restore account 1.
    private async Task<int> FinishSetupAsync(CommandContext ctx)
    {
        var (exitCode, creds, _) = await ReadKeychainAsync(ctx, MainService);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(creds))
        {
            ctx.Err.WriteLine("tk switch: no session found — log in with `claude` first");
            return 1;
        }

        if (await SaveSlotAsync(ctx, Slot2Service, creds.Trim()) != 0) return 1;

        var (slot1Exit, slot1Creds, _) = await ReadKeychainAsync(ctx, Slot1Service);
        if (slot1Exit != 0 || string.IsNullOrWhiteSpace(slot1Creds))
        {
            ctx.Err.WriteLine("tk switch: slot 1 credentials missing — restart setup with `tk switch`");
            return 1;
        }

        if (await WriteMainAsync(ctx, slot1Creds.Trim()) != 0) return 1;

        WriteState("1");
        ctx.Out.WriteLine("Account 2 saved. Switched to account 1.");
        return await RunClaudeAsync(ctx);
    }

    // Subsequent switches: save current slot, restore other slot.
    private async Task<int> ToggleAsync(
        CommandContext ctx, string from, string fromSlot, string toSlot, string toName)
    {
        var (curExit, curCreds, _) = await ReadKeychainAsync(ctx, MainService);
        if (curExit == 0 && !string.IsNullOrWhiteSpace(curCreds))
            await SaveSlotAsync(ctx, fromSlot, curCreds.Trim());

        var (toExit, toCreds, _) = await ReadKeychainAsync(ctx, toSlot);
        if (toExit != 0 || string.IsNullOrWhiteSpace(toCreds))
        {
            ctx.Err.WriteLine($"tk switch: slot {toName} credentials missing");
            return 1;
        }

        if (await WriteMainAsync(ctx, toCreds.Trim()) != 0) return 1;

        WriteState(toName);
        ctx.Out.WriteLine($"Switched to account {toName}.");
        return await RunClaudeAsync(ctx);
    }

    private static async Task<int> RunClaudeAsync(CommandContext ctx)
    {
        ctx.Out.Flush();
        ctx.Err.Flush();

        try
        {
            return await ctx.Process.RunInteractiveAsync(["claude"]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ctx.Err.WriteLine($"tk switch: failed to start claude: {ex.Message}");
            return 1;
        }
    }

    private Task<(int, string, string)> ReadKeychainAsync(CommandContext ctx, string service) =>
        ctx.Process.RunAsync(["security", "find-generic-password", "-s", service, "-w"]);

    private async Task<int> SaveSlotAsync(CommandContext ctx, string slotService, string creds)
    {
        var (exit, _, err) = await ctx.Process.RunAsync(
            ["security", "add-generic-password", "-U",
             "-s", slotService, "-a", Environment.UserName, "-w", creds]);
        if (exit != 0)
            ctx.Err.WriteLine($"tk switch: failed to save to Keychain slot: {err.Trim()}");
        return exit;
    }

    private async Task<int> WriteMainAsync(CommandContext ctx, string creds)
    {
        var (exit, _, err) = await ctx.Process.RunAsync(
            ["security", "add-generic-password", "-U",
             "-s", MainService, "-a", Environment.UserName, "-w", creds]);
        if (exit != 0)
            ctx.Err.WriteLine($"tk switch: failed to update Keychain: {err.Trim()}");
        return exit;
    }

    private static string ReadState() =>
        File.Exists(StateFile) ? File.ReadAllText(StateFile).Trim() : string.Empty;

    private static void WriteState(string state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
        File.WriteAllText(StateFile, state);
    }
}
