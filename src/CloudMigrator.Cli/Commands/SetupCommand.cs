using System.CommandLine;
using CloudMigrator.Setup.Cli.Commands;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// setup サブコマンド。
/// 初期設定（bootstrap/init/doctor/verify）を cloud-migrator.exe から直接実行できるようにまとめたコマンド。
/// </summary>
internal static class SetupCommand
{
    public static Command Build()
    {
        var cmd = new Command("setup", "初期設定・診断・疎通確認を行います");
        cmd.Add(BootstrapCommand.Build());
        cmd.Add(InitCommand.Build());
        cmd.Add(DoctorCommand.Build());
        cmd.Add(VerifyCommand.Build());
        return cmd;
    }
}
