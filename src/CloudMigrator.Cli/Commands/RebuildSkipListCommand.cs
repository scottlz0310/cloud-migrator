using System.CommandLine;

namespace CloudMigrator.Cli.Commands;

/// <summary>
/// rebuild-skiplist サブコマンド（廃止済み）。
/// SharePoint は SQLite 状態管理（4フェーズパイプライン）に移行したため、このコマンドは不要です。
/// 転送状態をリセットしてフルリビルドするには <c>transfer --full-rebuild</c> を使用してください。
/// </summary>
internal static class RebuildSkipListCommand
{
    public static Command Build()
    {
        var cmd = new Command(
            "rebuild-skiplist",
            "[廃止済み] SharePoint skip_list を再構築します。transfer --full-rebuild を使用してください。");

        cmd.SetAction((parseResult, ct) =>
        {
            Console.Error.WriteLine(
                "[警告] rebuild-skiplist コマンドは廃止されました。" +
                "SharePoint 移行は SQLite 状態管理に移行しており、skip_list は不要です。" +
                "転送状態をリセットするには 'transfer --full-rebuild' を使用してください。");
            Environment.ExitCode = 1;
            return Task.CompletedTask;
        });

        return cmd;
    }
}
