using CloudMigrator.Cli.Commands;
using FluentAssertions;

namespace CloudMigrator.Tests.Unit;

/// <summary>
/// SetupCommand のユニットテスト。
/// </summary>
public class SetupCommandTests
{
    [Fact]
    public void Build_ShouldReturnCommandNamedSetup()
    {
        // 検証対象: SetupCommand.Build()  目的: コマンド名が "setup" であること
        var cmd = SetupCommand.Build();

        cmd.Name.Should().Be("setup");
    }

    [Fact]
    public void Build_ShouldRegisterFourSubcommands()
    {
        // 検証対象: SetupCommand.Build()  目的: サブコマンドが bootstrap/init/doctor/verify の 4 件であること
        var cmd = SetupCommand.Build();

        cmd.Subcommands.Should().HaveCount(4);
    }

    [Theory]
    [InlineData("bootstrap")]
    [InlineData("init")]
    [InlineData("doctor")]
    [InlineData("verify")]
    public void Build_ShouldContainSubcommand(string name)
    {
        // 検証対象: SetupCommand.Build()  目的: 各サブコマンドが名前で特定できること
        var cmd = SetupCommand.Build();

        cmd.Subcommands.Should().Contain(s => s.Name == name);
    }
}
