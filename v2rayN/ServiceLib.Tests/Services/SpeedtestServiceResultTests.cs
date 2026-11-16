namespace ServiceLib.Tests.Services;

public class SpeedtestServiceResultTests
{
    [Test]
    [Arguments(ESpeedActionType.Speedtest, 0)]
    [Arguments(ESpeedActionType.Mixedtest, 0)]
    [Arguments(ESpeedActionType.Realping, 12.5)]
    public async Task StartingATest_ShouldClearTheLastSpeedOfSpeedTests(ESpeedActionType actionType, double expected)
    {
        var indexId = Guid.NewGuid().ToString();
        ProfileExManager.Instance.SetTestSpeed(indexId, 12.5m);

        await SetTestResult([new() { IndexId = indexId }], actionType, ResUI.Speedtesting);

        var profileEx = (await ProfileExManager.Instance.GetProfileExs()).First(t => t.IndexId == indexId);
        await profileEx.Speed.Should().BeEqualTo((decimal)expected);
    }

    [Test]
    [Arguments(ESpeedActionType.Tcping)]
    [Arguments(ESpeedActionType.Realping)]
    [Arguments(ESpeedActionType.Speedtest)]
    [Arguments(ESpeedActionType.Mixedtest)]
    [Arguments(ESpeedActionType.UdpTest)]
    public async Task StartingATest_ShouldResetAnOldFailedDelayToUntested(ESpeedActionType actionType)
    {
        // 构造上一轮失败记录；新一轮还没结果时必须按未测保留。
        var indexId = Guid.NewGuid().ToString();
        ProfileExManager.Instance.SetTestDelay(indexId, -1);

        await SetTestResult([new() { IndexId = indexId }], actionType, ResUI.Speedtesting);

        var profileEx = (await ProfileExManager.Instance.GetProfileExs()).First(t => t.IndexId == indexId);
        await profileEx.Delay.Should().BeEqualTo(0);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(20)]
    public async Task CancellingATest_ShouldPreserveDelayResultsAlreadyReturned(int delay)
    {
        // 取消只更新提示和未完成的速度，不能把本轮已返回的延迟结果改写成未测。
        var indexId = Guid.NewGuid().ToString();
        ProfileExManager.Instance.SetTestDelay(indexId, delay);

        await SetTestResult([new() { IndexId = indexId }], ESpeedActionType.Mixedtest, ResUI.SpeedtestingSkip, resetDelay: false);

        var profileEx = (await ProfileExManager.Instance.GetProfileExs()).First(t => t.IndexId == indexId);
        await profileEx.Delay.Should().BeEqualTo(delay);
    }

    private static Task SetTestResult(List<ServerTestItem> items, ESpeedActionType actionType, string message,
        bool resetDelay = true)
    {
        var service = new SpeedtestService(new Config { SpeedTestItem = new() }, _ => Task.CompletedTask);
        var method = typeof(SpeedtestService).GetMethod("SetTestResultAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(service, [items, actionType, message, resetDelay])!;
    }
}
