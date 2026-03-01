using System.CommandLine;
using CloudMigrator.Cli.Commands;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var rootCmd = new RootCommand("CloudMigrator - OneDrive から SharePoint へのファイル移行ツール");
rootCmd.Add(TransferCommand.Build());
rootCmd.Add(RebuildSkipListCommand.Build());

return await rootCmd.Parse(args).InvokeAsync(new InvocationConfiguration(), cts.Token);

