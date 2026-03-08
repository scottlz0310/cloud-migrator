using System.CommandLine;
using CloudMigrator.Setup.Cli.Commands;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var rootCmd = new RootCommand("CloudMigrator Setup - 初期設定診断・テンプレート生成・疎通確認ツール");
rootCmd.Add(BootstrapCommand.Build());
rootCmd.Add(DoctorCommand.Build());
rootCmd.Add(InitCommand.Build());
rootCmd.Add(VerifyCommand.Build());

return await rootCmd.Parse(args).InvokeAsync(new InvocationConfiguration(), cts.Token);
