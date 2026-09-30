using System.Text;
using SafePatch.Authoring.Sources;
using SafePatch.Cli;

if (OperatingSystem.IsWindows())
{
    // With --mo2, run inside MO2's virtual file system: restart through MO2 and relay this console to that copy.
    if (Mo2Relay.Needed(args))
    {
        try
        {
            return Mo2Relay.Run(Mo2Relay.Option(args, "--mo2")!, Mo2Relay.Option(args, "--profile"), Mo2Relay.Option(args, "--mo2-exe"), args,
                Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.OpenStandardError());
        }
        catch (SafePatch.Host.SafePatchException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
    if (Mo2Relay.Option(args, Mo2Relay.PipeOption) is { } pipe)
    {
        using var relay = Mo2Relay.Connect(pipe);
        using var output = new StreamWriter(relay.Output, new UTF8Encoding(false)) { AutoFlush = true };
        using var error = new StreamWriter(relay.Error, new UTF8Encoding(false)) { AutoFlush = true };
        var code = CliApp.Run(args, output, error);
        relay.Exit(code);
        return code;
    }
}
return CliApp.Run(args, Console.Out, Console.Error);
